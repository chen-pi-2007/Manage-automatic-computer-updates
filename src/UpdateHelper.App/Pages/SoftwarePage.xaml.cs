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

    private void ViewLeftovers_Click(object sender, RoutedEventArgs e)
    {
        if (SoftwareGrid.SelectedItem is not SoftwareRow row)
        {
            System.Windows.MessageBox.Show("请先在列表里选一个软件。", "查看残留");
            return;
        }
        new LeftoversWindow(row.Group) { Owner = Window.GetWindow(this) }.ShowDialog();
    }
}
