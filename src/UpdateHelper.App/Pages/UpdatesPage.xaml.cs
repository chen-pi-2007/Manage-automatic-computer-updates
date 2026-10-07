namespace UpdateHelper.App.Pages;

public partial class UpdatesPage : System.Windows.Controls.Page
{
    public UpdatesPage()
    {
        DataContext = AppHost.Updates;
        InitializeComponent();
    }
}
