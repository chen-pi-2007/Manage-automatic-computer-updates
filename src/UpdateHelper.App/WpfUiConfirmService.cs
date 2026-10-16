using UpdateHelper.Presentation;
using Wpf.Ui.Controls;

namespace UpdateHelper.App;

/// <summary>用 WPF-UI 的 MessageBox（主题化、非系统阻塞弹窗）实现确认/提示。始终在 UI 线程显示。</summary>
public sealed class WpfUiConfirmService : IConfirmService
{
    public Task<bool> ConfirmAsync(string title, string message) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
            };
            var result = await box.ShowDialogAsync();
            return result == Wpf.Ui.Controls.MessageBoxResult.Primary;
        }).Task.Unwrap();

    public Task InfoAsync(string title, string message) =>
        System.Windows.Application.Current.Dispatcher.InvokeAsync(async () =>
        {
            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = title,
                Content = message,
                CloseButtonText = "知道了",
            };
            await box.ShowDialogAsync();
        }).Task.Unwrap();
}
