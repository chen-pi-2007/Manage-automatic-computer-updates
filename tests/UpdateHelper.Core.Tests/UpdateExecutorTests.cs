using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class UpdateExecutorTests
{
    // —— 假实现 ——
    private sealed class FakeInstaller(Func<InstallerReport> behave) : IPackageInstaller
    {
        public List<(string Id, InstallScopeHint Scope)> Calls { get; } = [];
        public string Name => "fake";

        public Task<InstallerReport> UpgradeAsync(string packageId, InstallScopeHint scope, IProgress<double>? progress,
            CancellationToken cancellationToken)
        {
            Calls.Add((packageId, scope));
            progress?.Report(1.0);
            return Task.FromResult(behave());
        }
    }

    private sealed class FakeVersions(Dictionary<string, string?> byKey) : IVersionProbe
    {
        public List<string> Asked { get; } = [];
        public string? GetInstalledVersion(string key)
        {
            Asked.Add(key);
            return byKey.TryGetValue(key, out var v) ? v : null;
        }
    }

    private sealed class FakeProcesses(params string[] running) : IRunningProcessProbe
    {
        public IReadOnlyList<string> FindRunningUnder(IReadOnlyList<string> directories) => running;
    }

    private sealed class MemoryHistory : IUpdateHistory
    {
        public List<HistoryRecord> Records { get; } = [];
        public void Append(HistoryRecord record) => Records.Add(record);
        public IReadOnlyList<HistoryRecord> ReadAll() => Records;
    }

    // —— 测试数据：Bandizip 7.30 → 7.46，装在 HKLM ——
    private static JudgedUpdate Update(UpdateTier tier = UpdateTier.Low, UninstallHive hive = UninstallHive.LocalMachine64,
        string[]? codes = null)
    {
        var group = SoftwareGrouper.Group(
            [Entry("Bandizip", "Bandisoft.com", "7.30", location: @"C:\Program Files\Bandizip", key: "Bandizip", hive: hive)],
            []).Groups.Single();
        var candidate = new UpdateCandidate("Bandisoft.Bandizip", "Bandizip", "Bandisoft.com", "7.30", "7.46",
            codes ?? ["bandizip"]);
        return new JudgedUpdate(candidate, group, tier, "小版本更新");
    }

    private static readonly InstallerReport Ok = new(true, false, null, null);

    private static (UpdateExecutor Executor, FakeInstaller Installer, FakeVersions Versions, MemoryHistory History) Make(
        Func<InstallerReport>? behave = null, string? versionAfter = "7.46", string[]? running = null)
    {
        var installer = new FakeInstaller(behave ?? (() => Ok));
        var versions = new FakeVersions(new() { ["Bandizip"] = versionAfter });
        var history = new MemoryHistory();
        return (new UpdateExecutor(installer, versions, new FakeProcesses(running ?? []), history), installer, versions, history);
    }

    private static Task<ExecuteResult> Run(UpdateExecutor e, JudgedUpdate u, bool automatic = false)
        => e.ExecuteAsync(u, automatic, null, CancellationToken.None);

    [Fact]
    public async Task Successful_update()
    {
        var (executor, installer, _, history) = Make();
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Succeeded, result.Outcome);
        Assert.Equal("已从 7.30 更新到 7.46", result.Message);
        Assert.Equal("7.46", result.VersionAfter);
        Assert.Equal(("Bandisoft.Bandizip", InstallScopeHint.Machine), Assert.Single(installer.Calls));
        var record = Assert.Single(history.Records);
        Assert.Equal(ExecuteOutcome.Succeeded, record.Outcome);
        Assert.False(record.Automatic);
        Assert.Equal("7.46", record.ToVersion);
    }

    [Fact]
    public async Task Current_user_install_keeps_user_scope()
    {
        var (executor, installer, _, _) = Make();
        await Run(executor, Update(hive: UninstallHive.CurrentUser));
        Assert.Equal(InstallScopeHint.User, installer.Calls.Single().Scope);
    }

    [Theory]
    [InlineData(UpdateTier.NeverAuto)]
    [InlineData(UpdateTier.Ignored)]
    public async Task Judge_refusals_are_respected(UpdateTier tier)
    {
        var (executor, installer, _, history) = Make();
        var result = await Run(executor, Update(tier));

        Assert.Equal(ExecuteOutcome.Refused, result.Outcome);
        Assert.Equal("不能更新：小版本更新", result.Message);
        Assert.Empty(installer.Calls);
        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task Running_program_blocks_install()   // Review Focus 3
    {
        var (executor, installer, _, history) = Make(running: ["Bandizip.exe", "bdzsfx.exe"]);
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Refused, result.Outcome);
        Assert.Equal("Bandizip 正在运行（Bandizip.exe、bdzsfx.exe），请先关闭再更新", result.Message);
        Assert.Empty(installer.Calls);
        Assert.Empty(history.Records);
    }

    [Fact]
    public async Task Installer_failure_is_recorded()
    {
        var (executor, _, _, history) = Make(() => new InstallerReport(false, false, "InstallError", 0x80070643));
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Failed, result.Outcome);
        Assert.Equal("安装失败：InstallError（错误码 0x80070643）", result.Message);
        Assert.Equal(ExecuteOutcome.Failed, Assert.Single(history.Records).Outcome);
    }

    [Fact]
    public async Task Installer_exception_becomes_failure()   // Review Focus 1
    {
        var (executor, _, _, history) = Make(() => throw new InvalidOperationException("RPC 服务器不可用"));
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Failed, result.Outcome);
        Assert.Equal("安装过程中出错：RPC 服务器不可用", result.Message);
        Assert.Single(history.Records);
    }

    [Fact]
    public async Task Cancellation_is_not_a_failure()   // Review Focus 5
    {
        var (executor, _, _, history) = Make(() => throw new OperationCanceledException());
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Cancelled, result.Outcome);
        Assert.Equal(ExecuteOutcome.Cancelled, Assert.Single(history.Records).Outcome);
    }

    [Fact]
    public async Task Unchanged_version_is_failure()
    {
        var (executor, _, _, _) = Make(versionAfter: "7.30");
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Failed, result.Outcome);
        Assert.Equal("安装程序报告成功，但版本没有变化（仍是 7.30）", result.Message);
    }

    [Fact]
    public async Task Missing_key_after_install_is_success_with_note()   // Review Focus 2
    {
        var (executor, _, _, _) = Make(versionAfter: null);
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.Succeeded, result.Outcome);
        Assert.Equal("已安装，但登记信息有变化，下次扫描时再确认版本", result.Message);
        Assert.Null(result.VersionAfter);
    }

    [Fact]
    public async Task Reboot_required_wins_over_version_check()
    {
        var (executor, _, _, history) = Make(() => new InstallerReport(true, true, null, null), versionAfter: "7.30");
        var result = await Run(executor, Update());

        Assert.Equal(ExecuteOutcome.NeedsReboot, result.Outcome);
        Assert.Equal("已安装，需要重启电脑才能完成（不会自动重启）", result.Message);
        Assert.Equal(ExecuteOutcome.NeedsReboot, Assert.Single(history.Records).Outcome);
    }

    [Fact]
    public async Task Probe_uses_matching_component_key()
    {
        var group = SoftwareGrouper.Group(
        [
            Entry("Python 3.10.2 (64-bit)", "Python Software Foundation", "3.10.2", key: "{PRIMARY}"),
            Entry("Python 3.10.2 Core Interpreter (64-bit)", "Python Software Foundation", hidden: true, key: "{CORE}"),
        ], []).Groups.Single();
        var update = new JudgedUpdate(
            new UpdateCandidate("Python.Python.3.10", "Python 3.10", null, "3.10.2", "3.10.11", ["{core}"]),
            group, UpdateTier.Low, "小版本更新");

        var installer = new FakeInstaller(() => Ok);
        var versions = new FakeVersions(new() { ["{CORE}"] = "3.10.11" });
        var executor = new UpdateExecutor(installer, versions, new FakeProcesses(), new MemoryHistory());

        var result = await Run(executor, update);

        Assert.Equal(ExecuteOutcome.Succeeded, result.Outcome);
        Assert.Equal("{CORE}", Assert.Single(versions.Asked));
    }

    private static HistoryRecord Past(ExecuteOutcome outcome, string toVersion = "7.46")
        => new(DateTimeOffset.Now, "Bandisoft.Bandizip", "Bandizip", "7.30", toVersion, outcome, "", true);

    [Fact]
    public async Task Automatic_mode_stops_after_two_failures_of_same_version()
    {
        var (executor, installer, _, history) = Make();
        history.Records.AddRange([Past(ExecuteOutcome.Failed), Past(ExecuteOutcome.Failed)]);

        var result = await Run(executor, Update(), automatic: true);

        Assert.Equal(ExecuteOutcome.Refused, result.Outcome);
        Assert.Equal("这个版本已经失败 2 次，不再自动重试", result.Message);
        Assert.Empty(installer.Calls);
    }

    [Fact]
    public async Task Retry_limit_ignores_other_versions_cancellations_and_manual_mode()
    {
        var (executor, installer, _, history) = Make();
        history.Records.AddRange([Past(ExecuteOutcome.Failed, "7.45"), Past(ExecuteOutcome.Failed), Past(ExecuteOutcome.Cancelled)]);

        Assert.Equal(ExecuteOutcome.Succeeded, (await Run(executor, Update(), automatic: true)).Outcome);

        history.Records.Add(Past(ExecuteOutcome.Failed));   // 现在同版本失败 2 次了
        Assert.Equal(ExecuteOutcome.Succeeded, (await Run(executor, Update(), automatic: false)).Outcome);  // 手动不受限
        Assert.Equal(2, installer.Calls.Count);
    }

    [Fact]
    public async Task Progress_is_forwarded()
    {
        var (executor, _, _, _) = Make();
        var seen = new List<double>();
        await executor.ExecuteAsync(Update(), false, new SyncProgress(seen.Add), CancellationToken.None);
        Assert.Equal(new[] { 1.0 }, seen);
    }

    /// <summary>Progress&lt;T&gt; 会切到别的线程回调，测试里用同步版本。</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
