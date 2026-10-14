using System.IO;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Rules.Subscription;
using UpdateHelper.Core.Rules.ThirdParty;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Security;
using UpdateHelper.Core.Uninstall;
using UpdateHelper.Core.Updates;
using UpdateHelper.Presentation;
using UpdateHelper.Winget;

namespace UpdateHelper.App;

/// <summary>IAppBackend 的真实实现：组合计划 1～5 的底层。所有耗时工作都放到后台线程。</summary>
public sealed class RealBackend(string rulesDirectory, RuleSourceStore? sourceStore = null) : IAppBackend
{
    private readonly RuleSourceStore _sourceStore =
        sourceStore ?? new RuleSourceStore(RuleSourceStore.DefaultConfigPath, RuleSourceStore.DefaultCacheRoot);
    private volatile ScanSnapshot? _lastSnapshot;

    /// <summary>官方规则（内置/缓存择新）+ 所有已启用的第三方源。扫描和安装共用，避免"能检测不能装"。</summary>
    private RuleSet LoadRules() => RuleSetLoader.Load(BuildSources());

    private IReadOnlyList<RuleSource> BuildSources()
    {
        var officialDir = OfficialRules.SelectDirectory(rulesDirectory, RuleSubscriptionConfig.DefaultCacheDirectory);
        var sources = new List<RuleSource> { new(officialDir, RuleTrust.Official) };
        sources.AddRange(_sourceStore.BuildRuleSources(_sourceStore.Read()));
        return sources;
    }

    public Task<ScanSnapshot> ScanAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        var scan = SystemScanner.CreateDefault().Scan();
        var rules = LoadRules();
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
        var rules = LoadRules();
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

    public SecurityStatus GetSecurityStatus()
    {
        try
        {
            var probe = new RealSecurityProbe();
            return SecurityJudge.Evaluate(probe.QueryRegistered(), probe.QueryDefender());
        }
        catch (Exception)
        {
            return new SecurityStatus(AntivirusState.Unknown, "", "无法读取安全状态", "可以在 Windows 安全中心查看");
        }
    }

    public RuleSubscriptionInfo GetRuleSubscriptionInfo()
    {
        var cache = new RuleCacheStore(RuleSubscriptionConfig.DefaultCacheDirectory);
        var officialDir = OfficialRules.SelectDirectory(rulesDirectory, cache.Directory);
        var version = OfficialRules.TryReadManifest(officialDir)?.BundleVersion;
        var count = RuleSetLoader.Load([new RuleSource(officialDir, RuleTrust.Official)]).Rules.Count;
        var state = cache.ReadSyncState();
        SyncStatus? status = state is { } s && Enum.TryParse<SyncStatus>(s.Status, out var ss) ? ss : null;
        return new RuleSubscriptionInfo(RuleSubscriptionConfig.BundleUrl, version, count, state?.When, status);
    }

    public DateTimeOffset? GetRuleLastSync() =>
        new RuleCacheStore(RuleSubscriptionConfig.DefaultCacheDirectory).ReadSyncState()?.When;

    public Task<SyncOutcome> SyncRulesAsync(CancellationToken ct)
    {
        var svc = new RuleSyncService(new HttpDownloader(),
            new BundleVerifier(RuleSubscriptionConfig.PublicKeyPem),
            new RuleCacheStore(RuleSubscriptionConfig.DefaultCacheDirectory),
            RuleSubscriptionConfig.BundleUrl, RuleSubscriptionConfig.SignatureUrl);
        return svc.SyncAsync(ct);
    }

    public ThirdPartyConfig GetThirdPartyConfig() => _sourceStore.Read();

    public void SaveThirdPartyConfig(ThirdPartyConfig config)
    {
        _sourceStore.Write(config);
        _sourceStore.CleanOrphanCaches(config);   // 删源 / 改 URL 的残留一并清理
    }

    public IReadOnlyList<ThirdPartySourceStatus> GetThirdPartySourceStatuses()
    {
        var config = _sourceStore.Read();
        var list = new List<ThirdPartySourceStatus>();
        foreach (var s in config.Sources)
        {
            var state = ThirdPartySyncService.ReadState(_sourceStore, s.Id);
            var statusText = state?.Status switch
            {
                ThirdPartySyncStatus.Updated => $"上次同步：{state.When.LocalDateTime:yyyy-MM-dd HH:mm}",
                ThirdPartySyncStatus.NetworkFailed => "上次同步失败（连不上），仍用上次缓存",
                ThirdPartySyncStatus.Empty => "源内容为空",
                _ => "尚未同步",
            };
            list.Add(new ThirdPartySourceStatus(s.Id, s.Url, s.Note, s.Enabled, s.AllowOverrideOfficial,
                state?.RuleCount ?? 0, statusText));
        }
        return list;
    }

    public Task SyncThirdPartyAsync(CancellationToken ct)
    {
        var config = _sourceStore.Read();
        if (!config.ThirdPartyEnabled) return Task.CompletedTask;
        var enabled = config.Sources.Where(s => s.Enabled).ToList();
        if (enabled.Count == 0) return Task.CompletedTask;
        var svc = new ThirdPartySyncService(new HttpDownloader(), _sourceStore);
        return Task.Run(() => svc.SyncAsync(enabled, ct), ct);
    }
}
