using System.Windows;

namespace UpdateHelper.App.Pages;

public partial class RulesPage : System.Windows.Controls.Page
{
    public RulesPage()
    {
        InitializeComponent();
    }

    // VM 内部已兜住本地异常；重入由 VM.IsBusy 防护
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _ = AppHost.RuleSubscription.RefreshAsync();
        await AppHost.RuleSources.RefreshAsync();
    }

    private async void OnSync(object sender, RoutedEventArgs e)
    {
        _ = AppHost.RuleSubscription.SyncAsync();
        await AppHost.RuleSources.SyncAsync();
    }

    private async void OnAddSource(object sender, RoutedEventArgs e) => await AppHost.RuleSources.AddSourceAsync();

    private async void OnGlobalToggle(object sender, RoutedEventArgs e)
        => await AppHost.RuleSources.SetGlobalEnabledAsync(((Wpf.Ui.Controls.ToggleSwitch)sender).IsChecked == true);

    private async void OnSourceToggle(object sender, RoutedEventArgs e)
    {
        var sw = (Wpf.Ui.Controls.ToggleSwitch)sender;
        if (sw.DataContext is UpdateHelper.Presentation.ViewModels.ThirdPartySourceRow row)
            await AppHost.RuleSources.SetEnabledAsync(row, sw.IsChecked == true);
    }

    private async void OnOverrideToggle(object sender, RoutedEventArgs e)
    {
        var sw = (Wpf.Ui.Controls.ToggleSwitch)sender;
        if (sw.DataContext is UpdateHelper.Presentation.ViewModels.ThirdPartySourceRow row)
            await AppHost.RuleSources.SetOverrideAsync(row, sw.IsChecked == true);
    }

    private async void OnDeleteSource(object sender, RoutedEventArgs e)
    {
        if (((System.Windows.FrameworkElement)sender).DataContext is UpdateHelper.Presentation.ViewModels.ThirdPartySourceRow row)
            await AppHost.RuleSources.DeleteAsync(row);
    }
}
