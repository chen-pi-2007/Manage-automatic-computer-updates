using Microsoft.Toolkit.Uwp.Notifications;

namespace UpdateHelper.App;

/// <summary>标准 Windows 通知：开着"请勿打扰"也会留在通知中心。点击通知带回 "page=updates"。</summary>
public static class Notifier
{
    /// <summary>点击通知时触发，参数是通知里带的 page 值（这里只有 "updates"）。</summary>
    public static event Action<string>? Activated;

    private static bool _hooked;

    /// <summary>启动时调用一次：挂上通知点击处理（程序在托盘里时点击通知也要能跳页面）。</summary>
    public static void EnsureHooked()
    {
        if (_hooked) return;
        ToastNotificationManagerCompat.OnActivated += e =>
        {
            var args = ToastArguments.Parse(e.Argument);
            if (args.TryGetValue("page", out var page)) Activated?.Invoke(page);
        };
        _hooked = true;
    }

    public static void Show(string title, string message)
    {
        EnsureHooked();

        new ToastContentBuilder()
            .AddArgument("page", "updates")
            .AddText(title)
            .AddText(message)
            .Show();
    }
}
