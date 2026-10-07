using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation;

/// <summary>一次扫描的结果（已套用规则），以及后台项目说明和扫描时的警告。</summary>
public sealed record ScanSnapshot(
    ScanResult Result,
    RuleSet Rules,
    IReadOnlyDictionary<BackgroundItem, string> Explanations,
    IReadOnlyList<string> Warnings);

/// <summary>界面使用底层的唯一入口。真实实现在 App 项目（RealBackend），测试用 FakeBackend。</summary>
public interface IAppBackend
{
    Task<ScanSnapshot> ScanAsync(CancellationToken cancellationToken);
    Task<UpdateReport> CheckUpdatesAsync(ScanSnapshot snapshot, CancellationToken cancellationToken);
    Task<ExecuteResult> InstallAsync(JudgedUpdate update, IProgress<double>? progress, CancellationToken cancellationToken);
    IReadOnlyList<HistoryRecord> ReadHistory();
}
