namespace UpdateHelper.App.Pages;

public partial class SettingsPage : System.Windows.Controls.Page
{
    public SettingsPage()
    {
        DataContext = AppHost.SettingsPage;
        InitializeComponent();
        Loaded += (_, _) =>
        {
            AppHost.Agent.ClearMessage();
            _ = AppHost.Agent.RefreshAsync();
            _ = AppHost.SettingsPage.ReconcileMonitorModuleAsync();   // 开关为开但监控任务已不在 → 回落为关
        };
    }
}
