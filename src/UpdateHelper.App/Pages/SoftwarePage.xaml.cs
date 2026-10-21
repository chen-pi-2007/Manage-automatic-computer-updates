using System.Windows;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.App.Pages;

public partial class SoftwarePage : System.Windows.Controls.Page
{
    public SoftwarePage()
    {
        DataContext = AppHost.Software;
        InitializeComponent();
    }

    private async void ViewLeftovers_Click(object sender, RoutedEventArgs e)
    {
        if (SoftwareGrid.SelectedItem is not SoftwareRow row)
        {
            await AppHost.Confirm.InfoAsync("查看残留", "请先在列表里选一个软件。");
            return;
        }
        new LeftoversWindow(row.Group, uninstallMode: false) { Owner = Window.GetWindow(this) }.ShowDialog();
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (SoftwareGrid.SelectedItem is not SoftwareRow row)
        {
            await AppHost.Confirm.InfoAsync("卸载", "请先在列表里选一个软件。");
            return;
        }
        new LeftoversWindow(row.Group, uninstallMode: true) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
