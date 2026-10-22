using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>设置页。修改立即保存；数值越界会被限制到范围内。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    // 关→开时调用的"启用监控模块"动作（由组合根注入 backend.EnableMonitorModuleAsync；返回 false = 没成功）。
    // 注入而不是直接依赖 backend：测试不需要 UAC / 助手，也不改变本 VM 只依赖 SettingsService 的结构。
    private readonly Func<Task<bool>>? _enableMonitorModule;
    private string? _monitorMessage;

    public SettingsViewModel(SettingsService settings, Func<Task<bool>>? enableMonitorModule = null)
    {
        _settings = settings;
        _enableMonitorModule = enableMonitorModule;
        _settings.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);   // 刷新全部绑定
    }

    /// <summary>最近一次"启用监控模块"尚未完成的任务（测试用来等待；没有则已完成）。</summary>
    public Task PendingMonitorEnable { get; private set; } = Task.CompletedTask;

    /// <summary>监控模块开关（默认关）。关→开：先保存，再触发一次启用（弹一次管理员确认框）；失败则自动回退为关。</summary>
    public bool MonitorModuleEnabled
    {
        get => _settings.Current.MonitorModuleEnabled;
        set
        {
            var wasOn = _settings.Current.MonitorModuleEnabled;
            if (wasOn == value) return;   // 没变化不重复提权
            _settings.Update(s => s with { MonitorModuleEnabled = value });
            MonitorMessage = null;
            if (value && _enableMonitorModule is not null) PendingMonitorEnable = EnableMonitorAsync(_enableMonitorModule);
        }
    }

    /// <summary>启用结果提示（失败原因）；没有则为 null。</summary>
    public string? MonitorMessage
    {
        get => _monitorMessage;
        private set { _monitorMessage = value; OnPropertyChanged(); }
    }

    private async Task EnableMonitorAsync(Func<Task<bool>> enable)
    {
        string? failure = null;
        try
        {
            if (!await enable()) failure = "没有启用监控模块（管理员确认框被取消或注册失败），已保持关闭。";
        }
        catch (Exception ex)
        {
            failure = $"启用监控模块失败：{ex.Message}";
        }
        if (failure is null) return;
        // 没启用成功就别留着"开"的假象：回退为关
        _settings.Update(s => s with { MonitorModuleEnabled = false });
        MonitorMessage = failure;
    }

    /// <summary>设置页上监控模块的说明：可选、默认关闭、开启需要一次管理员确认。</summary>
    public string MonitorNote => "监控模块是可选功能，默认关闭：开启后可在安装软件时记录它新增了哪些文件和注册表，日后卸载更干净。开启时会弹一次管理员确认框；不开启不影响其他任何功能。";

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
