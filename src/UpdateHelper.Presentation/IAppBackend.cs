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

namespace UpdateHelper.Presentation;

/// <summary>一次扫描的结果（已套用规则），以及后台项目说明和扫描时的警告。</summary>
public sealed record ScanSnapshot(
    ScanResult Result,
    RuleSet Rules,
    IReadOnlyDictionary<BackgroundItem, string> Explanations,
    IReadOnlyList<string> Warnings);

/// <summary>第三方源在界面上要显示的状态。</summary>
public sealed record ThirdPartySourceStatus(
    string Id, string Url, string Note, bool Enabled, bool AllowOverrideOfficial, int RuleCount, string StatusText);

/// <summary>「添加前先试拉」的结果：Ok=true 表示拉到 ≥1 条规则、可以加入；否则 Message 说明为什么没加。</summary>
public sealed record ThirdPartyTrialResult(bool Ok, int RuleCount, string Message);

/// <summary>一次卸载的结果：原生卸载程序是否运行过、残留清理结果、给用户看的中文总结。</summary>
public sealed record UninstallOutcome(bool NativeUninstallRan, RemovalResult Removal, string Message);

/// <summary>界面使用底层的唯一入口。真实实现在 App 项目（RealBackend），测试用 FakeBackend。</summary>
public interface IAppBackend
{
    Task<ScanSnapshot> ScanAsync(CancellationToken cancellationToken);
    Task<UpdateReport> CheckUpdatesAsync(ScanSnapshot snapshot, CancellationToken cancellationToken);
    Task<ExecuteResult> InstallAsync(JudgedUpdate update, IProgress<double>? progress, CancellationToken cancellationToken);
    IReadOnlyList<HistoryRecord> ReadHistory();

    /// <summary>后台助手的状态；查询本身失败时抛异常（由界面显示原因）。</summary>
    AgentStatus GetAgentStatus();

    /// <summary>启用免确认更新（弹一次管理员确认框）。null = 成功，否则是中文原因。</summary>
    Task<string?> EnableAgentAsync();

    /// <summary>关闭免确认更新（弹一次管理员确认框）。null = 成功，否则是中文原因。</summary>
    Task<string?> DisableAgentAsync();

    /// <summary>扫描一个软件的卸载残留（只读，不删除）。</summary>
    IReadOnlyList<LeftoverItem> ScanLeftovers(SoftwareGroup group);

    /// <summary>卸载一个软件：先运行它自带的卸载程序（找不到则跳过），再清理勾选的残留（先备份）。不抛异常，结果在返回值里。</summary>
    Task<UninstallOutcome> UninstallAsync(SoftwareGroup group, IReadOnlyList<LeftoverItem> selected, CancellationToken ct);

    /// <summary>读取残留备份清单（新的在前）。</summary>
    IReadOnlyList<BackupManifest> ReadBackups();

    /// <summary>按备份 Id 一键恢复，返回成功恢复的条数。</summary>
    Task<int> RestoreBackupAsync(string backupId, CancellationToken ct);

    /// <summary>查询当前的杀毒软件保护状态（只读）。</summary>
    SecurityStatus GetSecurityStatus();

    /// <summary>读取规则订阅的当前状态（只读）。</summary>
    RuleSubscriptionInfo GetRuleSubscriptionInfo();
    /// <summary>只读上次同步时间（小 JSON，不解析规则），供每日调度轻量判断。</summary>
    DateTimeOffset? GetRuleLastSync();

    /// <summary>从官方源同步规则包；失败不抛，结果在返回值里。</summary>
    Task<SyncOutcome> SyncRulesAsync(CancellationToken ct);

    /// <summary>读取第三方源配置（缺失/损坏回退默认）。</summary>
    ThirdPartyConfig GetThirdPartyConfig();

    /// <summary>保存第三方源配置，并清理不再引用的缓存目录。</summary>
    void SaveThirdPartyConfig(ThirdPartyConfig config);

    /// <summary>读取各第三方源的展示状态（规则数、上次同步结果）。</summary>
    IReadOnlyList<ThirdPartySourceStatus> GetThirdPartySourceStatuses();

    /// <summary>同步所有启用的第三方源；失败按源隔离，不抛。</summary>
    Task SyncThirdPartyAsync(CancellationToken ct);

    /// <summary>添加前先试拉一个候选源（不写入配置）：拉到 ≥1 条规则才算成功。失败不抛，原因在返回值里。</summary>
    Task<ThirdPartyTrialResult> TrySyncSourceAsync(ThirdPartySource source, CancellationToken ct);
}
