using UpdateHelper.Core.Install;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;
using UpdateHelper.Presentation.Settings;
using UpdateHelper.Presentation.ViewModels;
using static UpdateHelper.Presentation.Tests.FakeBackend;

namespace UpdateHelper.Presentation.Tests;

public sealed class PageViewModelTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-pages-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private SettingsService Settings() => new(new SettingsStore(Path.Combine(_dir, "settings.json")));

    // —— 首页 ——

    [Fact]
    public void Home_before_first_check()
    {
        var home = new HomeViewModel(new AppState(new FakeBackend()), Settings());
        Assert.Equal("还没有检查更新", home.SummaryTitle);
        Assert.Equal("还没有检查", home.LastCheckedText);
    }

    [Fact]
    public async Task Home_summarizes_updates_by_tier()
    {
        var backend = new FakeBackend
        {
            Snapshot = MakeSnapshot([Entry("QQ"), Entry("微信"), Entry("CUBLAS", hidden: true)]),
            Report = new UpdateReport([
                Update("QQ", UpdateTier.Low), Update("A", UpdateTier.Low), Update("B", UpdateTier.Careful),
                Update("微信", UpdateTier.NeverAuto), Update("CS2", UpdateTier.Ignored)], null),
        };
        var state = new AppState(backend);
        var home = new HomeViewModel(state, Settings());

        await state.RefreshAsync();

        Assert.Equal("有 4 个更新可用", home.SummaryTitle);   // "不管"档不算
        Assert.Equal("其中 2 个低风险、1 个需确认、1 个不自动", home.SummaryDetail);
        Assert.Equal("已识别 2 个软件", home.SoftwareCountText);   // 组件合集不算
        Assert.StartsWith("上次检查：", home.LastCheckedText);
    }

    [Fact]
    public async Task Home_all_up_to_date_and_warning()
    {
        var backend = new FakeBackend { Report = new UpdateReport([Update("CS2", UpdateTier.Ignored)], "无法从 winget 获取更新信息：x") };
        var state = new AppState(backend);
        var home = new HomeViewModel(state, Settings());
        await state.RefreshAsync();

        Assert.Equal("所有软件都是最新的", home.SummaryTitle);
        Assert.Equal("", home.SummaryDetail);
        Assert.Equal("无法从 winget 获取更新信息：x", home.Warning);
    }

    [Fact]
    public async Task Home_check_now_is_disabled_while_busy()   // Review Focus 1
    {
        var gate = new TaskCompletionSource();
        var state = new AppState(new FakeBackend { ScanGate = gate });
        var home = new HomeViewModel(state, Settings());

        var refresh = state.RefreshAsync();
        Assert.False(home.CheckNowCommand.CanExecute(null));
        Assert.Equal("正在扫描电脑上的软件……", home.SummaryTitle);

        gate.SetResult();
        await refresh;
        Assert.True(home.CheckNowCommand.CanExecute(null));
    }

    [Fact]
    public void Mode_is_shared_between_home_and_settings_and_saved()
    {
        var path = Path.Combine(_dir, "settings.json");
        var service = new SettingsService(new SettingsStore(path));
        var home = new HomeViewModel(new AppState(new FakeBackend()), service);
        var settings = new SettingsViewModel(service);

        home.ModeIndex = (int)UpdateMode.Tiered;

        Assert.Equal((int)UpdateMode.Tiered, settings.ModeIndex);
        Assert.Equal(UpdateMode.Tiered, new SettingsStore(path).Load().Mode);
    }

    [Fact]
    public void Settings_values_are_clamped_and_saved()   // Review Focus 4
    {
        var path = Path.Combine(_dir, "settings.json");
        var vm = new SettingsViewModel(new SettingsService(new SettingsStore(path)));

        vm.ObservationDays = 99;
        vm.CheckIntervalHours = 0;
        vm.TrayEnabled = false;

        Assert.Equal(30, vm.ObservationDays);
        Assert.Equal(1, vm.CheckIntervalHours);
        var saved = new SettingsStore(path).Load();
        Assert.Equal((30, 1, false), (saved.ObservationDays, saved.CheckIntervalHours, saved.TrayEnabled));
        Assert.Contains("后续版本", vm.AutoUpdateNote);
    }

    // —— 我的软件 ——

    private static async Task<AppState> StateWith(ScanSnapshot snapshot)
    {
        var state = new AppState(new FakeBackend { Snapshot = snapshot });
        await state.RefreshAsync();
        return state;
    }

    [Fact]
    public async Task Software_list_filters_and_searches()
    {
        var state = await StateWith(MakeSnapshot([
            Entry("QQ", "9.9.20", "腾讯", @"C:\Program Files\Tencent\QQNT"),
            Entry("Git", "2.50.1", "The Git Development Community"),
            Entry("Microsoft Visual C++ 2013 Redistributable (x64)", "12.0", "Microsoft Corporation"),
            Entry("Python 3.10.2 (64-bit)", "3.10.2", "Python Software Foundation"),
            Entry("Python 3.10.2 Core Interpreter (64-bit)", null, "Python Software Foundation", hidden: true)]));
        var vm = new SoftwareViewModel(state);

        Assert.Equal(new[] { "QQ", "Git", "Python 3.10.2 (64-bit)" }.Order(), vm.Rows.Select(r => r.Name).Order());  // 默认不含运行库
        Assert.Equal("共 3 个", vm.CountText);
        Assert.Equal("含 1 个组件", vm.Rows.Single(r => r.Name.StartsWith("Python")).Components);
        Assert.Equal("普通软件", vm.Rows.Single(r => r.Name == "QQ").Category);

        vm.ShowAll = true;
        Assert.Equal(4, vm.Rows.Count);

        vm.SearchText = "python";
        Assert.Equal("Python 3.10.2 (64-bit)", Assert.Single(vm.Rows).Name);
    }

    [Fact]
    public async Task Software_rows_carry_the_registry_icon()
    {
        var state = await StateWith(MakeSnapshot([
            Entry("QQ", "9.9.20", "腾讯", @"C:\Program Files\Tencent\QQNT") with { DisplayIcon = @"C:\Program Files\Tencent\QQNT\QQ.exe,0" },
            Entry("Git", "2.50.1", "The Git Development Community")]));
        var vm = new SoftwareViewModel(state);

        Assert.Equal(new IconSource(@"C:\Program Files\Tencent\QQNT\QQ.exe", 0), vm.Rows.Single(r => r.Name == "QQ").Icon);
        Assert.Null(vm.Rows.Single(r => r.Name == "Git").Icon);   // 没登记图标：界面上用通用图标
    }

    // —— 后台项目 ——

    [Fact]
    public async Task Background_rows_show_owner_and_explanation()
    {
        var task = new BackgroundItem(BackgroundKind.ScheduledTask, "WpsUpdateTask", null, null, @"D:\WPS\up.exe", null);
        var service = new BackgroundItem(BackgroundKind.Service, "gupdate", null, null, @"C:\Google\GoogleUpdate.exe", null);
        var state = await StateWith(MakeSnapshot(
            [Entry("WPS Office", location: @"D:\WPS")], [task, service],
            explanations: new Dictionary<BackgroundItem, string> { [task] = "WPS 的自动更新任务" }));
        var vm = new BackgroundViewModel(state);

        var wps = vm.Rows.Single(r => r.Name == "WpsUpdateTask");
        Assert.Equal(("计划任务", "WPS Office", "WPS 的自动更新任务"), (wps.Kind, wps.Owner, wps.Explanation));
        var g = vm.Rows.Single(r => r.Name == "gupdate");
        Assert.Equal(("服务", "（未归属）", "未知用途"), (g.Kind, g.Owner, g.Explanation));
        Assert.Equal("共 2 个后台项目", vm.CountText);
    }

    // —— 历史 ——

    [Fact]
    public async Task History_is_newest_first_with_readable_fields()
    {
        var backend = new FakeBackend();
        backend.History.Add(new HistoryRecord(new DateTimeOffset(2026, 10, 6, 20, 50, 0, TimeSpan.FromHours(8)),
            "Bilibili.Bilibili", "哔哩哔哩", "1.16.5", "1.19.0", ExecuteOutcome.Succeeded, "已从 1.16.5 更新到 1.19.0", false));
        backend.History.Add(new HistoryRecord(new DateTimeOffset(2026, 10, 7, 9, 0, 0, TimeSpan.FromHours(8)),
            "rule:x", "EV录屏", "5.2.3", "9.9.9", ExecuteOutcome.Failed, "签名不对", true));
        var state = new AppState(backend);
        var vm = new HistoryViewModel(state);

        Assert.Equal(new[] { "EV录屏", "哔哩哔哩" }, vm.Rows.Select(r => r.Name));
        Assert.Equal(new HistoryRow("2026-10-06 20:50", "哔哩哔哩", "1.16.5 → 1.19.0", "已更新", "已从 1.16.5 更新到 1.19.0", "手动"), vm.Rows[1]);
        Assert.Equal("自动", vm.Rows[0].Trigger);

        backend.History.Clear();
        await state.RefreshAsync();   // 每次检查结束都会重新读历史
        Assert.Empty(vm.Rows);
    }
}
