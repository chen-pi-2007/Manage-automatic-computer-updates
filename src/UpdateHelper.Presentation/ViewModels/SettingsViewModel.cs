using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>设置页。修改立即保存；数值越界会被限制到范围内。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    // 开/关监控模块时调用的动作（由组合根注入 backend 的对应方法；返回 null = 成功，否则是中文原因）。
    // 注入而不是直接依赖 backend：测试不需要 UAC / 助手，也不改变本 VM 只依赖 SettingsService 的结构。
    private readonly Func<Task<string?>>? _enableMonitorModule;
    private readonly Func<Task<string?>>? _disableMonitorModule;
    // 监控计划任务现在是否存在；查询失败应抛异常（= 不确定，不动设置）
    private readonly Func<bool>? _monitorTaskExists;
    private string? _monitorMessage;
    private bool _monitorBusy;

    public SettingsViewModel(SettingsService settings, Func<Task<string?>>? enableMonitorModule = null,
        Func<Task<string?>>? disableMonitorModule = null, Func<bool>? monitorTaskExists = null)
    {
        _settings = settings;
        _enableMonitorModule = enableMonitorModule;
        _disableMonitorModule = disableMonitorModule;
        _monitorTaskExists = monitorTaskExists;
        _settings.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);   // 刷新全部绑定
    }

    /// <summary>最近一次"启用 / 关闭监控模块"尚未完成的任务（测试用来等待；没有则已完成）。</summary>
    public Task PendingMonitorEnable { get; private set; } = Task.CompletedTask;

    /// <summary>开关是否可操作：启用 / 关闭（等管理员确认框）进行期间禁用，避免反复切换触发二次提权。</summary>
    public bool MonitorToggleEnabled => !_monitorBusy;

    private void SetMonitorBusy(bool busy)
    {
        _monitorBusy = busy;
        OnPropertyChanged(nameof(MonitorToggleEnabled));
    }

    /// <summary>
    /// 监控模块开关（默认关）。
    /// 关→开：先保存，再触发一次启用（弹一次管理员确认框）；失败则自动回退为关并说明原因。
    /// 开→关：先弹一次管理员确认框删除监控计划任务，成功才保存为关；取消 / 失败则保持为开并提示（不留"显示关、任务还在"的不一致）。
    /// </summary>
    public bool MonitorModuleEnabled
    {
        get => _settings.Current.MonitorModuleEnabled;
        set
        {
            if (_monitorBusy)
            {
                OnPropertyChanged();   // 进行中忽略切换，并让界面回到真实状态
                return;
            }
            var wasOn = _settings.Current.MonitorModuleEnabled;
            if (wasOn == value) return;   // 没变化不重复提权
            MonitorMessage = null;
            if (value)
            {
                _settings.Update(s => s with { MonitorModuleEnabled = true });
                if (_enableMonitorModule is not null)
                {
                    SetMonitorBusy(true);
                    PendingMonitorEnable = EnableMonitorAsync(_enableMonitorModule);
                }
            }
            else if (_disableMonitorModule is null)
            {
                _settings.Update(s => s with { MonitorModuleEnabled = false });
            }
            else
            {
                SetMonitorBusy(true);
                PendingMonitorEnable = DisableMonitorAsync(_disableMonitorModule);
            }
        }
    }

    /// <summary>最近一次启用 / 关闭的失败原因；没有则为 null。</summary>
    public string? MonitorMessage
    {
        get => _monitorMessage;
        private set { _monitorMessage = value; OnPropertyChanged(); }
    }

    private async Task EnableMonitorAsync(Func<Task<string?>> enable)
    {
        string? failure = null;
        try
        {
            var error = await enable();
            if (error is not null) failure = $"没有启用监控模块：{error}。已保持关闭。";
        }
        catch (Exception ex)
        {
            failure = $"启用监控模块失败：{ex.Message}。已保持关闭。";
        }
        finally
        {
            SetMonitorBusy(false);
        }
        if (failure is null) return;
        // 没启用成功就别留着"开"的假象：回退为关
        _settings.Update(s => s with { MonitorModuleEnabled = false });
        MonitorMessage = failure;
    }

    private async Task DisableMonitorAsync(Func<Task<string?>> disable)
    {
        string? failure = null;
        try
        {
            var error = await disable();
            if (error is not null) failure = $"没有关闭监控模块：{error}。监控任务可能仍在系统里，已保持开启。";
        }
        catch (Exception ex)
        {
            failure = $"关闭监控模块失败：{ex.Message}。已保持开启。";
        }
        finally
        {
            SetMonitorBusy(false);
        }
        if (failure is null)
        {
            _settings.Update(s => s with { MonitorModuleEnabled = false });   // 任务确已删除才保存为关
            return;
        }
        // 删除没成功：开关保持"开"（界面可能已被用户拨到关，通知它回到真实状态）并说明原因
        MonitorMessage = failure;
        OnPropertyChanged(nameof(MonitorModuleEnabled));
    }

    /// <summary>
    /// 核对开关与监控计划任务是否一致（启动时、打开设置页时调用）：设置为开、但任务实际不存在
    /// （例如在“设置”里关闭免确认助手时被一并删除）→ 把设置回落为关，用户才能干净地重新开启。
    /// 查询失败（超时等）= 不确定，什么都不动；有启用 / 关闭正在进行时也不动。
    /// </summary>
    public async Task ReconcileMonitorModuleAsync()
    {
        if (_monitorTaskExists is null || _monitorBusy || !MonitorModuleEnabled) return;
        bool exists;
        try
        {
            exists = await Task.Run(_monitorTaskExists);
        }
        catch (Exception)
        {
            return;
        }
        if (_monitorBusy || exists || !MonitorModuleEnabled) return;   // 查询期间用户已开始操作 / 状态已变：不覆盖
        _settings.Update(s => s with { MonitorModuleEnabled = false });
        MonitorMessage = "监控任务已不存在（可能随“免确认助手”一起被关闭了），监控模块已自动设为关闭；需要时可重新打开。";
    }

    /// <summary>设置页上监控模块的说明：可选、默认关闭；开 / 关各需要一次管理员确认。</summary>
    public string MonitorNote => "监控模块是可选功能，默认关闭：开启后可在安装软件时记录它新增了哪些文件和注册表，日后卸载更干净。打开开关会弹一次管理员确认框，注册监控用的计划任务；关闭开关同样会弹一次管理员确认框，删除这个计划任务（确认框被取消则开关保持原状）。不开启不影响其他任何功能。";

    public int ModeIndex
    {
        get => (int)_settings.Current.Mode;
        set => _settings.Update(s => s with { Mode = (UpdateMode)value });
    }

    public int ObservationDays
    {
        get => _settings.Current.ObservationDays;
        set => _settings.Update(s => s with { ObservationDays = value });
    }

    public int CheckIntervalHours
    {
        get => _settings.Current.CheckIntervalHours;
        set => _settings.Update(s => s with { CheckIntervalHours = value });
    }

    public bool TrayEnabled
    {
        get => _settings.Current.TrayEnabled;
        set => _settings.Update(s => s with { TrayEnabled = value });
    }

    // —— 外观 ——

    /// <summary>强调色选项；第 0 项"跟随系统"的颜色为 null。</summary>
    public IReadOnlyList<AccentOption> AccentOptions { get; } =
    [
        new("跟随系统", null),
        new("蓝色", "#0067C0"),
        new("青色", "#038387"),
        new("绿色", "#0F7B0F"),
        new("紫色", "#8764B8"),
        new("粉色", "#C239B3"),
        new("红色", "#C42B1C"),
        new("橙色", "#CA5010"),
        new("石墨", "#68768A"),
    ];

    public int ThemeIndex
    {
        get => (int)_settings.Current.Theme;
        set => _settings.Update(s => s with { Theme = (ThemeChoice)value });
    }

    /// <summary>当前强调色在选项中的位置；不在预设里（如手改了文件）时显示为"跟随系统"。</summary>
    public int AccentIndex
    {
        get
        {
            var i = AccentOptions.ToList().FindIndex(o => o.Color == _settings.Current.AccentColor);
            return i < 0 ? 0 : i;
        }
        set
        {
            if (value < 0 || value >= AccentOptions.Count) return;
            _settings.Update(s => s with { AccentColor = AccentOptions[value].Color });
        }
    }

    public int BackdropIndex
    {
        get => (int)_settings.Current.Backdrop;
        set => _settings.Update(s => s with { Backdrop = (BackdropChoice)value });
    }

    public int TextSizeIndex
    {
        get => (int)_settings.Current.TextSize;
        set => _settings.Update(s => s with { TextSize = (TextSize)value });
    }

    public bool Compact
    {
        get => _settings.Current.Compact;
        set => _settings.Update(s => s with { Compact = value });
    }

    public bool StartWithWindows
    {
        get => _settings.Current.StartWithWindows;
        set => _settings.Update(s => s with { StartWithWindows = value });
    }

    public string? SaveError => _settings.SaveError;

    public string AutoUpdateNote => "现在会按设置的间隔自动检查更新，发现更新时通知你。免确认更新、彻底卸载（在“我的软件”里卸载并清理残留，删除前自动备份、可在“历史”一键恢复）都已经可用。自动安装（分级自动、全部自动）仍在开发中，暂未启用。";
}

/// <summary>强调色选项：显示名和颜色（null = 跟随系统）。</summary>
public sealed record AccentOption(string Name, string? Color);
