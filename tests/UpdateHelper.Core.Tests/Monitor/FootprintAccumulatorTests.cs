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
}
