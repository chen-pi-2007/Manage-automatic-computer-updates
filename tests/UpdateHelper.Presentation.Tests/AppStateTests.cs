using UpdateHelper.Core.Updates;
using static UpdateHelper.Presentation.Tests.FakeBackend;

namespace UpdateHelper.Presentation.Tests;

public class AppStateTests
{
    [Fact]
    public async Task Refresh_records_first_seen_versions()
    {
        var dir = Directory.CreateTempSubdirectory("uh-state-seen-").FullName;
        try
        {
            var backend = new FakeBackend { Report = new UpdateReport([Update("QQ", UpdateTier.Low)], null) };
            var store = new FirstSeenStore(Path.Combine(dir, "first-seen.json"));
            var state = new AppState(backend, firstSeen: store);

            await state.RefreshAsync();

            var u = state.Updates.Single();
            Assert.NotNull(store.Get(u.Candidate.PackageId, u.Candidate.AvailableVersion));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Refresh_fills_snapshot_updates_and_time()
    {
        var backend = new FakeBackend
        {
            Snapshot = MakeSnapshot([Entry("QQ")]),
            Report = new UpdateReport([Update("QQ", UpdateTier.Low)], null),
        };
        var state = new AppState(backend);

        await state.RefreshAsync();

        Assert.Same(backend.Snapshot, state.Snapshot);
        Assert.Single(state.Updates);
        Assert.NotNull(state.LastChecked);
        Assert.False(state.IsBusy);
        Assert.Equal("", state.StatusText);
        Assert.Null(state.Warning);
    }

    [Fact]
    public async Task Second_refresh_while_busy_is_ignored()   // Review Focus 1
    {
        var gate = new TaskCompletionSource();
        var backend = new FakeBackend { ScanGate = gate };
        var state = new AppState(backend);

        var first = state.RefreshAsync();
        Assert.True(state.IsBusy);
        Assert.Equal("正在扫描电脑上的软件……", state.StatusText);

        await state.RefreshAsync();          // 忙时直接返回
        Assert.Equal(1, backend.ScanCalls);

        gate.SetResult();
        await first;
        Assert.False(state.IsBusy);
    }

    [Fact]
    public async Task Exclusive_work_is_refused_while_busy()
    {
        var gate = new TaskCompletionSource();
        var state = new AppState(new FakeBackend { ScanGate = gate });
        var refresh = state.RefreshAsync();

        var ran = false;
        Assert.False(await state.RunExclusiveAsync(() => { ran = true; return Task.CompletedTask; }));
        Assert.False(ran);

        gate.SetResult();
        await refresh;
        Assert.True(await state.RunExclusiveAsync(() => { ran = true; return Task.CompletedTask; }));
        Assert.True(ran);
        Assert.False(state.IsBusy);
    }

    [Fact]
    public async Task Scan_failure_becomes_warning_and_keeps_old_snapshot()   // Review Focus 2
    {
        var backend = new FakeBackend { Snapshot = MakeSnapshot([Entry("QQ")]) };
        var state = new AppState(backend);
        await state.RefreshAsync();
        var old = state.Snapshot;

        backend.ScanError = new InvalidOperationException("注册表读取失败");
        await state.RefreshAsync();

        Assert.Same(old, state.Snapshot);
        Assert.Equal("检查失败：注册表读取失败", state.Warning);
        Assert.False(state.IsBusy);
    }

    [Fact]
    public async Task Warnings_from_scan_and_update_check_are_joined()   // Review Focus 2
    {
        var backend = new FakeBackend
        {
            Snapshot = MakeSnapshot([Entry("QQ")], warnings: ["读取计划任务失败"]),
            Report = new UpdateReport([], "无法从 winget 获取更新信息：没有找到 winget"),
        };
        var state = new AppState(backend);
        await state.RefreshAsync();

        Assert.Equal("读取计划任务失败；无法从 winget 获取更新信息：没有找到 winget", state.Warning);
        Assert.NotNull(state.Snapshot);   // 软件列表照常可用
    }

    [Theory]
    [InlineData(UpdateTier.Low, "低风险")]
    [InlineData(UpdateTier.Careful, "需确认")]
    [InlineData(UpdateTier.NeverAuto, "不自动")]
    [InlineData(UpdateTier.Ignored, "不管")]
    public void Tier_names(UpdateTier tier, string name) => Assert.Equal(name, Display.TierName(tier));
}
