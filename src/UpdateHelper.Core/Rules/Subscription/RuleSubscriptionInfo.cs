namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>给订阅页显示的当前状态。</summary>
public sealed record RuleSubscriptionInfo(
    string SourceUrl, int? CurrentVersion, int RuleCount, DateTimeOffset? LastSync, SyncStatus? LastStatus);
