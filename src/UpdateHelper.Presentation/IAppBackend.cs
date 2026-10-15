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

/// <summary>界面使用底层的唯一入口。真实实现在 App 项目（RealBackend），测试用 FakeBackend。</summary>
public interface IAppBackend
{
    Task<ScanSnapshot> ScanAsync(CancellationToken cancellationToken);
    Task<UpdateReport> CheckUpdatesAsync(ScanSnapshot snapshot, CancellationToken cancellationToken);
    Task<ExecuteResult> InstallAsync(JudgedUpdate update, IProgress<double>? progress, CancellationToken cancellationToken);
    IReadOnlyList<HistoryRecord> ReadHistory();

    /// <summary>扫描一个软件的卸载残留（只读，不删除）。</summary>
    IReadOnlyList<LeftoverItem> ScanLeftovers(SoftwareGroup group);

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
}
