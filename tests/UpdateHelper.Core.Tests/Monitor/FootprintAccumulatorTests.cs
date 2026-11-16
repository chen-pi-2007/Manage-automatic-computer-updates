using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Core.Tests.Monitor;

public class FootprintAccumulatorTests
{
    private const int Root = 100;
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset T(int s) => T0.AddSeconds(s);

    // 固定落点基准，不依赖本机真实目录
    private static readonly KnownFolders F = new(
        ProgramFiles: @"C:\Program Files", ProgramFilesX86: @"C:\Program Files (x86)",
        ProgramData: @"C:\ProgramData", LocalAppData: @"C:\Users\u\AppData\Local",
        RoamingAppData: @"C:\Users\u\AppData\Roaming", UserTemp: @"C:\Users\u\AppData\Local\Temp",
        Windows: @"C:\Windows");

    private const string KeepFile = @"C:\Program Files\Foo\a.exe";

    // 假复验：默认一切都在；可指定哪些路径已不存在
    private sealed class FakeProbe : IFootprintProbe
    {
        public HashSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool StillExists(FootprintEntry e) => !Missing.Contains(e.Path);
    }

    private static FootprintAccumulator NewAcc()
    {
        var tree = new ProcessTree(Root, DelegateImages.Default);
        return new FootprintAccumulator(tree, T(0), Root, @"C:\setup.exe", F);
    }

    [Fact]
    public void Create_then_delete_is_dropped()
    {
        var acc = NewAcc();
        acc.Observe(new FileEvent(Root, KeepFile, true, T(1)));
        acc.Observe(new FileEvent(Root, KeepFile, false, T(2)));
        var fp = acc.Finalize("id", new FakeProbe(), T(10));
        Assert.Empty(fp.Entries);
    }

    [Fact]
    public void Created_and_survives_is_kept()
    {
        var acc = NewAcc();
        acc.Observe(new FileEvent(Root, KeepFile, true, T(1)));
        var fp = acc.Finalize("id", new FakeProbe(), T(10));
        var e = Assert.Single(fp.Entries);
        Assert.True(e.IsNew);
        Assert.Equal(FootprintConfidence.Certain, e.Confidence);
        Assert.Equal(KeepFile, e.Path);
    }

    [Fact]
    public void Nonexistent_at_finalize_is_dropped()
    {
        var acc = NewAcc();
        acc.Observe(new FileEvent(Root, KeepFile, true, T(1)));
        var probe = new FakeProbe();
        probe.Missing.Add(KeepFile);
        Assert.Empty(acc.Finalize("id", probe, T(10)).Entries);
    }

    [Fact]
    public void Outside_pid_action_not_recorded()
    {
        var acc = NewAcc();
        acc.Observe(new FileEvent(999, KeepFile, true, T(1)));
        Assert.Empty(acc.Finalize("id", new FakeProbe(), T(10)).Entries);
    }

    [Fact]
    public void Delegated_action_is_possible()
    {
        var acc = NewAcc();
        acc.Observe(new ProcessStartEvent(300, 1, @"C:\Windows\System32\msiexec.exe", T(1)));
        acc.Observe(new FileEvent(300, KeepFile, true, T(2)));
        var e = Assert.Single(acc.Finalize("id", new FakeProbe(), T(10)).Entries);
        Assert.Equal(FootprintConfidence.Possible, e.Confidence);
    }

    [Fact]
    public void Ambiguous_location_is_possible()
    {
        var acc = NewAcc();
        acc.Observe(new FileEvent(Root, @"D:\Games\Foo\a.exe", true, T(1)));
        var e = Assert.Single(acc.Finalize("id", new FakeProbe(), T(10)).Entries);
        Assert.Equal(FootprintConfidence.Possible, e.Confidence);
    }

    [Fact]
    public void Repeated_writes_dedup_to_one()
    {
        var acc = NewAcc();
        acc.Observe(new FileEvent(Root, KeepFile, true, T(1)));
        acc.Observe(new FileEvent(Root, KeepFile, true, T(2)));
        acc.Observe(new FileEvent(Root, KeepFile.ToUpperInvariant(), true, T(3)));
        var fp = acc.Finalize("id", new FakeProbe(), T(10));
        Assert.Single(fp.Entries);
    }

    [Fact]
    public void Events_lost_marks_incomplete()
    {
        var acc = NewAcc();
        acc.Observe(new FileEvent(Root, KeepFile, true, T(1)));
        acc.RecordEventsLost(5);
        var fp = acc.Finalize("id", new FakeProbe(), T(10));
        Assert.False(fp.Completeness.IsComplete);
        Assert.Contains(fp.Completeness.Reasons, r => r.Contains("丢事件"));
    }

    [Fact]
    public void Clean_finalize_is_complete()
    {
        var acc = NewAcc();
        acc.Observe(new FileEvent(Root, KeepFile, true, T(1)));
        var fp = acc.Finalize("id", new FakeProbe(), T(10));
        Assert.True(fp.Completeness.IsComplete);
        Assert.Empty(fp.Completeness.Reasons);
    }

    [Fact]
    public void Early_stop_marks_incomplete()
    {
        var acc = NewAcc();
        acc.RecordEarlyStop("用户手动停止");
        var fp = acc.Finalize("id", new FakeProbe(), T(10));
        Assert.False(fp.Completeness.IsComplete);
        Assert.Contains(fp.Completeness.Reasons, r => r.Contains("用户手动停止"));
    }

    // —— Ruling 补充 ——

