using UpdateHelper.Presentation.Settings;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

/// <summary>设置页"监控模块"开关：默认关、持久化、关→开才触发启用、启用失败回退为关。全程不涉及 UAC / 助手。</summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-monmod-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string SettingsPath => Path.Combine(_dir, "settings.json");

    private SettingsViewModel NewVm(Func<Task<bool>>? enable = null) =>
        new(new SettingsService(new SettingsStore(SettingsPath)), enable);

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
        var vm = NewVm(() => { calls++; return Task.FromResult(true); });

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
        var vm = NewVm(() => Task.FromResult(false));

        vm.MonitorModuleEnabled = true;
        await vm.PendingMonitorEnable;

        Assert.False(vm.MonitorModuleEnabled);
        Assert.False(new SettingsStore(SettingsPath).Load().MonitorModuleEnabled);
        Assert.False(string.IsNullOrEmpty(vm.MonitorMessage));
    }

    [Fact]
    public async Task Enable_throwing_also_reverts()
    {
        var vm = NewVm(() => throw new InvalidOperationException("boom"));

        vm.MonitorModuleEnabled = true;
        await vm.PendingMonitorEnable;

        Assert.False(vm.MonitorModuleEnabled);
    }

    [Fact]
    public void Note_mentions_optional_monitor_module()
    {
        var note = NewVm().MonitorNote;
        Assert.Contains("默认关闭", note);
        Assert.Contains("可选", note);
    }
}
