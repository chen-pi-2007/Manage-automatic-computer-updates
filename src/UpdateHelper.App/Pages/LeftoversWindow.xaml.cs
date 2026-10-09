using System.Windows;
using UpdateHelper.Core.Grouping;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.App.Pages;

/// <summary>"查看残留"只读预览窗口：后台扫描，完成后显示表格。</summary>
public partial class LeftoversWindow : Window
{
    private readonly SoftwareGroup _group;

    public LeftoversWindow(SoftwareGroup group)
    {
        _group = group;
        InitializeComponent();
        TitleText.Text = $"{group.Name} 的残留";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var items = await Task.Run(() => AppHost.State.Backend.ScanLeftovers(_group));
            var vm = new LeftoverViewModel(items);
            Grid.ItemsSource = vm.Rows;
            SummaryText.Text = vm.Summary;
            DisclaimerText.Text = vm.DisclaimerText;
            LoadingText.Visibility = Visibility.Collapsed;
            Grid.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"扫描残留失败：{ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