    [Fact]
    public void Volume_serial_is_copied_and_software_key_detected()
    {
        var acc = NewAcc();
        acc.Observe(new FileEvent(Root, @"E:\Foo\a.exe", true, T(1), "ABCD-1234"));
        const string key = @"HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo";
        acc.Observe(new RegistryEvent(Root, key, null, MonitorEventKind.RegistryCreate, T(2)));
        acc.Observe(new RegistryEvent(Root, key, "DisplayName", MonitorEventKind.RegistrySet, T(3)));
        var fp = acc.Finalize("id", new FakeProbe(), T(10));
        Assert.Equal(key, fp.SoftwareKey);
        Assert.Equal("ABCD-1234", fp.Entries.Single(x => x.Type == FootprintEntryType.File).VolumeSerial);
        // 注册表项不走文件落点分类，不应被降为“可能”
        Assert.All(fp.Entries.Where(x => x.Type != FootprintEntryType.File),
            x => Assert.Equal(FootprintConfidence.Certain, x.Confidence));
    }

    // —— 真机复现的缺口 ——

    private const string Dir = @"C:\Program Files\Foo";

    [Fact]
    public void Temp_file_renamed_to_final_name_is_recorded_as_new_under_final_name()
    {
        var acc = NewAcc();
        acc.Observe(new FileEvent(Root, Dir + @"\final.dat.tmp", true, T(1)));
        acc.Observe(new FileRenameEvent(Root, Dir + @"\final.dat.tmp", Dir + @"\final.dat", T(2)));
        var e = Assert.Single(acc.Finalize("id", new FakeProbe(), T(10)).Entries);
        Assert.Equal(Dir + @"\final.dat", e.Path);
        Assert.True(e.IsNew);
        Assert.Equal(FootprintConfidence.Certain, e.Confidence);
    }

    [Fact]
    public void Renaming_a_preexisting_file_is_not_new()
    {
        var acc = NewAcc();
        acc.Observe(new FileRenameEvent(Root, Dir + @"\old.cfg", Dir + @"\new.cfg", T(1)));
        var e = Assert.Single(acc.Finalize("id", new FakeProbe(), T(10)).Entries);
        Assert.False(e.IsNew);   // 原本就有的文件：只算动过，删除时不能当软件新建的
    }

    [Fact]
    public void Renaming_a_directory_moves_files_already_recorded_inside_it()
    {
        var acc = NewAcc();
        acc.Observe(new FileEvent(Root, Dir + @"\staging\a.dll", true, T(1)));
        acc.Observe(new FileRenameEvent(Root, Dir + @"\staging", Dir + @"\bin", T(2)));
        var probe = new FakeProbe();
        probe.Missing.Add(Dir + @"\staging\a.dll");
        probe.Missing.Add(Dir + @"\bin");   // 目录本身不是文件条目，复验按文件判
        var fp = acc.Finalize("id", probe, T(10));
        var moved = Assert.Single(fp.Entries, x => x.Path == Dir + @"\bin\a.dll");
        Assert.True(moved.IsNew);
    }

    [Fact]
    public void New_value_under_existing_key_is_new_but_overwritten_value_is_not()
    {
        var acc = NewAcc();
        acc.Observe(new RegistryEvent(Root, @"HKCU\Software\Pre", "Added", MonitorEventKind.RegistrySet, T(1), IsNew: true));
        acc.Observe(new RegistryEvent(Root, @"HKCU\Software\Pre", "Changed", MonitorEventKind.RegistrySet, T(2), IsNew: false));
        var fp = acc.Finalize("id", new FakeProbe(), T(10));
        Assert.True(fp.Entries.Single(x => x.Path.EndsWith("Added")).IsNew);
        Assert.False(fp.Entries.Single(x => x.Path.EndsWith("Changed")).IsNew);
    }

    [Fact]
    public void Registry_change_without_resolved_key_marks_incomplete_only_when_in_tree()
    {
        var acc = NewAcc();
        acc.Observe(new RegistryEvent(Root, "", "X", MonitorEventKind.RegistrySet, T(1)));
        acc.Observe(new RegistryEvent(9999, "", "Y", MonitorEventKind.RegistrySet, T(1)));   // 树外的不算
        var fp = acc.Finalize("id", new FakeProbe(), T(10));
        Assert.Empty(fp.Entries);
        Assert.False(fp.Completeness.IsComplete);
        Assert.Contains(fp.Completeness.Reasons, r => r.Contains("1 处注册表改动"));
    }

    [Fact]
    public void System_registry_noise_is_dropped()
    {
        var acc = NewAcc();
        acc.Observe(new RegistryEvent(Root,
            @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist\{GUID}\Count", "x",
            MonitorEventKind.RegistrySet, T(1), IsNew: true));
        Assert.Empty(acc.Finalize("id", new FakeProbe(), T(10)).Entries);
    }

    [Fact]
    public void Inventory_additions_are_possible_entries_and_failure_marks_incomplete()
    {
        var acc = NewAcc();
        acc.AddInventoryAdditions([new InventoryItem(FootprintEntryType.ScheduledTask, @"\FooUpdater")], T(5));
        var fp = acc.Finalize("id", new FakeProbe(), T(10));
        var e = Assert.Single(fp.Entries);
        Assert.Equal(FootprintEntryType.ScheduledTask, e.Type);
        Assert.Equal(FootprintConfidence.Possible, e.Confidence);
        Assert.True(fp.Completeness.IsComplete);

        var acc2 = NewAcc();
        acc2.RecordInventoryUnavailable();
        Assert.False(acc2.Finalize("id", new FakeProbe(), T(10)).Completeness.IsComplete);
    }
}
