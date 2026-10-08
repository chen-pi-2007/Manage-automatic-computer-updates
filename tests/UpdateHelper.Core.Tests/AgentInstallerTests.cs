using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public sealed class AgentInstallerTests
{
    private sealed class SlowInstaller(TimeSpan delay) : IPackageInstaller
    {
        public string Name => "slow";
        public async Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
            IProgress<double>? progress, CancellationToken cancellationToken)
        {
            await Task.Delay(delay, cancellationToken);
            return new InstallerReport(true, true, null, null);
        }
    }

    /// <summary>被调用时才启动服务端，模拟"计划任务把 Agent 拉起来"。</summary>
    private sealed class FakeLauncher(Action start) : IAgentLauncher
    {
        public int Starts { get; private set; }
        public void Start() { Starts++; start(); }
    }

    private static string UniquePipe() => $"UpdateHelper.Test.{Guid.NewGuid():N}";

    [Fact]
    public async Task AgentInstaller_starts_agent_on_demand_and_returns_report()
    {
        var pipe = UniquePipe();
        using var stop = new CancellationTokenSource();
        Task? running = null;
        var launcher = new FakeLauncher(() => running = new AgentPipeServer(pipe,
            new AgentRequestHandler(new SlowInstaller(TimeSpan.FromMilliseconds(10)), true, "test")).RunAsync(TimeSpan.FromSeconds(30), stop.Token));
        var installer = new AgentInstaller(launcher, new AgentPipeClient(pipe, Environment.ProcessPath!), TimeSpan.FromSeconds(5));

        var report = await installer.UpgradeAsync("Tencent.QQ", "9.9.21", InstallScopeHint.User, null, CancellationToken.None);

        Assert.Equal(1, launcher.Starts);
        Assert.Equal(new InstallerReport(true, true, null, null), report);
        stop.Cancel();
        await running!;
    }

    [Fact]
    public async Task AgentInstaller_fails_fast_when_agent_never_starts()
    {
        var launcher = new FakeLauncher(() => { });
        var installer = new AgentInstaller(launcher, new AgentPipeClient(UniquePipe(), Environment.ProcessPath!), TimeSpan.FromMilliseconds(500));
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var report = await installer.UpgradeAsync("Tencent.QQ", "9.9.21", InstallScopeHint.User, null, CancellationToken.None);

        Assert.False(report.Success);
        Assert.Contains("后台助手没有响应", report.ErrorMessage);
        Assert.Contains("设置", report.ErrorMessage);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AgentInstaller_reports_launcher_errors()
    {
        var launcher = new FakeLauncher(() => throw new InvalidOperationException("找不到计划任务"));
        var installer = new AgentInstaller(launcher, new AgentPipeClient(UniquePipe(), Environment.ProcessPath!), TimeSpan.FromMilliseconds(300));

        var report = await installer.UpgradeAsync("Tencent.QQ", "9.9.21", InstallScopeHint.User, null, CancellationToken.None);

        Assert.False(report.Success);
        Assert.Contains("找不到计划任务", report.ErrorMessage);
    }

    [Fact]
    public async Task AgentInstaller_cancellation_throws_operation_canceled()
    {
        var pipe = UniquePipe();
        using var stop = new CancellationTokenSource();
        var running = new AgentPipeServer(pipe, new AgentRequestHandler(new SlowInstaller(TimeSpan.FromSeconds(30)), true, "test"))
            .RunAsync(TimeSpan.FromSeconds(30), stop.Token);
        var installer = new AgentInstaller(new FakeLauncher(() => { }), new AgentPipeClient(pipe, Environment.ProcessPath!));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            installer.UpgradeAsync("Tencent.QQ", "9.9.21", InstallScopeHint.User, null, cancel.Token));

        // Agent 不因为连接断开而崩溃：还能处理下一个请求
        // 服务端发现断开后会取消那个 30 秒的安装，所以 5 秒内就能处理下一个请求
        var pong = await new AgentPipeClient(pipe, Environment.ProcessPath!)
            .SendAsync(new AgentRequest(AgentProtocol.Version, AgentOp.Ping), null, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(AgentMessageType.Pong, pong.Type);
        stop.Cancel();
        await running;
    }
}
