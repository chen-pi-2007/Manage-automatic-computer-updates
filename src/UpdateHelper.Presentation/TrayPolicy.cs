using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation;

/// <summary>托盘相关的两个判断：要不要弹通知、关窗口时是缩到托盘还是退出。</summary>
public static class TrayPolicy
{
    /// <summary>需要弹通知时返回文字，否则返回 null。窗口可见时不弹，用户已经看到了。</summary>
    public static string? NotificationText(IReadOnlyList<JudgedUpdate> updates, bool windowVisible)
    {
        if (windowVisible) return null;
        var relevant = updates.Where(u => u.Tier != UpdateTier.Ignored).ToList();
        if (relevant.Count == 0) return null;
        var low = relevant.Count(u => u.Tier == UpdateTier.Low);
        return $"发现 {relevant.Count} 个更新，其中 {low} 个低风险。点击查看。";
    }

    /// <summary>托盘开着且不是用户主动"退出"时，关窗口只是缩到托盘。</summary>
    public static bool HideInsteadOfClose(bool trayEnabled, bool exitRequested) => trayEnabled && !exitRequested;
}
