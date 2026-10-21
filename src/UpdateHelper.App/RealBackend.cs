using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using UpdateHelper.Core.Agent;
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
    private readonly AgentController _agent = new();

    public AgentStatus GetAgentStatus() => _agent.GetStatus();
    public Task<string?> EnableAgentAsync() => _agent.EnableAsync();
    public Task<string?> DisableAgentAsync() => _agent.DisableAsync();

    // 启动兜底（spec §6.4）：创建时清理删源/改 URL 遗留的孤儿缓存（CleanOrphanCaches 内部已吞 IO/权限异常）
    private readonly RuleSourceStore _sourceStore = InitStore(sourceStore);

    private static RuleSourceStore InitStore(RuleSourceStore? given)
    {
        var store = given ?? new RuleSourceStore(RuleSourceStore.DefaultConfigPath, RuleSourceStore.DefaultCacheRoot);
        store.CleanOrphanCaches(store.Read());
        return store;
    }
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
        var rules = LoadRules();   // 官方 + 第三方，避免"能检测不能装"
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

    public IReadOnlyList<LeftoverItem> ScanLeftovers(UpdateHelper.Core.Grouping.SoftwareGroup group)
    {
        var snapshot = _lastSnapshot;   // 最近一次扫描；共用检测需要全量结果，没有则只用当前软件
        var rule = snapshot?.Rules.Rules.FirstOrDefault(r => r.Id == group.RuleId);
        var result = snapshot?.Result ?? new UpdateHelper.Core.Grouping.ScanResult([group], [], 1);
        return new LeftoverScanner(new RealFileProbe(), new RealRegistryProbe()).Scan(group, rule, result);
    }

    private static BackupStore NewBackupStore() => new(BackupStore.DefaultRoot, () => DateTimeOffset.Now);
    private static LeftoverRemover NewRemover() => new(new RealLeftoverMutator(), NewBackupStore(), () => DateTimeOffset.Now);

    public IReadOnlyList<BackupManifest> ReadBackups()
    {
        try { return NewBackupStore().ReadAll(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    public Task<int> RestoreBackupAsync(string backupId, CancellationToken ct) => Task.Run(() =>
    {
        var manifest = NewBackupStore().Find(backupId);
        return manifest is null ? 0 : NewRemover().Restore(manifest);
    }, ct);

    public async Task<UninstallOutcome> UninstallAsync(UpdateHelper.Core.Grouping.SoftwareGroup group,
        IReadOnlyList<LeftoverItem> selected, CancellationToken ct)
    {
        var none = new RemovalResult(0, 0, [], null);
        var viaAgent = UninstallRouting.UsesAgent(group.Primary?.Hive);

        // 机器级软件要靠后台助手清残留：没启用就先说清楚，别等卸载完才发现清不了
        if (viaAgent && selected.Count > 0)
        {
            AgentStatus status;
            try { status = await Task.Run(() => _agent.GetStatus(), ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { status = AgentStatus.NotEnabled; }
            if (status != AgentStatus.Ready)
                return new UninstallOutcome(false, none,
                    "这个软件是为所有用户安装的，清理它的残留需要管理员权限。请先到“设置”里启用“免确认更新”（后台助手），再回来重试。");
        }

        // 第一步：运行软件自带的卸载程序（它需要管理员时会自己弹确认框，和控制面板一样）
        var nativeRan = false;
        var nativeSkipped = true;
        if (group.Primary is { } primary)
        {
            var command = UninstallCommandParser.Parse(primary, new RealFileProbe(), preferSilent: false);
            if (command is not null)
            {
                nativeSkipped = false;
                try
                {
                    using var process = Process.Start(new ProcessStartInfo(command.ExecutablePath, command.Arguments) { UseShellExecute = true });
                    if (process is not null) await process.WaitForExitAsync(ct);
                    nativeRan = true;
                }
                catch (Win32Exception ex)
                {
                    // 用户在确认框点了“否”或程序启动不了：软件还在，不动它的残留
                    var why = ex.NativeErrorCode == 1223 ? "已取消卸载（在确认框里点了“否”）" : $"启动卸载程序失败：{ex.Message}";
                    return new UninstallOutcome(false, none, why + "。没有清理残留。");
                }
            }
        }

        if (selected.Count == 0)
            return new UninstallOutcome(nativeRan, none, UninstallMessages.Build(none, nativeRan, nativeSkipped));

        // 第二步：清理勾选的残留
        RemovalResult removal;
        try
        {
            removal = viaAgent
                ? await RemoveViaAgentAsync(group, selected, ct)
                : await Task.Run(() => NewRemover().Remove(group.Name, StillPresent(selected)), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = new RemovalResult(0, 0, selected.Select(i => i.Path).ToList(), null);
            return new UninstallOutcome(nativeRan, failed, $"清理残留时出错：{ex.Message}");
        }

        var message = UninstallMessages.Build(removal, nativeRan, nativeSkipped);
        if (viaAgent && nativeRan && removal.Removed + removal.RebootPending + removal.Failed.Count == 0)
            message += "后台助手找不到该软件的登记信息（卸载程序可能已把它清除），所以没能清理残留。";
        return new UninstallOutcome(nativeRan, removal, message);
    }

    /// <summary>原生卸载程序可能已经删掉了一部分：只处理还在的，免得把"已经没了"算成失败。</summary>
    private static IReadOnlyList<LeftoverItem> StillPresent(IReadOnlyList<LeftoverItem> items)
    {
        var files = new RealFileProbe();
        var registry = new RealRegistryProbe();
        return items.Where(i => i.Type switch
        {
            LeftoverType.RegistryKey => registry.KeyExists(i.Path),
            LeftoverType.InstallDir or LeftoverType.DataDir => files.DirectoryExists(i.Path),
            LeftoverType.File => files.FileExists(i.Path),
            _ => true,
        }).ToList();
    }

    private async Task<RemovalResult> RemoveViaAgentAsync(UpdateHelper.Core.Grouping.SoftwareGroup group,
        IReadOnlyList<LeftoverItem> selected, CancellationToken ct)
    {
        var primary = group.Primary ?? throw new InvalidOperationException("找不到该软件的卸载登记项");
        var request = new AgentRequest(AgentProtocol.Version, AgentOp.RemoveResidue,
            UninstallKeyName: primary.KeyName, Hive: primary.Hive, SelectedPaths: selected.Select(i => i.Path).ToList());
        var response = await _agent.SendAsync(request, ct);
        if (!response.Success)
            throw new InvalidOperationException(response.ErrorMessage ?? "后台助手没有完成清理");
        return new RemovalResult(response.RemovedCount ?? 0, response.RebootPendingCount ?? 0,
            Enumerable.Repeat("（由后台助手处理）", response.FailedCount ?? 0).ToList(), null);
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
        var svc = new RuleSyncService(new MirrorFallbackDownloader(new HttpDownloader()),
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
        var svc = new ThirdPartySyncService(new MirrorFallbackDownloader(new HttpDownloader()), _sourceStore);
        return Task.Run(() => svc.SyncAsync(enabled, ct), ct);
    }

    public async Task<ThirdPartyTrialResult> TrySyncSourceAsync(ThirdPartySource source, CancellationToken ct)
    {
        var svc = new ThirdPartySyncService(new MirrorFallbackDownloader(new HttpDownloader()), _sourceStore);
        await Task.Run(() => svc.SyncAsync([source], ct), ct);
        var state = ThirdPartySyncService.ReadState(_sourceStore, source.Id);
        if (state is { Status: ThirdPartySyncStatus.Updated, RuleCount: > 0 })
            return new ThirdPartyTrialResult(true, state.RuleCount, "");
        // 没成功：清掉这次试拉留下的缓存目录（它不在配置里，属孤儿），别留垃圾
        _sourceStore.CleanOrphanCaches(_sourceStore.Read());
        var msg = state?.Status switch
        {
            ThirdPartySyncStatus.NetworkFailed => "连不上这个地址，没有添加。请检查链接是否正确，或网络 / 代理是否正常。",
            ThirdPartySyncStatus.Empty => "这个地址没有解析出任何规则，没有添加。请确认它是规则 YAML 文件。",
            _ => "拉取失败，没有添加。",
        };
        return new ThirdPartyTrialResult(false, 0, msg);
    }
}
