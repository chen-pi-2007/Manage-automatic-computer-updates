using System.Windows;

namespace UpdateHelper.App.Pages;

public partial class RulesPage : System.Windows.Controls.Page
{
    public RulesPage()
    {
        InitializeComponent();
    }

    // VM 内部已兜住本地异常，这里 fire-and-forget；重入由 VM.IsBusy 防护
    private void OnLoaded(object sender, RoutedEventArgs e) => _ = AppHost.RuleSubscription.RefreshAsync();

    private void OnSync(object sender, RoutedEventArgs e) => _ = AppHost.RuleSubscription.SyncAsync();
}
