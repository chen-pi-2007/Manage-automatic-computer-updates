using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests;

public sealed class LeftoverScannerTests
{
    private sealed class FakeFiles : IFileProbe
    {
        public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> Children { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, long?> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool DirectoryExists(string path) => Dirs.Contains(path.TrimEnd('\\'));
        public bool FileExists(string path) => Files.Contains(path);
        public IReadOnlyList<string> GetChildDirectories(string parent) =>
            Children.TryGetValue(parent.TrimEnd('\\'), out var c) ? c : [];
        public long? DirectorySize(string path) => Sizes.TryGetValue(path.TrimEnd('\\'), out var s) ? s : 0;
    }

    private sealed class FakeRegistry : IRegistryProbe
    {
        public HashSet<string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool KeyExists(string path) => Keys.Contains(path);
    }

    private static UninstallEntry Entry(string name, string? installLoc) => new(
        name, UninstallHive.LocalMachine64, name, "1.0", "Pub", installLoc,
        null, null, false, null, null, null);

    private static SoftwareGroup Group(string name, string? installLoc, string? publisher = "Pub") =>
        new() { Name = name, Publisher = publisher, Primary = Entry(name, installLoc) };

    private static ScanResult Result(params SoftwareGroup[] groups) => new(groups, [], groups.Length);

    [Fact]
    public void Lists_existing_install_dir_as_owned()
    {
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Apps\Foo");
        files.Sizes[@"C:\Apps\Foo"] = 5000;
        var g = Group("Foo", @"C:\Apps\Foo");
        var items = new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g));

