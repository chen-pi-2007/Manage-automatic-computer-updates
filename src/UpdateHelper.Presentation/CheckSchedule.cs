using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation;

/// <summary>托盘里的定时检查：现在该不该自动检查一次（spec 第 6、18 节）。</summary>
public static class CheckSchedule
{
    /// <summary>开机自动启动后，等这么久再做第一次检查，避免和开机时的其他程序抢资源。</summary>
    public static readonly TimeSpan LoginDelay = TimeSpan.FromMinutes(5);

    public static bool ShouldCheck(DateTimeOffset now, DateTimeOffset startedAt, bool startedAtLogin,
        DateTimeOffset? lastChecked, AppSettings settings, bool busy)
    {
        if (busy) return false;
        if (settings.Mode == UpdateMode.OnOpenOnly) return false;
        if (lastChecked is null) return !startedAtLogin || now - startedAt >= LoginDelay;
        return now - lastChecked.Value >= TimeSpan.FromHours(settings.CheckIntervalHours);
    }
}
