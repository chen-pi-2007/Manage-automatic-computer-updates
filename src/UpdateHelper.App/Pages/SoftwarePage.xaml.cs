namespace UpdateHelper.App.Pages;

public partial class SoftwarePage : System.Windows.Controls.Page
{
    public SoftwarePage()
    {
        DataContext = AppHost.Software;
        InitializeComponent();
    }
}
