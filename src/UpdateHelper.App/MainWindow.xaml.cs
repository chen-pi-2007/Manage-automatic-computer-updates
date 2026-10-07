using UpdateHelper.App.Pages;
using Wpf.Ui.Controls;

namespace UpdateHelper.App;

public partial class MainWindow : FluentWindow
{
    private Type? _startPage;

    public MainWindow()
    {
        InitializeComponent();
        // 加载完成时只导航一次：紧接着连续导航两次，第二次会被导航动画吞掉（本机截图核对时发现）
        Loaded += (_, _) => Nav.Navigate(_startPage ?? typeof(HomePage));
    }

    /// <summary>打开某个页面。窗口还没加载时，记下来等加载完成再打开。</summary>
    public void ShowPage(Type pageType)
    {
        if (IsLoaded) Nav.Navigate(pageType);
        else _startPage = pageType;
    }
}
