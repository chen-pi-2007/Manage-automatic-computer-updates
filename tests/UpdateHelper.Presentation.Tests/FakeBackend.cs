using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
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

    public AgentStatus GetAgentStatus() => OnGetAgentStatus?.Invoke() ?? AgentStatus;
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
}
