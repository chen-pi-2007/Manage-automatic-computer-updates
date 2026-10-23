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

    // 这些用例靠 stop.Cancel() 显式收尾，所以给个远大于用例时长的空闲超时，免得它自己先退出
    private static (Task Running, CancellationTokenSource Stop) StartServer(string pipe, IMonitorHandler handler)
    {
        var stop = new CancellationTokenSource();
        return (new MonitorPipeServer(pipe, handler).RunAsync(TimeSpan.FromMinutes(5), stop.Token), stop);
    }

    private static AgentRequest StartRequest(string id, int version = AgentProtocol.Version) =>
        new(version, AgentOp.StartMonitor, InstallerPath: @"C:\setup.exe", RootPid: 1234, MonitorSessionId: id);

    /// <summary>
    /// 回归（空闲到期与客户端连入同时）：旧实现在空闲到期分支里直接 continue 再 WaitForConnectionAsync，
    /// 若此刻管道已是已连接态会抛未捕获的 InvalidOperationException 崩掉 RunAsync。
    /// 时序无法稳定复现，所以扫一串不同的空闲时长 / 连入延迟去撞这个窗口，只断言"服务从不崩"；
    /// 连入的客户端要么被服务（收到错误回复）要么服务干净退出，两种都可以，唯独不能抛异常。
    /// </summary>
    [Fact]
    public async Task Idle_expiry_racing_a_connecting_client_never_crashes_the_server()
    {
        for (var i = 0; i < 40; i++)
        {
            var pipe = UniquePipe();
            var server = new MonitorPipeServer(pipe, new FakeHandler(0)).RunAsync(TimeSpan.FromMilliseconds(15), CancellationToken.None);
            await Task.Delay(8 + i % 16);   // 在空闲到期（15ms）前后扫过
            try
            {
                using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await client.ConnectAsync(500);
                var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                await writer.WriteLineAsync("{\"bad\":");   // 坏请求：服务端应回错误后复位
            }
            catch (Exception ex) when (ex is TimeoutException or IOException) { /* 服务已空闲退出：合法 */ }
            await server.WaitAsync(Timeout);   // 有异常会在这里抛出
        }
    }

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

    [Fact]
    public async Task Exits_after_idle_when_no_session_or_connection()
    {
        // 既没有连接、也没有会话：达到空闲阈值后进程自行退出（不常驻），无需 stop.Cancel
        var pipe = UniquePipe();
        var running = new MonitorPipeServer(pipe, new FakeHandler(0))
            .RunAsync(TimeSpan.FromMilliseconds(150), CancellationToken.None);

        await running.WaitAsync(Timeout);
        Assert.True(running.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Active_session_keeps_server_alive_past_idle()
    {
        // 会话脱离连接继续记录时，哪怕没有新连接、远超空闲阈值，服务端也绝不能退出（否则会砍断会话、丢足迹）
        var pipe = UniquePipe();
        var handler = new FakeHandler(1, hangAfterDiscoveries: true);
        using var stop = new CancellationTokenSource();
        var running = new MonitorPipeServer(pipe, handler).RunAsync(TimeSpan.FromMilliseconds(100), stop.Token);
        using var cancel = new CancellationTokenSource();
        var firstItem = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var run = new MonitorPipeClient(pipe, ThisExe).RunAsync(StartRequest("live"), _ => firstItem.TrySetResult(), cancel.Token);
        await firstItem.Task.WaitAsync(Timeout);
        await cancel.CancelAsync();   // 客户端断开——会话脱离连接继续跑
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

        // 空闲阈值 100ms，等它好几倍：有会话在跑，服务端不得退出
        await Task.Delay(500);
        Assert.False(running.IsCompleted);

        // 会话结束（stop 取消会话）后、无连接，才允许退出
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
