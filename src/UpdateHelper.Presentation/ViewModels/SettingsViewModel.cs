using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>设置页。修改立即保存；数值越界会被限制到范围内。</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;

    public SettingsViewModel(SettingsService settings)
    {
        _settings = settings;
        _settings.PropertyChanged += (_, _) => OnPropertyChanged(string.Empty);   // 刷新全部绑定
    }

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

    public string? SaveError => _settings.SaveError;

    public string AutoUpdateNote => "分级自动、全部自动和定时检查将在后续版本启用；目前程序打开时检查一次，有更新会提醒你。";
}

/// <summary>强调色选项：显示名和颜色（null = 跟随系统）。</summary>
public sealed record AccentOption(string Name, string? Color);
