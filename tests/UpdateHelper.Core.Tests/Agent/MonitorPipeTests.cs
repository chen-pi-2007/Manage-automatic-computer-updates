using System.IO.Pipes;
using System.Text;
using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Core.Tests.Agent;

public sealed class MonitorPipeTests
{
    private static string UniquePipe() => $"UpdateHelper.Test.Monitor.{Guid.NewGuid():N}";
    private static string ThisExe => Environment.ProcessPath!;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private static MonitorFootprint MakeFootprint(string id, int entries = 1) => new(
        id, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(1), 1234, @"C:\setup.exe", null,
        new FootprintCompleteness(true, []),
        Enumerable.Range(0, entries).Select(i => new FootprintEntry(
            FootprintEntryType.File, $@"C:\Program Files\Demo\file{i}.dll", null, FootprintConfidence.Certain,
            1234, @"C:\setup.exe", DateTimeOffset.UnixEpoch, true)).ToList());

    /// <summary>假处理器：Start 发 discoveries 条发现再发最终足迹；可选在发完发现后挂起，等待被断开/停止。</summary>
    private sealed class FakeHandler(int discoveries, bool hangAfterDiscoveries = false, int footprintEntries = 1) : IMonitorHandler
    {
        public readonly TaskCompletionSource Disconnected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Sent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<string> Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task StartAsync(string installerPath, int rootPid, Func<AgentResponse, Task> send,
            CancellationToken cancellationToken, string? sessionId = null)
        {
            for (var i = 0; i < discoveries; i++)
                await send(new AgentResponse(AgentMessageType.Discovery, Discovery: new DiscoveryItem(
                    FootprintEntryType.File, $@"C:\Demo\{i}.dll", FootprintConfidence.Certain)));
            Sent.TrySetResult();
            if (hangAfterDiscoveries)
            {
                try { await Task.Delay(Timeout, cancellationToken); } catch (OperationCanceledException) { }
                return;
            }
            await send(new AgentResponse(AgentMessageType.Footprint,
                FootprintJson: AgentProtocol.Encode(MakeFootprint(sessionId ?? "s", footprintEntries))));
        }

        public Task<MonitorFootprint?> StopAsync(string sessionId)
        {
            Stopped.TrySetResult(sessionId);
            return Task.FromResult<MonitorFootprint?>(MakeFootprint(sessionId));
        }

        public void OnClientDisconnected() => Disconnected.TrySetResult();
    }

    private static (Task Running, CancellationTokenSource Stop) StartServer(string pipe, IMonitorHandler handler)
    {
        var stop = new CancellationTokenSource();
        return (new MonitorPipeServer(pipe, handler).RunAsync(stop.Token), stop);
    }

    private static AgentRequest StartRequest(string id, int version = AgentProtocol.Version) =>
        new(version, AgentOp.StartMonitor, InstallerPath: @"C:\setup.exe", RootPid: 1234, MonitorSessionId: id);

