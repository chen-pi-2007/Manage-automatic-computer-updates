using System.IO.Pipes;
using System.Text;
using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public sealed class AgentPipeTests
{
    private sealed class FakeInstaller : IPackageInstaller
    {
        public string Name => "fake";
        public async Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
            IProgress<double>? progress, CancellationToken cancellationToken)
        {
            progress?.Report(0.3);
            await Task.Delay(10, cancellationToken);
            return new InstallerReport(true, false, null, null);
        }
    }

    private static string UniquePipe() => $"UpdateHelper.Test.{Guid.NewGuid():N}";
    private static string ThisExe => Environment.ProcessPath!;
    private static readonly TimeSpan Connect = TimeSpan.FromSeconds(5);

    private static (AgentPipeServer Server, Task Running, CancellationTokenSource Stop) StartServer(string pipe)
    {
        var stop = new CancellationTokenSource();
        var server = new AgentPipeServer(pipe, new AgentRequestHandler(new FakeInstaller(), elevated: true, "test"));
        return (server, server.RunAsync(TimeSpan.FromSeconds(30), stop.Token), stop);
    }

    [Fact]
    public async Task Ping_round_trip()
    {
        var pipe = UniquePipe();
        var (_, running, stop) = StartServer(pipe);

        var pong = await new AgentPipeClient(pipe, ThisExe)
            .SendAsync(new AgentRequest(AgentProtocol.Version, AgentOp.Ping), null, Connect, CancellationToken.None);

        Assert.Equal(AgentMessageType.Pong, pong.Type);
        Assert.Equal("test", pong.AgentVersion);
        stop.Cancel();
        await running;
    }

    [Fact]
    public async Task Upgrade_streams_progress_and_serves_several_connections()
    {
        var pipe = UniquePipe();
        var (_, running, stop) = StartServer(pipe);
        var client = new AgentPipeClient(pipe, ThisExe);
        var seen = new List<double>();
        var progress = new SyncProgress(seen.Add);

        for (var i = 0; i < 3; i++)
        {
            var result = await client.SendAsync(
                new AgentRequest(AgentProtocol.Version, AgentOp.Upgrade, "Tencent.QQ", "9.9.21"), progress, Connect, CancellationToken.None);
            Assert.True(result.Success);
        }

        Assert.Contains(0.3, seen);
        stop.Cancel();
        await running;
    }

    [Fact]
    public async Task Client_refuses_server_with_wrong_executable()
    {
        var pipe = UniquePipe();
        var (_, running, stop) = StartServer(pipe);

        var ex = await Assert.ThrowsAsync<AgentUnavailableException>(() => new AgentPipeClient(pipe, @"C:\Program Files\UpdateHelper\UpdateHelper.Agent.exe")
            .SendAsync(new AgentRequest(AgentProtocol.Version, AgentOp.Ping), null, Connect, CancellationToken.None));

        Assert.Contains("不是后台助手", ex.Message);
        Assert.Equal(AgentFailureKind.Rejected, ex.Kind);
        stop.Cancel();
        await running;
    }

    [Fact]
    public async Task Client_fails_fast_when_nobody_listens()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<AgentUnavailableException>(() => new AgentPipeClient(UniquePipe(), ThisExe)
            .SendAsync(new AgentRequest(AgentProtocol.Version, AgentOp.Ping), null, TimeSpan.FromMilliseconds(300), CancellationToken.None));
        Assert.Equal(AgentFailureKind.NotRunning, ex.Kind);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Server_rejects_garbage_and_keeps_serving()
    {
        var pipe = UniquePipe();
        var (_, running, stop) = StartServer(pipe);

        // 1. 垃圾数据
        using (var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(5000);
            var bytes = Encoding.UTF8.GetBytes("this is not json\n");
            await raw.WriteAsync(bytes);
            await raw.FlushAsync();
            using var reader = new StreamReader(raw, Encoding.UTF8);
            var reply = AgentProtocol.Decode<AgentResponse>((await reader.ReadLineAsync())!);
            Assert.NotNull(reply);
            Assert.False(reply.Success);
            Assert.Contains("请求格式不对", reply.ErrorMessage);
        }

        // 2. 超长一行（没有换行）
        using (var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(5000);
            var huge = Encoding.UTF8.GetBytes(new string('x', AgentProtocol.MaxLineChars + 10));
            try { await raw.WriteAsync(huge); } catch (IOException) { /* 服务端可能已经断开 */ }
        }

        // 3. 之后正常请求照样能处理
        var pong = await new AgentPipeClient(pipe, ThisExe)
            .SendAsync(new AgentRequest(AgentProtocol.Version, AgentOp.Ping), null, Connect, CancellationToken.None);
        Assert.Equal(AgentMessageType.Pong, pong.Type);

        stop.Cancel();
        await running;
    }

    [Fact]
    public async Task Server_exits_after_idle_timeout()
    {
        var server = new AgentPipeServer(UniquePipe(), new AgentRequestHandler(new FakeInstaller(), true, "test"));
        var finished = server.RunAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
        Assert.Same(finished, await Task.WhenAny(finished, Task.Delay(5000)));
    }

    [Fact]
    public void Paths_are_per_user()
    {
        Assert.Equal(@"\UpdateHelper\Agent-S-1-5-21-1-2-3-1001", AgentPaths.TaskName("S-1-5-21-1-2-3-1001"));
        Assert.Equal("UpdateHelper.Agent.S-1-5-21-1-2-3-1001", AgentPaths.PipeName("S-1-5-21-1-2-3-1001"));
        Assert.EndsWith(@"\UpdateHelper\UpdateHelper.Agent.exe", AgentPaths.AgentExe);
        Assert.StartsWith("S-1-5-", AgentPaths.CurrentUserSid());
    }

    private static async Task AssertPingWorks(string pipe)
    {
        var pong = await new AgentPipeClient(pipe, ThisExe)
            .SendAsync(new AgentRequest(AgentProtocol.Version, AgentOp.Ping), null, Connect, CancellationToken.None);
        Assert.Equal(AgentMessageType.Pong, pong.Type);
    }

    [Fact]
    public async Task Server_survives_client_that_connects_and_leaves()
    {
        var pipe = UniquePipe();
        var stop = new CancellationTokenSource();
        var server = new AgentPipeServer(pipe, new AgentRequestHandler(new FakeInstaller(), true, "test"))
        {
            // 在第一次等待连接之前连上再断开：ConnectNamedPipe 会返回 ERROR_NO_DATA
            BeforeFirstWait = async () =>
            {
                var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await raw.ConnectAsync(5000);
                raw.Dispose();
            },
        };
        var running = server.RunAsync(TimeSpan.FromSeconds(30), stop.Token);
        await AssertPingWorks(pipe);
        Assert.False(running.IsCompleted);
        // 之后再连上就断几次，也不影响
        for (var i = 0; i < 5; i++)
        {
            var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await raw.ConnectAsync(5000);
            raw.Dispose();
        }
        await AssertPingWorks(pipe);
        Assert.False(running.IsCompleted);
        stop.Cancel();
        await running;
    }

    [Fact]
    public async Task Silent_client_times_out_and_server_keeps_serving()
    {
        var pipe = UniquePipe();
        var stop = new CancellationTokenSource();
        var server = new AgentPipeServer(pipe, new AgentRequestHandler(new FakeInstaller(), true, "test"), TimeSpan.FromMilliseconds(300));
        var running = server.RunAsync(TimeSpan.FromSeconds(30), stop.Token);
        using var silent = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await silent.ConnectAsync(5000);
        await AssertPingWorks(pipe);
        stop.Cancel();
        await running;
    }

    [Fact]
    public async Task Oversized_line_hits_byte_cap()
    {
        var pipe = UniquePipe();
        var (_, running, stop) = StartServer(pipe);
        using (var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous))
        {
            await raw.ConnectAsync(5000);
            var huge = new byte[AgentProtocol.MaxLineChars * 3 + 100];
            Array.Fill(huge, (byte)'x');
            try { await raw.WriteAsync(huge); } catch (IOException) { }
        }
        await AssertPingWorks(pipe);
        stop.Cancel();
        await running;
    }

    [Fact]
    public async Task Second_server_with_same_name_exits_immediately()
    {
        var pipe = UniquePipe();
        var (_, running, stop) = StartServer(pipe);
        var b = new AgentPipeServer(pipe, new AgentRequestHandler(new FakeInstaller(), true, "test"));
        var runningB = b.RunAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.Same(runningB, await Task.WhenAny(runningB, Task.Delay(2000)));
        await runningB;
        await AssertPingWorks(pipe);
        stop.Cancel();
        await running;
    }

    [Fact]
    public async Task Client_maps_access_denied_to_unavailable()
    {
        var pipe = UniquePipe();
        var security = new System.IO.Pipes.PipeSecurity();
        security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
            System.Security.Principal.WindowsIdentity.GetCurrent().User!,
            System.IO.Pipes.PipeAccessRights.FullControl, System.Security.AccessControl.AccessControlType.Deny));
        using var denied = System.IO.Pipes.NamedPipeServerStreamAcl.Create(pipe, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
        await Assert.ThrowsAsync<AgentUnavailableException>(() => new AgentPipeClient(pipe, ThisExe)
            .SendAsync(new AgentRequest(AgentProtocol.Version, AgentOp.Ping), null, Connect, CancellationToken.None));
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
