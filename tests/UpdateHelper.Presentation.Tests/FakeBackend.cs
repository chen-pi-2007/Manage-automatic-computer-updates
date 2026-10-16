using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Rules.Subscription;
using UpdateHelper.Core.Rules.ThirdParty;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Security;
using UpdateHelper.Core.Uninstall;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation.Tests;

/// <summary>可控制的假底层：返回固定结果，可以让扫描"卡住"直到测试放行。</summary>
public sealed class FakeBackend : IAppBackend
{
    public ScanSnapshot Snapshot { get; set; } = MakeSnapshot([]);
    public UpdateReport Report { get; set; } = new([], null);
    public Exception? ScanError { get; set; }
    public TaskCompletionSource? ScanGate { get; set; }
    public Func<JudgedUpdate, ExecuteResult> Install { get; set; } =
        u => new ExecuteResult(ExecuteOutcome.Succeeded, $"已从 {u.Candidate.InstalledVersion} 更新到 {u.Candidate.AvailableVersion}", u.Candidate.AvailableVersion);
    public List<HistoryRecord> History { get; } = [];

    public AgentStatus AgentStatus { get; set; } = AgentStatus.NotEnabled;
    public Func<AgentStatus>? OnGetAgentStatus { get; set; }
    public Func<string?> OnEnableAgent { get; set; } = () => null;
    public Func<string?> OnDisableAgent { get; set; } = () => null;

    public int AgentStatusCalls { get; private set; }
    public AgentStatus GetAgentStatus() { AgentStatusCalls++; return OnGetAgentStatus?.Invoke() ?? AgentStatus; }
    public Task<string?> EnableAgentAsync() => Task.FromResult(OnEnableAgent());
    public Task<string?> DisableAgentAsync() => Task.FromResult(OnDisableAgent());

    public int ScanCalls { get; private set; }
    public List<string> Installed { get; } = [];

    public async Task<ScanSnapshot> ScanAsync(CancellationToken cancellationToken)
    {
        ScanCalls++;
        if (ScanGate is not null) await ScanGate.Task;
        if (ScanError is not null) throw ScanError;
        return Snapshot;
    }

    public Task<UpdateReport> CheckUpdatesAsync(ScanSnapshot snapshot, CancellationToken cancellationToken)
        => Task.FromResult(Report);

    public Task<ExecuteResult> InstallAsync(JudgedUpdate update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        Installed.Add(update.Candidate.PackageId);
        return Task.FromResult(Install(update));
    }

    public IReadOnlyList<HistoryRecord> ReadHistory() => History;

    public Func<SoftwareGroup, IReadOnlyList<LeftoverItem>> OnScanLeftovers { get; set; } = _ => [];
    public IReadOnlyList<LeftoverItem> ScanLeftovers(SoftwareGroup group) => OnScanLeftovers(group);

    // —— 构造测试数据 ——

    public static UninstallEntry Entry(string name, string? version = "1.0", string? publisher = "Pub",
        string? location = null, bool hidden = false)
        => new(name, UninstallHive.LocalMachine64, name, version, publisher, location, null, null, hidden, null, null, null);

    public static ScanSnapshot MakeSnapshot(IReadOnlyList<UninstallEntry> entries, IReadOnlyList<BackgroundItem>? background = null,
        IReadOnlyDictionary<BackgroundItem, string>? explanations = null, IReadOnlyList<string>? warnings = null)
        => new(SoftwareGrouper.Group(entries, background ?? []), RuleSet.Empty,
               explanations ?? new Dictionary<BackgroundItem, string>(), warnings ?? []);

    public static JudgedUpdate Update(string name, UpdateTier tier, string reason = "小版本更新", SoftwareGroup? group = null)
        => new(new UpdateCandidate("Pkg." + name, name, null, "1.0", "1.1", [name]), group, tier, reason);

    public Func<SecurityStatus> OnGetSecurityStatus { get; set; } =
        () => new SecurityStatus(AntivirusState.Protected, "Windows Defender", "Windows Defender 正在保护", "");
    public SecurityStatus GetSecurityStatus() => OnGetSecurityStatus();

    public Func<RuleSubscriptionInfo>? OnGetRuleSubscriptionInfo { get; set; }
    public Func<SyncOutcome>? OnSyncRules { get; set; }
    public Func<DateTimeOffset?>? OnGetRuleLastSync { get; set; }
    public DateTimeOffset? GetRuleLastSync() => OnGetRuleLastSync?.Invoke();
    public RuleSubscriptionInfo GetRuleSubscriptionInfo() =>
        OnGetRuleSubscriptionInfo?.Invoke() ?? new RuleSubscriptionInfo("", null, 0, null, null);
    public Task<SyncOutcome> SyncRulesAsync(CancellationToken ct) =>
        Task.FromResult(OnSyncRules?.Invoke() ?? new SyncOutcome(SyncStatus.AlreadyLatest, null, DateTimeOffset.Now, null));

    public ThirdPartyConfig ThirdPartyConfig { get; set; } = ThirdPartyConfig.Default;
    public bool SyncThirdPartyCalled { get; private set; }
    public List<ThirdPartySourceStatus> ThirdPartyStatuses { get; } = [];

    public ThirdPartyConfig GetThirdPartyConfig() => ThirdPartyConfig;
    public void SaveThirdPartyConfig(ThirdPartyConfig config) => ThirdPartyConfig = config;
    public IReadOnlyList<ThirdPartySourceStatus> GetThirdPartySourceStatuses() =>
        ThirdPartyStatuses.Count > 0
            ? ThirdPartyStatuses
            : ThirdPartyConfig.Sources
                .Select(s => new ThirdPartySourceStatus(s.Id, s.Url, s.Note, s.Enabled, s.AllowOverrideOfficial, 0, "未同步"))
                .ToList();
    public TaskCompletionSource? SyncGate { get; set; }
    public int SyncThirdPartyCount { get; private set; }
    public async Task SyncThirdPartyAsync(CancellationToken ct)
    {
        SyncThirdPartyCalled = true;
        SyncThirdPartyCount++;
        if (SyncGate is not null) await SyncGate.Task;
    }

    public bool TrySyncCalled { get; private set; }
    public ThirdPartySource? LastTriedSource { get; private set; }
    public ThirdPartyTrialResult TrialResult { get; set; } = new(true, 1, "");   // 默认试拉成功
    public Task<ThirdPartyTrialResult> TrySyncSourceAsync(ThirdPartySource source, CancellationToken ct)
    {
        TrySyncCalled = true;
        LastTriedSource = source;
        return Task.FromResult(TrialResult);
    }
}
