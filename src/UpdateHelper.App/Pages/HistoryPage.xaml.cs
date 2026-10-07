namespace UpdateHelper.App.Pages;

public partial class HistoryPage : System.Windows.Controls.Page
{
    public HistoryPage()
    {
        DataContext = AppHost.History;
        InitializeComponent();
    }
}
