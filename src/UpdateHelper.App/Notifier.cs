using Microsoft.Toolkit.Uwp.Notifications;

namespace UpdateHelper.App;

/// <summary>标准 Windows 通知：开着"请勿打扰"也会留在通知中心。点击通知带回 "page=updates"。</summary>
public static class Notifier
{
    /// <summary>点击通知时触发，参数是通知里带的 page 值（这里只有 "updates"）。</summary>
    public static event Action<string>? Activated;

    private static bool _hooked;

    public static void Show(string title, string message)
    {
        if (!_hooked)
        {
            ToastNotificationManagerCompat.OnActivated += e =>
            {
                var args = ToastArguments.Parse(e.Argument);
                if (args.TryGetValue("page", out var page)) Activated?.Invoke(page);
            };
            _hooked = true;
        }

        new ToastContentBuilder()
            .AddArgument("page", "updates")
            .AddText(title)
            .AddText(message)
            .Show();
    }

    /// <summary>退出时清理，避免通知残留回调。</summary>
    public static void Uninstall()
    {
        try { ToastNotificationManagerCompat.Uninstall(); } catch (Exception) { }
    }
}
