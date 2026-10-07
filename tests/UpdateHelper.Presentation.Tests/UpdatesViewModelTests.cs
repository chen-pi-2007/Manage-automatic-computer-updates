using UpdateHelper.Core.Install;
using UpdateHelper.Core.Updates;
using UpdateHelper.Presentation.ViewModels;
using static UpdateHelper.Presentation.Tests.FakeBackend;

namespace UpdateHelper.Presentation.Tests;

public class UpdatesViewModelTests
{
    private static async Task<(UpdatesViewModel Vm, FakeBackend Backend, AppState State)> Make(params JudgedUpdate[] updates)
    {
        var backend = new FakeBackend { Report = new UpdateReport(updates, null) };
        var state = new AppState(backend);
        await state.RefreshAsync();
        return (new UpdatesViewModel(state), backend, state);
    }

    [Fact]
    public async Task Rows_follow_tiers_and_default_selection()
    {
        var (vm, _, _) = await Make(
            Update("QQ", UpdateTier.Low),
            Update("VirtualBox", UpdateTier.Careful, "开发工具跨了小版本（7.0 → 7.2）"),
            Update("微信", UpdateTier.NeverAuto, "电脑上登记了多个\"微信\""),
            Update("CS2", UpdateTier.Ignored, "游戏由 Steam 等平台负责更新"));

        Assert.Equal(new[] { "QQ", "VirtualBox", "微信" }, vm.Rows.Select(r => r.Name));   // "不管"档不显示
        Assert.Equal(new[] { true, false, false }, vm.Rows.Select(r => r.IsSelected));       // 默认只勾低风险
        Assert.Equal(new[] { true, true, false }, vm.Rows.Select(r => r.CanSelect));
        Assert.Equal("1.0 → 1.1", vm.Rows[0].Versions);
        Assert.Equal("需确认", vm.Rows[1].TierName);
        Assert.Equal("3 个更新，已选 1 个", vm.Summary);
    }

    [Fact]
    public async Task Never_auto_row_cannot_be_selected()
    {
        var (vm, _, _) = await Make(Update("微信", UpdateTier.NeverAuto));
        vm.Rows[0].IsSelected = true;
        Assert.False(vm.Rows[0].IsSelected);
    }

    [Fact]
    public async Task Selection_changes_update_summary_and_command()
    {
        var (vm, _, _) = await Make(Update("QQ", UpdateTier.Low), Update("VirtualBox", UpdateTier.Careful));
        vm.Rows[1].IsSelected = true;
        Assert.Equal("2 个更新，已选 2 个", vm.Summary);

        vm.Rows[0].IsSelected = false;
        vm.Rows[1].IsSelected = false;
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task Installs_selected_rows_one_by_one_then_rechecks()
    {
        var (vm, backend, _) = await Make(Update("QQ", UpdateTier.Low), Update("网易云音乐", UpdateTier.Low),
            Update("VirtualBox", UpdateTier.Careful));
        var scansBefore = backend.ScanCalls;

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Pkg.QQ", "Pkg.网易云音乐" }, backend.Installed);   // 没勾的 VirtualBox 不装
        Assert.Equal(new[] { "QQ：已更新：已从 1.0 更新到 1.1", "网易云音乐：已更新：已从 1.0 更新到 1.1" }, vm.LastResults);
        Assert.Equal(scansBefore + 1, backend.ScanCalls);   // 结束后重新检查
    }

    [Fact]
    public async Task One_failure_does_not_stop_the_rest()   // Review Focus 3
    {
        var (vm, backend, _) = await Make(Update("A", UpdateTier.Low), Update("B", UpdateTier.Low), Update("C", UpdateTier.Low));
        backend.Install = u => u.Candidate.Name switch
        {
            "A" => new ExecuteResult(ExecuteOutcome.Failed, "安装失败：InstallError", null),
            "B" => throw new InvalidOperationException("RPC 断开"),
            _ => new ExecuteResult(ExecuteOutcome.Succeeded, "已从 1.0 更新到 1.1", "1.1"),
        };

        await vm.InstallSelectedCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Pkg.A", "Pkg.B", "Pkg.C" }, backend.Installed);
        Assert.Equal(new[] { "A：失败：安装失败：InstallError", "B：失败：RPC 断开", "C：已更新：已从 1.0 更新到 1.1" }, vm.LastResults);
    }

    [Fact]
    public async Task Command_is_disabled_while_busy()   // Review Focus 1
    {
        var (vm, backend, state) = await Make(Update("QQ", UpdateTier.Low));
        var gate = new TaskCompletionSource();
        backend.ScanGate = gate;

        var refresh = state.RefreshAsync();
        Assert.False(vm.InstallSelectedCommand.CanExecute(null));

        gate.SetResult();
        await refresh;
        Assert.True(vm.InstallSelectedCommand.CanExecute(null));
    }
}
