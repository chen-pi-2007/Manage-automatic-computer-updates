using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Core.Agent;

/// <summary>流式发现的一项：给 App 实时展示用的轻量投影（完整信息在最终足迹里）。</summary>
public sealed record DiscoveryItem(FootprintEntryType Type, string Path, FootprintConfidence Confidence);

/// <summary>
/// 监控处理器（助手侧真正干活的一方）。约定（两阶段握手）：
/// StartAsync 在整个监控期间运行——先起好 ETW 并缓冲事件、用 send 回一条 Ready，期间用 send 发 Discovery；
/// 随后服务端读到 SetMonitorRoot 时调用 SetRoot 把根 PID 补进来（处理器据此建进程树、回放缓冲事件）。
/// 会话因任何原因结束（停止/空闲超时/最长时长/根 PID 超时）时，由它用 send 发最后的 Footprint 再返回。
/// send 是线程安全的，连接断开后变成空操作（不会抛，也不会写已释放的管道）。
/// cancellationToken 只在助手进程要退出时取消——客户端断线不会取消它（断线后助手继续记录）。
/// </summary>
public interface IMonitorHandler
{
    /// <summary>起 ETW 并缓冲、回 Ready，然后一直运行到会话结束。根 PID 之后经 <see cref="SetRoot"/> 补入。</summary>
    /// <param name="sessionId">客户端在请求里给的会话 id（没给则由服务端生成）；之后用它 SetRoot/StopAsync/重连。</param>
    Task StartAsync(string installerPath, Func<AgentResponse, Task> send,
        CancellationToken cancellationToken, string? sessionId = null);

    /// <summary>补入安装器根 PID（Ready 之后）：据此建进程树并回放已缓冲的事件。会话不存在/已设过/已结束则忽略。线程安全。</summary>
    void SetRoot(string sessionId, int rootPid);

    /// <summary>结束会话并返回最终足迹；找不到该会话返回 null。重连读回也走这里。</summary>
    Task<MonitorFootprint?> StopAsync(string sessionId);

    /// <summary>App 断开了连接。仅通知：会话继续记录，之后可凭会话 id 重连读回。</summary>
    void OnClientDisconnected();
}

