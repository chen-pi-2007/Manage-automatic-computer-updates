namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>规则每日同步判断：距上次 ≥ 24 小时、或从未同步 → 该同步。</summary>
public static class RuleSyncSchedule
{
    public static bool ShouldSync(DateTimeOffset now, DateTimeOffset? lastSync) =>
        lastSync is not { } t || now - t >= TimeSpan.FromHours(24);
}
