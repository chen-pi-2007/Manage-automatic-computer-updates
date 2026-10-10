using System.Windows;

namespace UpdateHelper.App.Pages;

public partial class SecurityPage : System.Windows.Controls.Page
{
    public SecurityPage()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => _ = AppHost.Security.RefreshAsync();

    private void OnRecheck(object sender, RoutedEventArgs e) => _ = AppHost.Security.RefreshAsync();

    private void OnOpenSecurityCenter(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("windowsdefender://") { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }
}
