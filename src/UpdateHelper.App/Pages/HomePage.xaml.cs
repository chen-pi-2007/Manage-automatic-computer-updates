namespace UpdateHelper.App.Pages;

public partial class HomePage : System.Windows.Controls.Page
{
    public HomePage()
    {
        DataContext = AppHost.Home;
        InitializeComponent();
    }
}
