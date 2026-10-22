using UpdateHelper.Core.Monitor;
using Xunit;

namespace UpdateHelper.Core.Tests.Monitor;

public sealed class MonitorFootprintStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uh-footprint-" + Guid.NewGuid().ToString("N"));
    private readonly DateTimeOffset _now = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
    }

    private MonitorFootprintStore NewStore() => new(_root, () => _now);

    private MonitorFootprint Make(string id, DateTimeOffset start, string exe = @"C:\Setup\安装.exe") => new(
        id, start, start.AddMinutes(1), 1234, exe, @"HKLM\SOFTWARE\Demo",
        new FootprintCompleteness(false, ["监控中途丢失事件"]),
        [new FootprintEntry(FootprintEntryType.File, @"C:\Program Files\演示\a.dll", "ABCD-1234",
            FootprintConfidence.Certain, 1234, exe, start, true)]);

    [Fact]
    public void Save_then_read_roundtrips()
    {
        var store = NewStore();
        var f = Make("a", _now);
        store.Save(f);

        var got = Assert.Single(store.ReadAll());
        Assert.Equal(f.Id, got.Id);
        Assert.Equal(f.Start, got.Start);
        Assert.Equal(f.End, got.End);
        Assert.Equal(f.RootPid, got.RootPid);
        Assert.Equal(f.RootExe, got.RootExe);
        Assert.Equal(f.SoftwareKey, got.SoftwareKey);
        Assert.False(got.Completeness.IsComplete);
        Assert.Equal(["监控中途丢失事件"], got.Completeness.Reasons);
        Assert.Equal(f.Entries, got.Entries);   // record 值相等，含 Type/Path/VolumeSerial/Confidence 等
        Assert.Equal(f.Id, store.Find("a")!.Id);
        Assert.Null(store.Find("nope"));
    }

    [Fact]
    public void Save_same_id_replaces()
    {
        var store = NewStore();
        store.Save(Make("a", _now, @"C:\old.exe"));
        store.Save(Make("a", _now, @"C:\new.exe"));

        var got = Assert.Single(store.ReadAll());
        Assert.Equal(@"C:\new.exe", got.RootExe);
    }

    [Fact]
    public void ReadAll_orders_by_time_desc()
    {
        var store = NewStore();
        store.Save(Make("old", _now.AddHours(-2)));
        store.Save(Make("new", _now));
        store.Save(Make("mid", _now.AddHours(-1)));

        Assert.Equal(["new", "mid", "old"], store.ReadAll().Select(x => x.Id));
    }

    [Fact]
    public void Bad_line_is_skipped()
    {
        var store = NewStore();
        store.Save(Make("a", _now));
        File.AppendAllText(Path.Combine(_root, "footprints.jsonl"), "{这不是合法 JSON\n");
        store.Save(Make("b", _now.AddMinutes(-5)));   // 重写后坏行被清掉，也不应抛

        Assert.Equal(["a", "b"], store.ReadAll().Select(x => x.Id));

        // 再追加一次坏行，仅读取也不抛
        File.AppendAllText(Path.Combine(_root, "footprints.jsonl"), "garbage\n");
        Assert.Equal(2, store.ReadAll().Count);
    }

    [Fact]
    public void PruneExpired_removes_old_only()
    {
        var store = NewStore();
        store.Save(Make("old", _now.AddDays(-40)));
        store.Save(Make("fresh", _now.AddDays(-1)));

        Assert.Equal(1, store.PruneExpired(TimeSpan.FromDays(30)));
        Assert.Equal(["fresh"], store.ReadAll().Select(x => x.Id));
    }
}
