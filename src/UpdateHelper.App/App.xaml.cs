using System.Windows;
using Wpf.Ui.Appearance;

namespace UpdateHelper.App;

public partial class App : Application
{
    /// <summary>--page 参数可用的页面名。</summary>
    internal static readonly Dictionary<string, Type> PageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = typeof(Pages.HomePage),
        ["updates"] = typeof(Pages.UpdatesPage),
        ["software"] = typeof(Pages.SoftwarePage),
        ["background"] = typeof(Pages.BackgroundPage),
        ["history"] = typeof(Pages.HistoryPage),
        ["settings"] = typeof(Pages.SettingsPage),
    };

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.ApplySystemTheme();   // 跟随系统的深色/浅色

        AppHost.Initialize();
        var window = new MainWindow();
        window.Closed += (_, _) => Shutdown();
        var pageIndex = Array.IndexOf(e.Args, "--page");
        if (pageIndex >= 0 && pageIndex + 1 < e.Args.Length && PageTypes.TryGetValue(e.Args[pageIndex + 1], out var page))
            window.ShowPage(page);
        window.Show();
        _ = AppHost.State.RefreshAsync();             // 启动时扫描并检查一次
    }
}
