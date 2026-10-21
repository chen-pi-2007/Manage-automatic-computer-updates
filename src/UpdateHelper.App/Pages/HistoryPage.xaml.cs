namespace UpdateHelper.App.Pages;

public partial class HistoryPage : System.Windows.Controls.Page
{
    public HistoryPage()
    {
        DataContext = AppHost.History;
        InitializeComponent();
    }

    private async void Restore_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (((System.Windows.FrameworkElement)sender).DataContext is not UpdateHelper.Presentation.ViewModels.BackupRow row) return;
        await AppHost.History.RestoreAsync(row);
    }
}
