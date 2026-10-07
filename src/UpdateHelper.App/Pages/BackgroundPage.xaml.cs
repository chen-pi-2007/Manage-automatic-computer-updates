namespace UpdateHelper.App.Pages;

public partial class BackgroundPage : System.Windows.Controls.Page
{
    public BackgroundPage()
    {
        DataContext = AppHost.Background;
        InitializeComponent();
    }
}
