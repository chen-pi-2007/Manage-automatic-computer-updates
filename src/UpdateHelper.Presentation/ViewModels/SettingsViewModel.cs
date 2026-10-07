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

    public string? SaveError => _settings.SaveError;

    public string AutoUpdateNote => "分级自动、全部自动和定时检查将在后续版本启用；目前程序打开时检查一次，有更新会提醒你。";
}
