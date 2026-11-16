using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Monitor;
using UpdateHelper.Presentation.Settings;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

/// <summary>设置页"监控安装"：选安装包 → 在监控下运行 → 列出足迹。全程用假后端，不涉及 UAC / 助手 / ETW。</summary>
public sealed class MonitorInstallViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-moninst-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private readonly FakeBackend _backend = new();
    private string? _picked = @"C:\Downloads\setup.exe";

    private SettingsService NewSettings(bool moduleOn)
    {
        var s = new SettingsService(new SettingsStore(Path.Combine(_dir, "settings.json")));
        s.Update(x => x with { MonitorModuleEnabled = moduleOn });
        return s;
    }

    private MonitorInstallViewModel NewVm(SettingsService settings) => new(_backend, settings, () => _picked);

    private static readonly DateTimeOffset T = new(2026, 11, 16, 16, 28, 0, TimeSpan.FromHours(8));

    private static MonitorFootprint Footprint(bool complete = true, params FootprintEntry[] entries) =>
        new("id1", T, T.AddSeconds(5), 42, @"C:\Downloads\setup.exe", null,
            new FootprintCompleteness(complete, complete ? [] : ["监控丢事件 3 个，部分改动可能没有被记录"]), entries);

    private static FootprintEntry Entry(FootprintEntryType type, string path, FootprintConfidence c = FootprintConfidence.Certain,
        bool isNew = true) => new(type, path, null, c, 42, null, T, isNew);

    [Fact]
    public void Cannot_start_while_module_is_off_and_becomes_available_when_turned_on()
    {
        var settings = NewSettings(moduleOn: false);
        var vm = NewVm(settings);
        Assert.False(vm.StartCommand.CanExecute(null));

        settings.Update(x => x with { MonitorModuleEnabled = true });
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Cancelling_the_file_picker_does_nothing()
    {
        _picked = null;
        var vm = NewVm(NewSettings(true));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Null(_backend.LastMonitoredInstaller);
        Assert.Empty(vm.Entries);
    }

    [Fact]
    public async Task Successful_run_lists_entries_certain_first_with_readable_labels()
    {
        _backend.OnStartMonitoredInstall = (_, _) => Task.FromResult(Footprint(true,
            Entry(FootprintEntryType.ScheduledTask, @"\FooUpdater", FootprintConfidence.Possible),
            Entry(FootprintEntryType.File, @"C:\Program Files\Foo\foo.exe"),
            Entry(FootprintEntryType.RegistryValue, @"HKCU\Software\Pre\X", isNew: false)));
        var vm = NewVm(NewSettings(true));

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(@"C:\Downloads\setup.exe", _backend.LastMonitoredInstaller);
        Assert.Equal(3, vm.Entries.Count);
        Assert.Equal("确定", vm.Entries[0].Confidence);
        Assert.Equal("可能", vm.Entries[^1].Confidence);
        Assert.Contains(vm.Entries, r => r.Kind == "计划任务" && r.Path == @"\FooUpdater");
        Assert.Contains(vm.Entries, r => r.Kind == "注册表值" && r.Change == "修改");
        Assert.Contains("共 3 项", vm.Summary);
        Assert.Contains("1 项", vm.Summary);   // 「可能」档的项数
        Assert.Contains("完整", vm.CompletenessText);
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public async Task Incomplete_footprint_shows_the_reasons()
    {
        _backend.OnStartMonitoredInstall = (_, _) => Task.FromResult(Footprint(false));
        var vm = NewVm(NewSettings(true));
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Contains("可能不完整", vm.CompletenessText);
        Assert.Contains("丢事件", vm.CompletenessText);
    }

    [Fact]
    public async Task Failure_shows_backend_message_and_keeps_previous_result()
    {
        _backend.Footprints.Add(Footprint(true, Entry(FootprintEntryType.File, @"C:\old.txt")));
        var vm = NewVm(NewSettings(true));
        vm.LoadLatest();
        _backend.OnStartMonitoredInstall = (_, _) => throw new InvalidOperationException("监控服务没有按时启动");

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Contains("监控服务没有按时启动", vm.Message);
        Assert.Single(vm.Entries);   // 上一次的结果还在
        Assert.False(vm.IsRunning);
        Assert.True(vm.StartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Progress_reports_how_many_items_were_found_so_far()
    {
        var gate = new TaskCompletionSource<MonitorFootprint>();
        IProgress<DiscoveryItem>? captured = null;
        _backend.OnStartMonitoredInstall = (_, p) => { captured = p; return gate.Task; };
        var vm = NewVm(NewSettings(true));

        var run = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        Assert.False(vm.StartCommand.CanExecute(null));   // 进行中不能再开第二个

        captured!.Report(new DiscoveryItem(FootprintEntryType.File, @"C:\a", FootprintConfidence.Possible));
        captured.Report(new DiscoveryItem(FootprintEntryType.File, @"C:\b", FootprintConfidence.Possible));
        for (var i = 0; i < 100 && !vm.Message!.Contains("2 项"); i++) await Task.Delay(10);   // Progress 在线程池上回调
        Assert.Contains("2 项", vm.Message);

        gate.SetResult(Footprint(true));
        await run;
        Assert.False(vm.IsRunning);
    }

    [Fact]
    public void Load_latest_shows_the_most_recent_saved_footprint()
    {
        _backend.Footprints.Add(Footprint(true, Entry(FootprintEntryType.File, @"C:\Program Files\Foo\foo.exe")));
        var vm = NewVm(NewSettings(true));
        vm.LoadLatest();
        Assert.Single(vm.Entries);
        Assert.Contains("setup.exe", vm.ResultTitle);
    }
}