/// <summary>
/// 流式监控管道的服务端（独立管道名，和升级/清残留那条互不占用）。
/// 与单请求管道的区别：连接存活期内可多次写（Discovery），同时有后台读循环既侦测断开、又接收 StopMonitor。
/// 一次只服务一个连接；监控会话本身脱离连接运行——客户端断开后会话继续，重连用 StopMonitor+会话 id 读回足迹。
/// </summary>
public sealed class MonitorPipeServer(string pipeName, IMonitorHandler handler)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);
    private const int MaxBrokenConnects = 10;

    // 脱离连接运行的会话任务：RunAsync 退出前要等它们收尾（它们拿的是 RunAsync 的 ct，会随之取消）
    private readonly List<Task> _sessions = [];

    /// <param name="idleTimeout">
    /// 不常驻：既没有活动监控会话、也没有在途连接，持续这么久就退出（提权进程随之结束，不吃性能，镜像 --serve 的做法）。
    /// 有会话在跑（含客户端已断开、脱离连接继续记录的会话）或正在服务连接时，计时清零——绝不因“没有新连接”砍断进行中的会话。
    /// </param>
    public async Task RunAsync(TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        // 与现有管道一致：只建一次实例（FirstPipeInstance），名字被别人占了就直接退出，不加入别人的管道
        NamedPipeServerStream pipe;
        try
        {
            pipe = CreatePipe();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        try
        {
            var brokenConnects = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                // 空闲计时只在“两次服务连接之间”推进；服务连接期间不在此等待，所以不受影响
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(idleTimeout);
                try
                {
                    await pipe.WaitForConnectionAsync(idle.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException)
                {
                    if (cancellationToken.IsCancellationRequested) return;   // 助手被要求退出
                    // 竞态：空闲到期的同一时刻客户端恰好连入，管道已是已连接态。
                    // 此时再 WaitForConnectionAsync 会抛 InvalidOperationException 崩掉整个提权服务，
                    // 而且这个客户端也不该被丢弃：直接去服务它（走下面的正常路径）。有连接就绝不退出（Ruling-6）。
                    if (!pipe.IsConnected)
                    {
                        // 没有连接：被取消但未连接的实例可以直接再等（既有路径）；不在这里 DisconnectNamedPipe，
                        // 因为复位恰好撞上此刻连入的客户端会把它砍断。
                        if (ex is InvalidOperationException)
                        {
                            // 状态异常且并无连接：复位后重等（计数防止无限空转）
                            DisconnectNamedPipe(pipe.SafePipeHandle);
                            if (++brokenConnects >= MaxBrokenConnects) return;
                            continue;
                        }
                        if (HasActiveSession()) continue;   // 空闲到期但仍有会话在记录：清零重等，绝不砍断它
                        return;   // 既无会话也无连接：不常驻，退出
                    }
                }
                catch (IOException)
                {
                    // 对方在我们等待前就连上又断了：用 Win32 复位实例（原因同 AgentPipeServer）
                    DisconnectNamedPipe(pipe.SafePipeHandle);
                    if (++brokenConnects >= MaxBrokenConnects) return;
                    continue;
                }
                brokenConnects = 0;

                try
                {
                    await ServeOneAsync(pipe, cancellationToken);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
                {
                    // 对方断开或超时：处理下一个连接
                }
                finally
                {
                    TryDisconnect(pipe);
                }
            }
        }
        finally
        {
            pipe.Dispose();
            // 管道关了再等会话收尾：会话的 send 此时都是空操作
            Task[] pending;
            lock (_sessions) pending = [.. _sessions];
            try { await Task.WhenAll(pending); } catch (Exception) { /* 会话里的异常已在 RunSession 内处理；这里只是等待 */ }
        }
    }

    /// <summary>是否还有未完成的监控会话（含客户端已断开、脱离连接继续记录的）。顺带清掉已完成项。</summary>
    private bool HasActiveSession()
    {
        lock (_sessions)
        {
            _sessions.RemoveAll(t => t.IsCompleted);
            return _sessions.Count > 0;
        }
    }

    /// <summary>
    /// 一个连接的写出端。所有写都经过同一把锁串行化；Close 之后 Send 是空操作。
    /// 为什么：Discovery 来自处理器的后台线程，Footprint 可能同时来自会话收尾和 Stop 路径，
    /// 并且连接结束后处理器可能还握着 send——必须保证“不并发写、不在关闭后写”。
    /// </summary>
    private sealed class Connection(Stream pipe, CancellationToken serverToken)
    {
        private readonly StreamWriter _writer = new(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        private readonly SemaphoreSlim _gate = new(1, 1);   // 不 Dispose：晚到的 send 只会拿锁后发现已关闭
        private bool _closed;
        private bool _footprintSent;

        public async Task Send(AgentResponse r)
        {
            await _gate.WaitAsync();
            try
            {
                if (_closed) return;
                if (r.Type == AgentMessageType.Footprint)
                {
                    if (_footprintSent) return;   // 最终足迹只发一次（Stop 路径与会话收尾可能都想发）
                    _footprintSent = true;
                }
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
                cts.CancelAfter(WriteTimeout);
                await _writer.WriteLineAsync(AgentProtocol.Encode(r).AsMemory(), cts.Token);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                _closed = true;   // 写失败（对方走了/超时）：后续都不再写，断开由读循环另行发现
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>断开管道前调用：等正在进行的写结束，之后所有 Send 变空操作。</summary>
        public async Task CloseAsync()
        {
            await _gate.WaitAsync();
            _closed = true;
            _gate.Release();
        }
    }

    private enum ControlResult { Stopped, Disconnected }

    private async Task ServeOneAsync(NamedPipeServerStream pipe, CancellationToken serverToken)
    {
        var conn = new Connection(pipe, serverToken);
        try
        {
            string? line;
            using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
            {
                readCts.CancelAfter(RequestTimeout);
                line = await LineReader.ReadLineAsync(pipe, AgentProtocol.MaxLineChars, readCts.Token);
            }
            if (line is null) return;   // 没发就断开，或超长：直接断开不回复
            var request = AgentProtocol.Decode<AgentRequest>(line);
            if (request is null)
            {
                await conn.Send(new AgentResponse(AgentMessageType.Result, ErrorMessage: "请求格式不对"));
                return;
            }
            if (request.Version != AgentProtocol.Version)
            {
                await conn.Send(new AgentResponse(AgentMessageType.Result,
                    ErrorMessage: $"协议版本不符（对方 {request.Version}，助手 {AgentProtocol.Version}），请更新后台助手"));
                return;
            }

            switch (request.Op)
            {
                case AgentOp.StopMonitor:
                    // 重连读回：不依赖原来的连接，凭会话 id 结束（若还在跑）并取回足迹
                    await SendStopResultAsync(conn, request.MonitorSessionId);
                    return;
                case AgentOp.StartMonitor:
                    await ServeStartAsync(pipe, conn, request, serverToken);
                    return;
                default:
                    await conn.Send(new AgentResponse(AgentMessageType.Result, ErrorMessage: "监控通道不支持这个操作"));
                    return;
            }
        }
        finally
        {
            // 先让 Send 变空操作（并等在途的写结束），再由调用方断开/复用管道
            await conn.CloseAsync();
        }
    }

    private async Task SendStopResultAsync(Connection conn, string? sessionId)
    {
        MonitorFootprint? footprint = string.IsNullOrEmpty(sessionId) ? null : await handler.StopAsync(sessionId);
        if (footprint is null)
            await conn.Send(new AgentResponse(AgentMessageType.Result, ErrorMessage: "找不到这个监控会话"));
        else
            await conn.Send(new AgentResponse(AgentMessageType.Footprint, FootprintJson: AgentProtocol.Encode(footprint)));
    }

    private async Task ServeStartAsync(NamedPipeServerStream pipe, Connection conn, AgentRequest request, CancellationToken serverToken)
    {
        // 两阶段握手：StartMonitor 只带安装包路径，根 PID 之后由 SetMonitorRoot 补（所以这里不再要求 RootPid）
        if (string.IsNullOrWhiteSpace(request.InstallerPath))
        {
            await conn.Send(new AgentResponse(AgentMessageType.Result, ErrorMessage: "监控请求缺少安装包路径"));
            return;
        }
        var sessionId = string.IsNullOrEmpty(request.MonitorSessionId) ? Guid.NewGuid().ToString("N") : request.MonitorSessionId;

        // 会话脱离本连接运行：它的 token 是服务端 token（不是“断线 token”），所以客户端断线不会让它停
        var session = RunSession(request.InstallerPath, conn, sessionId, serverToken);
        lock (_sessions)
        {
            _sessions.RemoveAll(t => t.IsCompleted);
            _sessions.Add(session);
        }

        // 后台读循环：读到 0 字节/出错 = 客户端断了；读到 StopMonitor = 要求结束。与写（Discovery）并行，互不阻塞
        using var controlCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var control = ControlLoopAsync(pipe, conn, sessionId, controlCts.Token);

        try
        {
            var first = await Task.WhenAny(session, control);
            if (first == control && await control == ControlResult.Disconnected && !session.IsCompleted)
                NotifyDisconnected();
            // 会话自己先结束（足迹已发）→ 直接收尾；Stopped → 足迹已在控制循环里发出
        }
        finally
        {
            // 先停读循环并等它结束，再离开：管道才能干净复用给下一个连接（同 AgentPipeServer 的做法）
            try { controlCts.Cancel(); } catch (ObjectDisposedException) { }
            try { await control; } catch (Exception) { /* 取消或管道已关 */ }
        }
    }

    private async Task RunSession(string installerPath, Connection conn, string sessionId, CancellationToken serverToken)
    {
        try
        {
            await handler.StartAsync(installerPath, conn.Send, serverToken, sessionId);
        }
        catch (OperationCanceledException) when (serverToken.IsCancellationRequested)
        {
            // 助手要退出了
        }
        catch (Exception ex)
        {
            // 处理器出错：能告诉客户端就告诉（连接已关则是空操作），绝不让它变成未观察的任务异常
            await conn.Send(new AgentResponse(AgentMessageType.Result, ErrorMessage: $"监控失败：{ex.Message}"));
        }
    }

    private void NotifyDisconnected()
    {
        try { handler.OnClientDisconnected(); }
        catch (Exception) { /* 通知是尽力而为；处理器出错不该拖垮管道服务 */ }
    }

    private async Task<ControlResult> ControlLoopAsync(Stream pipe, Connection conn, string sessionId, CancellationToken token)
    {
        try
        {
            while (true)
            {
                var line = await LineReader.ReadLineAsync(pipe, AgentProtocol.MaxLineChars, token);
                if (line is null) return ControlResult.Disconnected;   // 0 字节（对方关闭）或超长行
                var msg = AgentProtocol.Decode<AgentRequest>(line);
                if (msg is { Op: AgentOp.SetMonitorRoot })
                {
                    // 两阶段握手第二步：把安装器根 PID 补给处理器（同步、线程安全），然后继续读 Stop/断开
                    handler.SetRoot(msg.MonitorSessionId ?? sessionId, msg.RootPid);
                    continue;
                }
                if (msg is { Op: AgentOp.StopMonitor })
                {
                    await SendStopResultAsync(conn, msg.MonitorSessionId ?? sessionId);
                    return ControlResult.Stopped;
                }
                // 其它/坏行：忽略，继续读
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return ControlResult.Disconnected;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DisconnectNamedPipe(SafePipeHandle hNamedPipe);

    private static void TryDisconnect(NamedPipeServerStream pipe)
    {
        try { pipe.Disconnect(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException) { }
    }

    /// <summary>与 AgentPipeServer 相同的安全设置：只允许当前用户、拒绝网络访问。</summary>
    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        using var identity = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }
}

/// <summary>
/// App 侧的监控客户端：连接 → 核对对方程序路径 → 发请求 → 收 Discovery 直到最终 Footprint。
/// 取消（ct）= 放弃这条连接：服务端会话继续记录，之后用 StopMonitor + 会话 id 再调用本方法即可读回足迹。
/// </summary>
public sealed class MonitorPipeClient(string pipeName, string expectedServerExe)
{
    /// <summary>
    /// 足迹整条一行 JSON，条目多了会远超普通消息的 64K 上限，所以监控通道放宽到 32M 字符。
    /// 放宽只在客户端读取方向（对方已被验证是安装目录里的助手）；服务端读请求仍用普通上限。
    /// </summary>
    private const int MaxMonitorLineChars = 32 * 1024 * 1024;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// start 为 StartMonitor 时走两阶段握手：发 StartMonitor（不带根 PID）→ 等 Ready → 回调 onReadyLaunch 启动安装器并拿根 PID
    /// → 发 SetMonitorRoot 补入 → 继续收 Discovery 直到最终 Footprint；为 StopMonitor（重连读回）时直接等足迹。
    /// onReadyLaunch 只在 StartMonitor 且收到 Ready 时调用一次，返回安装器根 PID；它抛异常（启动失败/取消）则原样向上抛，
    /// 这条连接随之放弃（服务端会话仍在记录已缓冲的早到事件，直到根 PID 超时自行收尾并标不完整）。
    /// 失败：连不上/被拒/版本不符/对方不是助手 → AgentUnavailableException；中途断线 → Broken（可凭会话 id 重连）。
    /// </summary>
    public async Task<MonitorFootprint> RunAsync(AgentRequest start, Action<DiscoveryItem> onDiscovery,
        CancellationToken cancellationToken, Func<Task<int>>? onReadyLaunch = null)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync((int)ConnectTimeout.TotalMilliseconds, cancellationToken);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new AgentUnavailableException("没有权限连接后台助手", AgentFailureKind.Rejected, ex);
        }
        catch (TimeoutException ex)
        {
            throw new AgentUnavailableException("后台助手没有响应", AgentFailureKind.NotRunning, ex);
        }
        catch (IOException ex)
        {
            throw new AgentUnavailableException($"连接后台助手失败：{ex.Message}", AgentFailureKind.NotRunning, ex);
        }

        // 防冒充：管道另一头必须是安装目录里的后台助手
        var serverExe = AgentPipeClient.ServerExecutable(pipe);
        if (!string.Equals(serverExe, Path.GetFullPath(expectedServerExe), StringComparison.OrdinalIgnoreCase))
            throw new AgentUnavailableException($"管道另一头不是后台助手（{serverExe ?? "未知程序"}），已拒绝连接", AgentFailureKind.Rejected);

        try
        {
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync(AgentProtocol.Encode(start).AsMemory(), cancellationToken);

            var awaitingRoot = start.Op == AgentOp.StartMonitor && onReadyLaunch is not null;

            while (true)
            {
                var line = await LineReader.ReadLineAsync(pipe, MaxMonitorLineChars, cancellationToken)
                           ?? throw new AgentUnavailableException("后台助手中途断开了连接", AgentFailureKind.Broken);
                var message = AgentProtocol.Decode<AgentResponse>(line, MaxMonitorLineChars)
                              ?? throw new AgentUnavailableException("后台助手回复的格式不对", AgentFailureKind.Broken);
                switch (message.Type)
                {
                    case AgentMessageType.Ready:
                        // ETW 已就绪、事件已在缓冲：现在才启动安装器（这正是修复点），拿到根 PID 后补发 SetMonitorRoot
                        if (awaitingRoot)
                        {
                            awaitingRoot = false;   // 只握手一次，重复的 Ready 忽略
                            var rootPid = await onReadyLaunch!();
                            var setRoot = start with { Op = AgentOp.SetMonitorRoot, RootPid = rootPid };
                            await writer.WriteLineAsync(AgentProtocol.Encode(setRoot).AsMemory(), cancellationToken);
                        }
                        break;
                    case AgentMessageType.Discovery:
                        if (message.Discovery is not null) onDiscovery(message.Discovery);
                        break;
                    case AgentMessageType.Footprint:
                        return message.FootprintJson is { } json && AgentProtocol.Decode<MonitorFootprint>(json, MaxMonitorLineChars) is { } footprint
                            ? footprint
                            : throw new AgentUnavailableException("后台助手回复的足迹格式不对", AgentFailureKind.Broken);
                    case AgentMessageType.Result:
                        // 监控通道上的 Result 只会是错误（版本不符、会话不存在、监控失败）
                        throw new AgentUnavailableException(message.ErrorMessage ?? "后台助手拒绝了监控请求", AgentFailureKind.Rejected);
                    default:
                        break;   // Progress/Pong：监控通道不会有，忽略
                }
            }
        }
        catch (IOException ex)
        {
            throw new AgentUnavailableException($"和后台助手的连接断开了：{ex.Message}", AgentFailureKind.Broken, ex);
        }
    }
}