        var dir = Assert.Single(items, i => i.Type == LeftoverType.InstallDir);
        Assert.Equal(LeftoverCategory.Owned, dir.Category);
        Assert.True(dir.DefaultChecked);
        Assert.Equal(5000, dir.SizeBytes);
    }

    [Fact]
    public void Missing_paths_are_skipped()
    {
        var files = new FakeFiles();   // C:\Apps\Foo 不存在
        var g = Group("Foo", @"C:\Apps\Foo");
        Assert.Empty(new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g)));
    }

    [Fact]
    public void System_paths_are_never_listed()
    {
        var files = new FakeFiles();
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        files.Dirs.Add(win.TrimEnd('\\'));
        var g = Group("Bad", win);
        Assert.Empty(new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g)));
    }

    [Fact]
    public void Shared_path_detected_from_other_software()
    {
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Shared\Tencent");
        files.Dirs.Add(@"C:\Shared\Tencent\QQ");
        // 另一个软件（微信）装在同一个父目录下
        var qq = Group("QQ", @"C:\Shared\Tencent\QQ");
        var wechat = Group("WeChat", @"C:\Shared\Tencent\WeChat");
        var rule = new Rule("t", "T", new RuleMatch("QQ", null), null, null, [],
            [new LeftoverRule(@"C:\Shared\Tencent", LeftoverKind.Owned)]);
        var items = new LeftoverScanner(files, new FakeRegistry()).Scan(qq, rule, Result(qq, wechat));

        var shared = Assert.Single(items, i => i.Path.TrimEnd('\\').Equals(@"C:\Shared\Tencent", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(LeftoverCategory.Shared, shared.Category);
        Assert.False(shared.DefaultChecked);
    }

    [Fact]
    public void Rule_personal_path_is_red_and_unchecked()
    {
        var files = new FakeFiles();
        var appdata = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        files.Dirs.Add(Path.Combine(appdata, "FooChats").TrimEnd('\\'));
        var g = Group("Foo", null);
        var rule = new Rule("f", "Foo", new RuleMatch("Foo", null), null, null, [],
            [new LeftoverRule(@"%APPDATA%\FooChats", LeftoverKind.Personal)]);
        var items = new LeftoverScanner(files, new FakeRegistry()).Scan(g, rule, Result(g));

        var personal = Assert.Single(items, i => i.Category == LeftoverCategory.Personal);
        Assert.False(personal.DefaultChecked);
        Assert.Contains("个人数据", personal.Note);
    }

    [Fact]
    public void Registry_leftover_listed_only_when_key_exists()
    {
        var reg = new FakeRegistry();
        reg.Keys.Add(@"HKCU\Software\Foo");
        var g = Group("Foo", null);
        var rule = new Rule("f", "Foo", new RuleMatch("Foo", null), null, null, [],
            [new LeftoverRule(@"HKCU\Software\Foo", LeftoverKind.Owned),
             new LeftoverRule(@"HKCU\Software\Missing", LeftoverKind.Owned)]);
        var items = new LeftoverScanner(new FakeFiles(), reg).Scan(g, rule, Result(g));

        var key = Assert.Single(items, i => i.Type == LeftoverType.RegistryKey);
        Assert.Equal(@"HKCU\Software\Foo", key.Path);
    }

    [Fact]
    public void Background_items_become_leftovers()
    {
        var g = Group("Foo", null);
        g.Background.Add(new BackgroundItem(BackgroundKind.Service, "FooSvc", null, null, @"C:\Apps\Foo\svc.exe", null));
        g.Background.Add(new BackgroundItem(BackgroundKind.ScheduledTask, "FooTask", null, null, null, null));
        var items = new LeftoverScanner(new FakeFiles(), new FakeRegistry()).Scan(g, null, Result(g));

        Assert.Contains(items, i => i.Type == LeftoverType.Service && i.Path.Contains("FooSvc"));
        Assert.Contains(items, i => i.Type == LeftoverType.ScheduledTask && i.Path.Contains("FooTask"));
        Assert.All(items.Where(i => i.Type is LeftoverType.Service or LeftoverType.ScheduledTask),
            i => Assert.True(i.DefaultChecked));
    }

    [Fact]
    public void Data_dir_name_match_is_possible()
    {
        var files = new FakeFiles();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');
        files.Children[local] = [Path.Combine(local, "FooBar"), Path.Combine(local, "Unrelated")];
        files.Dirs.Add(Path.Combine(local, "FooBar"));
        files.Dirs.Add(Path.Combine(local, "Unrelated"));
        var g = Group("FooBar", null);
        var items = new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g));

        var hit = Assert.Single(items, i => i.Path.Contains("FooBar"));
        Assert.Equal(LeftoverCategory.Possible, hit.Category);
        Assert.DoesNotContain(items, i => i.Path.Contains("Unrelated"));
    }

    [Fact]
    public void Size_failure_is_unknown()
    {
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Apps\Foo");
        files.Sizes[@"C:\Apps\Foo"] = null;   // 算不出
        var g = Group("Foo", @"C:\Apps\Foo");
        var item = Assert.Single(new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g)),
            i => i.Type == LeftoverType.InstallDir);
        Assert.Null(item.SizeBytes);
    }

    [Fact]
    public void Rule_personal_on_install_location_wins_over_owned()
    {
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Apps\Foo");
        var g = Group("Foo", @"C:\Apps\Foo");
        var rule = new Rule("f", "Foo", new RuleMatch("Foo", null), null, null, [],
            [new LeftoverRule(@"C:\Apps\Foo", LeftoverKind.Personal)]);
        var items = new LeftoverScanner(files, new FakeRegistry()).Scan(g, rule, Result(g));
        var item = Assert.Single(items);
        Assert.Equal(LeftoverCategory.Personal, item.Category);
        Assert.False(item.DefaultChecked);
    }

    [Fact]
    public void Unrelated_other_software_does_not_make_dir_shared()
    {
        var files = new FakeFiles();
        files.Dirs.Add(@"C:\Apps\Foo");
        var g = Group("Foo", @"C:\Apps\Foo");
        var other = Group("Other", @"C:\Apps\Other");
        var item = Assert.Single(new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g, other)));
        Assert.Equal(LeftoverCategory.Owned, item.Category);
    }

    [Fact]
    public void Too_short_name_does_not_match_data_dirs()
    {
        var files = new FakeFiles();
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData).TrimEnd('\\');
        files.Children[local] = [Path.Combine(local, "ABCDEF")];
        files.Dirs.Add(Path.Combine(local, "ABCDEF"));
        var g = Group("XY", null, publisher: "AB");
        Assert.Empty(new LeftoverScanner(files, new FakeRegistry()).Scan(g, null, Result(g)));
    }

    [Fact]
    public void Registry_under_microsoft_is_not_listed()
    {
        var reg = new FakeRegistry();
        reg.Keys.Add(@"HKLM\SOFTWARE\Microsoft\Foo");
        var g = Group("Foo", null);
        var rule = new Rule("f", "Foo", new RuleMatch("Foo", null), null, null, [],
            [new LeftoverRule(@"HKLM\SOFTWARE\Microsoft\Foo", LeftoverKind.Owned)]);
        Assert.Empty(new LeftoverScanner(new FakeFiles(), reg).Scan(g, rule, Result(g)));
    }

    [Fact]
    public void Long_hive_name_is_normalized()
    {
        var reg = new FakeRegistry();
        reg.Keys.Add(@"HKCU\Software\Foo");
        var g = Group("Foo", null);
        var rule = new Rule("f", "Foo", new RuleMatch("Foo", null), null, null, [],
            [new LeftoverRule(@"HKEY_CURRENT_USER\Software\Foo", LeftoverKind.Owned)]);
        var key = Assert.Single(new LeftoverScanner(new FakeFiles(), reg).Scan(g, rule, Result(g)));
        Assert.Equal(@"HKCU\Software\Foo", key.Path);
    }

    [Fact]
    public void Relative_or_unexpanded_rule_path_is_skipped()
    {
        var files = new FakeFiles();
        var g = Group("Foo", null);
        var rule = new Rule("f", "Foo", new RuleMatch("Foo", null), null, null, [],
            [new LeftoverRule(@"%NOPE_UNDEFINED%\Foo", LeftoverKind.Owned),
             new LeftoverRule(@"relative\Foo", LeftoverKind.Owned)]);
        Assert.Empty(new LeftoverScanner(files, new FakeRegistry()).Scan(g, rule, Result(g)));
    }
}
