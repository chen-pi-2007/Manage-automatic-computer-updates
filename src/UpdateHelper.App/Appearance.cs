using System.Windows;
using System.Windows.Media;
using UpdateHelper.Presentation.Settings;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace UpdateHelper.App;

/// <summary>把设置里的外观选项（主题、强调色、背景材质、文字大小、紧凑模式）应用到界面。</summary>
public static class Appearance
{
    private static bool _watching;

    public static void Apply(AppSettings s, FluentWindow window)
    {
        var backdrop = s.Backdrop switch
        {
            BackdropChoice.Acrylic => WindowBackdropType.Acrylic,
            BackdropChoice.Solid => WindowBackdropType.None,
            _ => WindowBackdropType.Mica,
        };

        // 主题：跟随系统时监听系统切换；固定浅色/深色时停止监听
        if (s.Theme == ThemeChoice.System)
        {
            ApplicationThemeManager.ApplySystemTheme(updateAccent: false);
            if (!_watching && window.IsLoaded) { SystemThemeWatcher.Watch(window, backdrop, updateAccents: s.AccentColor is null); _watching = true; }
        }
        else
        {
            if (_watching) { SystemThemeWatcher.UnWatch(window); _watching = false; }
            ApplicationThemeManager.Apply(s.Theme == ThemeChoice.Dark ? ApplicationTheme.Dark : ApplicationTheme.Light, backdrop, updateAccent: false);
        }

        // 强调色：在主题之后设置，否则会被主题切换覆盖
        if (s.AccentColor is { } hex)
            ApplicationAccentColorManager.Apply((Color)ColorConverter.ConvertFromString(hex), ApplicationThemeManager.GetAppTheme());
        else
            ApplicationAccentColorManager.ApplySystemAccent();

        window.WindowBackdropType = backdrop;

        var resources = Application.Current.Resources;
        var fontSize = s.TextSize switch { TextSize.Small => 12.0, TextSize.Large => 16.0, _ => 14.0 };
        resources["ControlContentThemeFontSize"] = fontSize;
        window.FontSize = fontSize;
        // 表格行高：标准模式按内容自动，紧凑模式固定矮一些
        resources["UhRowHeight"] = s.Compact ? fontSize + 12 : double.NaN;
    }
}
