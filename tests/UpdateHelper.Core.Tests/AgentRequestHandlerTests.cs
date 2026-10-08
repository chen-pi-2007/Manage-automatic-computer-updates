using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public sealed class AgentRequestHandlerTests
{
    private sealed class FakeInstaller : IPackageInstaller
    {
        public string Name => "fake";
        public List<(string Id, string Version, InstallScopeHint Scope)> Calls { get; } = [];
        public Func<InstallerReport> Result { get; set; } = () => new InstallerReport(true, false, null, null);

        public Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
            IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Calls.Add((packageId, targetVersion, scope));
            progress?.Report(0.5);
            return Task.FromResult(Result());
        }
    }

    private static async Task<List<AgentResponse>> Run(AgentRequestHandler handler, AgentRequest request)
    {
        var sent = new List<AgentResponse>();
        await handler.HandleAsync(request, r => { sent.Add(r); return Task.CompletedTask; }, CancellationToken.None);
        return sent;
    }

    private static AgentRequest Upgrade(string id = "Tencent.QQ", string version = "9.9.21") =>
        new(AgentProtocol.Version, AgentOp.Upgrade, id, version, InstallScopeHint.User);

    [Fact]
    public async Task Handler_answers_ping_with_version_and_elevation()
    {
        var sent = await Run(new AgentRequestHandler(new FakeInstaller(), elevated: true, "0.2.0"),
            new AgentRequest(AgentProtocol.Version, AgentOp.Ping));

        var pong = Assert.Single(sent);
        Assert.Equal(AgentMessageType.Pong, pong.Type);
        Assert.Equal("0.2.0", pong.AgentVersion);
        Assert.True(pong.Elevated);
    }

    [Fact]
    public async Task Handler_upgrades_and_streams_progress_then_one_result()
    {
        var installer = new FakeInstaller();
        var sent = await Run(new AgentRequestHandler(installer, elevated: true, "0.2.0"), Upgrade());

        Assert.Equal([("Tencent.QQ", "9.9.21", InstallScopeHint.User)], installer.Calls);
        Assert.Equal(AgentMessageType.Progress, sent[0].Type);
        Assert.Equal(0.5, sent[0].Progress);
        var result = sent[^1];
        Assert.Equal(AgentMessageType.Result, result.Type);
        Assert.True(result.Success);
        Assert.Single(sent, r => r.Type == AgentMessageType.Result);
    }

    [Fact]
    public async Task Handler_refuses_invalid_requests_without_calling_installer()
    {
        var installer = new FakeInstaller();
        var sent = await Run(new AgentRequestHandler(installer, elevated: true, "0.2.0"), Upgrade(id: "rule:wps"));

        Assert.Empty(installer.Calls);
        var result = Assert.Single(sent);
        Assert.False(result.Success);
        Assert.Contains("包 id 格式不对", result.ErrorMessage);
    }

    [Fact]
    public async Task Handler_refuses_when_not_elevated()
    {
        var installer = new FakeInstaller();
        var sent = await Run(new AgentRequestHandler(installer, elevated: false, "0.2.0"), Upgrade());

        Assert.Empty(installer.Calls);
        Assert.Contains("没有管理员权限", Assert.Single(sent).ErrorMessage);
    }

    [Fact]
    public async Task Handler_turns_installer_exceptions_into_failed_result()
    {
        var installer = new FakeInstaller { Result = () => throw new InvalidOperationException("COM 炸了") };
        var sent = await Run(new AgentRequestHandler(installer, elevated: true, "0.2.0"), Upgrade());

        var result = sent[^1];
        Assert.False(result.Success);
        Assert.Contains("COM 炸了", result.ErrorMessage);
    }

    [Fact]
    public async Task Handler_passes_installer_failure_through()
    {
        var installer = new FakeInstaller { Result = () => new InstallerReport(false, false, "被系统策略禁止", 5) };
        var result = (await Run(new AgentRequestHandler(installer, elevated: true, "0.2.0"), Upgrade()))[^1];

        Assert.Equal(new InstallerReport(false, false, "被系统策略禁止", 5), AgentProtocol.ToReport(result));
    }
}
