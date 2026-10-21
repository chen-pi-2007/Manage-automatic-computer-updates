using System.Windows;
using UpdateHelper.Core.Grouping;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.App.Pages;

/// <summary>"卸载与残留"窗口：后台扫描残留，用户勾选后一键卸载并清理（清理前自动备份）。逻辑都在 LeftoverViewModel / 后端里。</summary>
public partial class LeftoversWindow : Window
{
    private readonly SoftwareGroup _group;
    private readonly bool _uninstallMode;   // true：从“卸载”按钮进入，什么都不勾也允许只运行自带卸载程序
    private LeftoverViewModel? _vm;
    private bool _loaded;

    public LeftoversWindow(SoftwareGroup group, bool uninstallMode)
    {
        _group = group;
        _uninstallMode = uninstallMode;
        InitializeComponent();
        TitleText.Text = uninstallMode ? $"卸载 {group.Name}" : $"{group.Name} 的残留";
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            var items = await Task.Run(() => AppHost.State.Backend.ScanLeftovers(_group));
            _vm = new LeftoverViewModel(items);
            Grid.ItemsSource = _vm.Rows;
            SummaryText.Text = _vm.Summary;
            DisclaimerText.Text = _vm.DisclaimerText;
            LoadingText.Visibility = Visibility.Collapsed;
            Grid.Visibility = Visibility.Visible;
            DeleteButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            LoadingText.Text = $"扫描残留失败：{ex.Message}";
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        Grid.CommitEdit(System.Windows.Controls.DataGridEditingUnit.Row, true);   // 让最后一次勾选先生效
        var count = _vm.SelectedCount;
        if (count == 0 && !_uninstallMode)
        {
            await AppHost.Confirm.InfoAsync("卸载与残留", "请先勾选要清理的残留项。");
            return;
        }

        var ask = count == 0
            ? $"将运行“{_group.Name}”自带的卸载程序，不清理残留。继续？"
            : $"将先运行“{_group.Name}”自带的卸载程序，再把勾选的 {count} 项残留备份后删除（可在“历史”页一键恢复）。继续？";
        if (!await AppHost.Confirm.ConfirmAsync("确认卸载", ask)) return;

        DeleteButton.IsEnabled = false;
        LoadingText.Text = "正在卸载并清理，请稍候……";
        LoadingText.Visibility = Visibility.Visible;
        Grid.Visibility = Visibility.Collapsed;
        try
        {
            var outcome = await _vm.UninstallAsync(AppHost.State.Backend, _group, CancellationToken.None, allowEmpty: _uninstallMode);
            await AppHost.Confirm.InfoAsync("卸载结果", outcome.Message);
            AppHost.History.ReloadCommand.Execute(null);   // 备份清单有了新的一条
            if (outcome.NativeUninstallRan || outcome.Removal.Removed > 0)
            {
                _ = AppHost.State.RefreshAsync();          // 软件列表要刷新
                Close();
                return;
            }
        }
        catch (Exception ex)
        {
            await AppHost.Confirm.InfoAsync("卸载失败", ex.Message);
        }
        LoadingText.Visibility = Visibility.Collapsed;
        Grid.Visibility = Visibility.Visible;
        DeleteButton.IsEnabled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
