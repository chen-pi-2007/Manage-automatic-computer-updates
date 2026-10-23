using UpdateHelper.Presentation.Settings;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

/// <summary>设置页"监控模块"开关：默认关、持久化、关→开才触发启用、启用失败回退为关。全程不涉及 UAC / 助手。</summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-monmod-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private SettingsViewModel NewVm(Func<Task<string?>>? enable = null, Func<Task<string?>>? disable = null, Func<bool>? taskExists = null) =>
        new(new SettingsService(new SettingsStore(SettingsPath)), enable, disable, taskExists);

    private static Task<string?> Ok() => Task.FromResult<string?>(null);
    private static Task<string?> Fail(string why) => Task.FromResult<string?>(why);

    [Fact]
    public void MonitorModule_defaults_off()
    {
        Assert.False(new AppSettings().MonitorModuleEnabled);
        Assert.False(NewVm().MonitorModuleEnabled);   // 新装 / 无配置文件
    }

    [Fact]
    public void Toggling_persists_setting()
    {
        var vm = NewVm();

        vm.MonitorModuleEnabled = true;

        Assert.True(vm.MonitorModuleEnabled);
        Assert.True(new SettingsStore(SettingsPath).Load().MonitorModuleEnabled);   // 重新读文件仍为 true
    }

    [Fact]
    public async Task Turning_on_triggers_enable_once_and_turning_off_does_not()
    {
        var calls = 0;
        var vm = NewVm(() => { calls++; return Ok(); });

        vm.MonitorModuleEnabled = true;
        vm.MonitorModuleEnabled = true;    // 已经是开：不重复提权
        await vm.PendingMonitorEnable;
        vm.MonitorModuleEnabled = false;   // 关：不触发

        Assert.Equal(1, calls);
        Assert.False(vm.MonitorModuleEnabled);
    }

    [Fact]
    public async Task Failed_enable_reverts_to_off_with_message()
    {
        var vm = NewVm(() => Fail("已取消（在确认框里点了“否”）"));

        vm.MonitorModuleEnabled = true;
        await vm.PendingMonitorEnable;

        Assert.False(vm.MonitorModuleEnabled);
        Assert.False(new SettingsStore(SettingsPath).Load().MonitorModuleEnabled);
        Assert.Contains("已取消", vm.MonitorMessage);   // 具体原因要透出到界面，而不是一句笼统的话
    }

    [Fact]
    public async Task Enable_throwing_also_reverts()
    {
        var vm = NewVm(() => throw new InvalidOperationException("boom"));

        vm.MonitorModuleEnabled = true;
        await vm.PendingMonitorEnable;

        Assert.False(vm.MonitorModuleEnabled);
    }

    // —— #1 启动 / 打开设置页时核对：开关为开但任务不存在 → 回落为关 ——

    [Fact]
    public async Task Reconcile_reverts_setting_when_monitor_task_is_gone()
    {
        var vm = NewVm(taskExists: () => false);
        vm.MonitorModuleEnabled = true;   // 无 enable 注入：只存设置
        Assert.True(vm.MonitorModuleEnabled);

        await vm.ReconcileMonitorModuleAsync();

        Assert.False(vm.MonitorModuleEnabled);
        Assert.False(new SettingsStore(SettingsPath).Load().MonitorModuleEnabled);
        Assert.False(string.IsNullOrEmpty(vm.MonitorMessage));
        // 回落后用户能干净地重新开启（不会被 wasOn==value 早返回卡死）
        var enables = 0;
        var vm2 = NewVm(() => { enables++; return Ok(); }, taskExists: () => false);
        vm2.MonitorModuleEnabled = true;
        await vm2.PendingMonitorEnable;
        Assert.Equal(1, enables);
    }

    [Fact]
    public async Task Reconcile_keeps_setting_when_task_exists_or_query_fails()
    {
        var vm = NewVm(taskExists: () => true);
        vm.MonitorModuleEnabled = true;
        await vm.ReconcileMonitorModuleAsync();
        Assert.True(vm.MonitorModuleEnabled);

        var unsure = NewVm(taskExists: () => throw new InvalidOperationException("查询超时"));
        unsure.MonitorModuleEnabled = true;
        await unsure.ReconcileMonitorModuleAsync();
        Assert.True(unsure.MonitorModuleEnabled);   // 查不出来 ≠ 任务没了：不动
    }

    [Fact]
    public async Task Reconcile_does_nothing_when_setting_is_off()
    {
        var queried = 0;
        var vm = NewVm(taskExists: () => { queried++; return false; });

        await vm.ReconcileMonitorModuleAsync();

        Assert.Equal(0, queried);
        Assert.Null(vm.MonitorMessage);
    }

    // —— #2 关闭开关 = 一次 UAC 删除监控任务；取消 / 失败保持为开 ——

    [Fact]
    public async Task Turning_off_runs_disable_once_and_saves_off_on_success()
    {
        var disables = 0;
        var vm = NewVm(() => Ok(), () => { disables++; return Ok(); });
        vm.MonitorModuleEnabled = true;
        await vm.PendingMonitorEnable;

        vm.MonitorModuleEnabled = false;
        await vm.PendingMonitorEnable;

        Assert.Equal(1, disables);
        Assert.False(vm.MonitorModuleEnabled);
        Assert.False(new SettingsStore(SettingsPath).Load().MonitorModuleEnabled);
        Assert.Null(vm.MonitorMessage);
    }

    [Fact]
    public async Task Cancelled_or_failed_disable_keeps_setting_on_with_message()
    {
        var vm = NewVm(() => Ok(), () => Fail("已取消（在确认框里点了“否”）"));
        vm.MonitorModuleEnabled = true;
        await vm.PendingMonitorEnable;
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.MonitorModuleEnabled = false;
        await vm.PendingMonitorEnable;

        Assert.True(vm.MonitorModuleEnabled);   // 没删成功：不显示"关"却留着任务
        Assert.True(new SettingsStore(SettingsPath).Load().MonitorModuleEnabled);
        Assert.Contains("已取消", vm.MonitorMessage);
        Assert.Contains(nameof(SettingsViewModel.MonitorModuleEnabled), raised);   // 通知界面把开关拨回开

        var throwing = NewVm(() => Ok(), () => throw new InvalidOperationException("boom"));
        throwing.MonitorModuleEnabled = true;
        await throwing.PendingMonitorEnable;
        throwing.MonitorModuleEnabled = false;
        await throwing.PendingMonitorEnable;
        Assert.True(throwing.MonitorModuleEnabled);
    }

    // —— #5 UAC 等待期间禁止重入 ——

    [Fact]
    public async Task Toggle_is_disabled_and_reentry_ignored_while_pending()
    {
        var gate = new TaskCompletionSource<string?>();
        var calls = 0;
        var vm = NewVm(() => { calls++; return gate.Task; }, () => { calls++; return Ok(); });

        vm.MonitorModuleEnabled = true;
        Assert.False(vm.MonitorToggleEnabled);
        vm.MonitorModuleEnabled = false;   // 等 UAC 期间反复切：忽略，不触发第二次提权
        vm.MonitorModuleEnabled = true;
        Assert.Equal(1, calls);
        Assert.True(vm.MonitorModuleEnabled);

        gate.SetResult(null);
        await vm.PendingMonitorEnable;
        Assert.True(vm.MonitorToggleEnabled);
        Assert.True(vm.MonitorModuleEnabled);
    }

    [Fact]
    public void Note_explains_both_on_and_off()
    {
        var note = NewVm().MonitorNote;
        Assert.Contains("打开开关", note);
        Assert.Contains("关闭开关", note);
    }

    [Fact]
    public void Note_mentions_optional_monitor_module()
    {
        var note = NewVm().MonitorNote;
        Assert.Contains("默认关闭", note);
        Assert.Contains("可选", note);
    }
}
