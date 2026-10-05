using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Scanning;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class SoftwareGrouperTests
{
    private static ScanResult Group(params UninstallEntry[] entries) => SoftwareGrouper.Group(entries, []);

    private static SoftwareGroup Find(ScanResult r, string name) => r.Groups.Single(g => g.Name == name);

    [Fact]
    public void Duplicates_across_hives_become_one_group()   // Review Focus 4
    {
        var r = Group(
            Entry("7-Zip", "Igor Pavlov", "24.08", hive: UninstallHive.LocalMachine64),
            Entry("7-Zip", "Igor Pavlov", "24.08", hive: UninstallHive.LocalMachine32, key: "7zip-32"));
        Assert.Single(r.Groups);
        Assert.Equal(2, r.TotalEntries);
    }

    [Fact]
    public void Same_name_different_versions_stay_separate()   // 运行库多版本共存
    {
        var r = Group(
            Entry("Microsoft Windows Desktop Runtime - 8.0.14 (x64)", "Microsoft Corporation", "8.0.14"),
            Entry("Microsoft Windows Desktop Runtime - 8.0.17 (x64)", "Microsoft Corporation", "8.0.17"));
        Assert.Equal(2, r.Groups.Count);
    }

    [Fact]
    public void Hidden_entry_attaches_by_parent_key()
    {
        var r = Group(
            Entry("Office", "Microsoft Corporation", key: "Office16"),
            Entry("Office 校对工具", "Microsoft Corporation", hidden: true, parentKey: "Office16"));
        var office = Find(r, "Office");
        Assert.Single(office.Components);
        Assert.Single(r.Groups);
    }

    [Fact]
    public void Hidden_entry_attaches_by_install_location()
    {
        var r = Group(
            Entry("Foo", "Foo Inc", location: @"C:\Apps\Foo\"),
            Entry("Bar Helper", "Other", hidden: true, location: @"C:\Apps\Foo\helper"));
        Assert.Single(Find(r, "Foo").Components);
    }

    [Fact]
    public void Install_location_prefix_respects_folder_boundary()
    {
        var r = Group(
            Entry("A", "X", location: @"C:\Apps\A"),
            Entry("AB part", "Y", hidden: true, location: @"C:\Apps\AB"));
        Assert.Empty(Find(r, "A").Components);   // C:\Apps\AB 不在 C:\Apps\A 里面
    }

    [Fact]
    public void Python_components_go_to_matching_python_not_launcher()
    {
        const string psf = "Python Software Foundation";
        var r = Group(
            Entry("Python 3.10.2 (64-bit)", psf, "3.10.2150.0"),
            Entry("Python Launcher", psf, "3.10.7686.0"),
            Entry("Python 3.10.2 Core Interpreter (64-bit)", psf, hidden: true),
            Entry("Python 3.10.2 pip Bootstrap (64-bit)", psf, hidden: true));
        Assert.Equal(2, Find(r, "Python 3.10.2 (64-bit)").Components.Count);
        Assert.Empty(Find(r, "Python Launcher").Components);
    }

    [Fact]
    public void Cuda_components_go_to_cuda_toolkit_not_nvidia_app()
    {
        const string nv = "NVIDIA Corporation";
        var r = Group(
            Entry("NVIDIA CUDA Toolkit 13.0", nv, "13.0"),
            Entry("NVIDIA App 11.0.7.247", nv, "11.0.7.247"),
            Entry("NVIDIA CUDA Runtime 13.0", nv, hidden: true),
            Entry("CUBLAS Runtime", nv, hidden: true));

        Assert.Single(Find(r, "NVIDIA CUDA Toolkit 13.0").Components);
        Assert.Empty(Find(r, "NVIDIA App 11.0.7.247").Components);

        var leftovers = Find(r, "NVIDIA Corporation 组件");     // CUBLAS 找不到主人
        Assert.Equal(SoftwareCategory.SystemComponent, leftovers.Category);
        Assert.Null(leftovers.Primary);
        Assert.Single(leftovers.Components);
    }

    [Fact]
    public void Orphan_without_publisher_goes_to_unknown_collection()
    {
        var r = Group(Entry("mystery", publisher: null, hidden: true));
        Assert.Equal("未知发布者的组件", Assert.Single(r.Groups).Name);
    }

    [Fact]
    public void Patch_is_not_a_primary()
    {
        var r = Group(
            Entry("Foo", "Foo Inc", key: "Foo"),
            Entry("Foo 安全更新 KB123", "Foo Inc", parentKey: "Foo"));   // 不隐藏但带 ParentKeyName
        Assert.Single(r.Groups);
        Assert.Single(Find(r, "Foo").Components);
    }

    [Theory]
    [InlineData("${{arpDisplayName}}", true)]
    [InlineData("{{name}}", true)]
    [InlineData("QQ", false)]
    public void Placeholder_names_are_detected(string name, bool expected)
        => Assert.Equal(expected, SoftwareGrouper.IsPlaceholderName(name));

    [Fact]
    public void Visible_placeholder_group_is_marked_incomplete()
    {
        var r = Group(Entry("${{arpDisplayName}}", "NVIDIA Corporation"));
        Assert.True(Assert.Single(r.Groups).IsIncomplete);
    }

    [Fact]
    public void Background_attaches_to_deepest_install_location()
    {
        var entries = new[]
        {
            Entry("Tencent 公共组件", "Tencent", location: @"C:\Program Files\Tencent"),
            Entry("QQ", "Tencent", location: @"C:\Program Files\Tencent\QQNT\"),
        };
        var bg = new[]
        {
            new BackgroundItem(BackgroundKind.RunKey, "QQNT", null, "x", @"C:\Program Files\Tencent\QQNT\QQ.exe", null),
            new BackgroundItem(BackgroundKind.Service, "Unknown", null, "y", @"D:\Other\svc.exe", null),
            new BackgroundItem(BackgroundKind.StartupFolder, "NoPath", null, "z", null, null),
        };

        var r = SoftwareGrouper.Group(entries, bg);

        Assert.Single(Find(r, "QQ").Background);
        Assert.Empty(Find(r, "Tencent 公共组件").Background);
        Assert.Equal(2, r.UnassignedBackground.Count);
    }

    [Theory]
    [InlineData(@"C:\Apps\Foo\bar.exe", @"C:\Apps\Foo", true)]
    [InlineData(@"c:\apps\foo\bar.exe", @"C:\Apps\Foo\", true)]
    [InlineData(@"C:\Apps\FooBar\x.exe", @"C:\Apps\Foo", false)]
    [InlineData(@"C:\Apps\Foo\x.exe", null, false)]
    [InlineData(null, @"C:\Apps", false)]
    [InlineData("C:\\bad|path", @"C:\", false)]   // 非法字符不抛异常
    public void IsUnder(string? path, string? dir, bool expected)
        => Assert.Equal(expected, PathUtil.IsUnder(path, dir));
}
