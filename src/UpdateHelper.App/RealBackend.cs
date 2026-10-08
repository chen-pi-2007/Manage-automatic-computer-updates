using System.IO;
using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Security;
using UpdateHelper.Core.Updates;
using UpdateHelper.Presentation;
using UpdateHelper.Winget;

namespace UpdateHelper.App;

/// <summary>IAppBackend 的真实实现：组合计划 1～5 的底层。所有耗时工作都放到后台线程。</summary>
public sealed class RealBackend(string rulesDirectory) : IAppBackend
{
    private readonly AgentController _agent = new();

    public AgentStatus GetAgentStatus() => _agent.GetStatus();
    public Task<string?> EnableAgentAsync() => _agent.EnableAsync();
    public Task<string?> DisableAgentAsync() => _agent.DisableAsync();

    public Task<ScanSnapshot> ScanAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var scan = SystemScanner.CreateDefault().Scan();
        var rules = RuleSetLoader.Load([new RuleSource(rulesDirectory, RuleTrust.Official)]);
        var applied = RuleApplier.Apply(scan.Result, rules);
        var warnings = scan.Warnings.Concat(rules.Errors.Select(e => $"规则错误：{e}")).ToList();
        return new ScanSnapshot(applied.Result, rules, applied.Explanations, warnings);
    }, cancellationToken);

    public Task<UpdateReport> CheckUpdatesAsync(ScanSnapshot snapshot, CancellationToken cancellationToken) => Task.Run(() =>
        UpdateService.Check(
            [new WingetUpdateSource(), new RuleUpdateSource(snapshot.Result, snapshot.Rules)],   // winget 优先
            snapshot.Result, snapshot.Rules), cancellationToken);

    public Task<ExecuteResult> InstallAsync(JudgedUpdate update, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var rules = RuleSetLoader.Load([new RuleSource(rulesDirectory, RuleTrust.Official)]);
        return Task.Run(() =>
        {
            // 启用了免确认更新：winget 更新交给后台助手（不弹确认框）；规则库更新仍在本进程（每次确认）。
            // 状态查询会调 schtasks，所以放在后台线程；查询失败就按没启用处理。
            AgentStatus status;
            try { status = _agent.GetStatus(); }
            catch (Exception) { status = AgentStatus.NotEnabled; }
            IPackageInstaller winget = status == AgentStatus.Ready ? _agent.CreateInstaller() : new WingetInstaller();
            var installer = new InstallerRouter(winget,
                new RuleInstaller(rules, new HttpDownloader(), new AuthenticodeVerifier(), new ProcessRunner(),
                    RuleInstaller.DefaultDownloadDirectory));
            var executor = new UpdateExecutor(installer, new RegistryVersionProbe(), new RunningProcessProbe(),
                new JsonLinesUpdateHistory(JsonLinesUpdateHistory.DefaultPath));
            return executor.ExecuteAsync(update, automatic: false, progress, cancellationToken);
        }, cancellationToken);
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
