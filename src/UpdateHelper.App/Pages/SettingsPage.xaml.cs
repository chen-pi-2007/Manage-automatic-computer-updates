namespace UpdateHelper.App.Pages;

public partial class SettingsPage : System.Windows.Controls.Page
{
    public SettingsPage()
    {
        DataContext = AppHost.SettingsPage;
        InitializeComponent();
    }
}
