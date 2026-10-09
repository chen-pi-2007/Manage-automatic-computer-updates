using System.IO;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Security;
using UpdateHelper.Core.Uninstall;
using UpdateHelper.Core.Updates;
using UpdateHelper.Presentation;
using UpdateHelper.Winget;

namespace UpdateHelper.App;

/// <summary>IAppBackend 的真实实现：组合计划 1～5 的底层。所有耗时工作都放到后台线程。</summary>
public sealed class RealBackend(string rulesDirectory) : IAppBackend
{
    private ScanSnapshot? _lastSnapshot;

    public Task<ScanSnapshot> ScanAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var scan = SystemScanner.CreateDefault().Scan();
        var rules = RuleSetLoader.Load([new RuleSource(rulesDirectory, RuleTrust.Official)]);
        var applied = RuleApplier.Apply(scan.Result, rules);
        var warnings = scan.Warnings.Concat(rules.Errors.Select(e => $"规则错误：{e}")).ToList();
        var snapshot = new ScanSnapshot(applied.Result, rules, applied.Explanations, warnings);
        _lastSnapshot = snapshot;
        return snapshot;
    }, cancellationToken);

    public Task<UpdateReport> CheckUpdatesAsync(ScanSnapshot snapshot, CancellationToken cancellationToken) => Task.Run(() =>
        UpdateService.Check(
            [new WingetUpdateSource(), new RuleUpdateSource(snapshot.Result, snapshot.Rules)],   // winget 优先
            snapshot.Result, snapshot.Rules), cancellationToken);

    public Task<ExecuteResult> InstallAsync(JudgedUpdate update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var rules = RuleSetLoader.Load([new RuleSource(rulesDirectory, RuleTrust.Official)]);
        var installer = new InstallerRouter(new WingetInstaller(),
            new RuleInstaller(rules, new HttpDownloader(), new AuthenticodeVerifier(), new ProcessRunner(),
                RuleInstaller.DefaultDownloadDirectory));
        var executor = new UpdateExecutor(installer, new RegistryVersionProbe(), new RunningProcessProbe(),
            new JsonLinesUpdateHistory(JsonLinesUpdateHistory.DefaultPath));
        return Task.Run(() => executor.ExecuteAsync(update, automatic: false, progress, cancellationToken), cancellationToken);
    }

    public IReadOnlyList<LeftoverItem> ScanLeftovers(UpdateHelper.Core.Grouping.SoftwareGroup group)
    {
        var snapshot = _lastSnapshot;   // 最近一次扫描；共用检测需要全量结果，没有则只用当前软件
        var rule = snapshot?.Rules.Rules.FirstOrDefault(r => r.Id == group.RuleId);
        var result = snapshot?.Result ?? new UpdateHelper.Core.Grouping.ScanResult([group], [], 1);
        return new LeftoverScanner(new RealFileProbe(), new RealRegistryProbe()).Scan(group, rule, result);
    }

    public IReadOnlyList<HistoryRecord> ReadHistory()
    {
        try
        {
            return new JsonLinesUpdateHistory(JsonLinesUpdateHistory.DefaultPath).ReadAll();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