    [Fact]
    public async Task Streams_discoveries_then_footprint()
    {
        var pipe = UniquePipe();
        var (running, stop) = StartServer(pipe, new FakeHandler(3));
        var got = new List<DiscoveryItem>();

        var footprint = await new MonitorPipeClient(pipe, ThisExe)
            .RunAsync(StartRequest("sess1"), got.Add, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(["0.dll", "1.dll", "2.dll"], got.Select(d => Path.GetFileName(d.Path)));
        Assert.All(got, d => Assert.Equal(FootprintConfidence.Certain, d.Confidence));
        Assert.Equal("sess1", footprint.Id);
        Assert.Single(footprint.Entries);
        stop.Cancel();
        await running.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Large_footprint_exceeding_normal_line_cap_still_arrives()
    {
        // 足迹整条一行 JSON，条目多了会超过普通消息的 64K 上限，监控通道必须放宽
        var pipe = UniquePipe();
        var (running, stop) = StartServer(pipe, new FakeHandler(0, footprintEntries: 3000));

        var footprint = await new MonitorPipeClient(pipe, ThisExe)
            .RunAsync(StartRequest("big"), _ => { }, CancellationToken.None).WaitAsync(Timeout);

        Assert.Equal(3000, footprint.Entries.Count);
        stop.Cancel();
        await running.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Wrong_version_rejected()
    {
        var pipe = UniquePipe();
        var (running, stop) = StartServer(pipe, new FakeHandler(1));

        var ex = await Assert.ThrowsAsync<AgentUnavailableException>(() => new MonitorPipeClient(pipe, ThisExe)
            .RunAsync(StartRequest("old", version: 2), _ => { }, CancellationToken.None).WaitAsync(Timeout));

        Assert.Equal(AgentFailureKind.Rejected, ex.Kind);
        stop.Cancel();
        await running.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Client_disconnect_notifies_handler_and_session_can_be_read_back()
    {
        var pipe = UniquePipe();
        var handler = new FakeHandler(1, hangAfterDiscoveries: true);
        var (running, stop) = StartServer(pipe, handler);
        using var cancel = new CancellationTokenSource();
        var firstItem = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = new MonitorPipeClient(pipe, ThisExe).RunAsync(StartRequest("keep"), _ => firstItem.TrySetResult(), cancel.Token);
        await firstItem.Task.WaitAsync(Timeout);
        await cancel.CancelAsync();   // 客户端中途断开
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        await handler.Disconnected.Task.WaitAsync(Timeout);   // 服务端得知断开、没崩

        // 重连：用同一个会话 id 读回足迹（服务端仍活着并接受新连接）
        var footprint = await new MonitorPipeClient(pipe, ThisExe)
            .RunAsync(new AgentRequest(AgentProtocol.Version, AgentOp.StopMonitor, MonitorSessionId: "keep"), _ => { }, CancellationToken.None)
            .WaitAsync(Timeout);
        Assert.Equal("keep", footprint.Id);
        Assert.Equal("keep", await handler.Stopped.Task.WaitAsync(Timeout));

        stop.Cancel();
        await running.WaitAsync(Timeout);
    }

    [Fact]
    public async Task Stop_on_same_connection_returns_footprint()
    {
        var pipe = UniquePipe();
        var handler = new FakeHandler(1, hangAfterDiscoveries: true);
        var (running, stop) = StartServer(pipe, handler);

        // 用裸管道在同一条连接上先发 StartMonitor、收到一条发现后再发 StopMonitor
        using var raw = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await raw.ConnectAsync((int)Timeout.TotalMilliseconds);
        var writer = new StreamWriter(raw, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        await writer.WriteLineAsync(AgentProtocol.Encode(StartRequest("s2")));
        var line = await ReadLine(raw);
        Assert.Equal(AgentMessageType.Discovery, AgentProtocol.Decode<AgentResponse>(line)!.Type);

        await writer.WriteLineAsync(AgentProtocol.Encode(new AgentRequest(AgentProtocol.Version, AgentOp.StopMonitor, MonitorSessionId: "s2")));
        var last = AgentProtocol.Decode<AgentResponse>(await ReadLine(raw))!;

        Assert.Equal(AgentMessageType.Footprint, last.Type);
        Assert.Equal("s2", AgentProtocol.Decode<MonitorFootprint>(last.FootprintJson!, 1 << 20)!.Id);
        Assert.Equal("s2", await handler.Stopped.Task.WaitAsync(Timeout));
        stop.Cancel();
        await running.WaitAsync(Timeout);
    }

    private static async Task<string> ReadLine(Stream s)
    {
        using var cts = new CancellationTokenSource(Timeout);
        return await LineReader.ReadLineAsync(s, 1 << 20, cts.Token) ?? throw new InvalidOperationException("连接被关闭");
    }

    [Fact]
    public void Malformed_line_decodes_null()
    {
        Assert.Null(AgentProtocol.Decode<AgentResponse>("这不是json"));
        Assert.Null(AgentProtocol.Decode<AgentResponse>("{\"Type\":\"Bogus\"}"));
        Assert.Null(AgentProtocol.Decode<AgentRequest>(new string('x', AgentProtocol.MaxLineChars + 1)));
        Assert.Null(AgentProtocol.Decode<AgentResponse>(""));
    }
}
