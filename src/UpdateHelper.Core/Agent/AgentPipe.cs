using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UpdateHelper.Core.Agent;

/// <summary>连不上后台助手、对方不是后台助手、或连接中途断开。Message 是给用户看的中文。</summary>
/// <summary>连不上后台助手的原因：没在运行（可启动重试）/ 被拒绝（冒充或没权限）/ 请求发出后断开（不能重发）。</summary>
public enum AgentFailureKind { NotRunning, Rejected, Broken }

public sealed class AgentUnavailableException(string message, AgentFailureKind kind = AgentFailureKind.Broken, Exception? inner = null)
    : Exception(message, inner)
{
    public AgentFailureKind Kind { get; } = kind;
}

/// <summary>
/// 后台助手的管道服务端。只允许当前用户连接、拒绝网络访问；一次处理一个连接、每个连接一条请求；
/// idleTimeout 内没有新连接就返回（Agent 进程随之退出，不常驻）。
/// </summary>
public sealed class AgentPipeServer(string pipeName, AgentRequestHandler handler, TimeSpan? requestTimeout = null)
{
    private readonly TimeSpan _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(30);

    /// <summary>仅供测试：管道实例建好后、第一次等待连接前调用一次（用来稳定复现“连上就断”的时间窗口）。</summary>
    internal Func<Task>? BeforeFirstWait { get; init; }

    public async Task RunAsync(TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        // 管道实例只建一次（FirstPipeInstance）：名字已被别人占了就直接退出，不去加入别人建的管道
        NamedPipeServerStream pipe;
        try
        {
            pipe = CreatePipe();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        using (pipe)
        {
            if (BeforeFirstWait is not null) await BeforeFirstWait();
            var brokenConnects = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(idleTimeout);
                try
                {
                    await pipe.WaitForConnectionAsync(idle.Token);
                }
                catch (OperationCanceledException)
                {
                    return;   // 空闲超时或被要求停止
                }
                catch (IOException)
                {
                    // 对方在我们等待之前就连上又断了（ERROR_NO_DATA）。这时 .NET 的状态仍是“等待连接”，
                    // Disconnect() 会直接抛异常、不会复位实例，所以直接调 Win32 DisconnectNamedPipe 复位（名字一直握在手里）
                    DisconnectNamedPipe(pipe.SafePipeHandle);
                    if (++brokenConnects >= MaxBrokenConnects) return;   // 防止空转：连续失败太多次就退出，下次由计划任务重新拉起
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
    }

    private const int MaxBrokenConnects = 10;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DisconnectNamedPipe(SafePipeHandle hNamedPipe);

    private static void TryDisconnect(NamedPipeServerStream pipe)
    {
        try { pipe.Disconnect(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException) { }
    }

    private async Task ServeOneAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        async Task Send(AgentResponse r)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(WriteTimeout);
            await writer.WriteLineAsync(AgentProtocol.Encode(r).AsMemory(), cts.Token);
        }

        string? line;
        using (var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            readCts.CancelAfter(_requestTimeout);
            line = await LineReader.ReadLineAsync(pipe, AgentProtocol.MaxLineChars, readCts.Token);
        }
        if (line is null) return;   // 对方没发就断开了，或超过长度上限：直接断开连接，不回复（回复会卡在还在写的对方身上）
        var request = AgentProtocol.Decode<AgentRequest>(line);
        if (request is null)
        {
            await Send(new AgentResponse(AgentMessageType.Result, ErrorMessage: "请求格式不对"));
            return;
        }

        // App 断开连接（用户取消、程序被关）时取消安装器：另起一个读取，读到连接关闭就取消
        var disconnected = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watch = WatchDisconnectAsync(pipe, disconnected);
        try
        {
            await handler.HandleAsync(request, Send, disconnected.Token);
        }
        finally
        {
            // 先停掉读取并等它结束，再释放 CTS，管道才能干净地复用给下一个连接
            try { disconnected.Cancel(); } catch (ObjectDisposedException) { }
            await watch;
            disconnected.Dispose();
        }
    }

    /// <summary>App 发完请求后只读不写，所以这边读到 0 字节（或出错）就说明它断开了。</summary>
    private static async Task WatchDisconnectAsync(Stream pipe, CancellationTokenSource disconnected)
    {
        try
        {
            var buffer = new byte[1];
            while (await pipe.ReadAsync(buffer, disconnected.Token) > 0) { }
        }
        catch (Exception) { /* 管道已关闭或已取消 */ }
        finally
        {
            try { disconnected.Cancel(); } catch (ObjectDisposedException) { /* 请求已经正常处理完 */ }
        }
    }

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

/// <summary>App 侧的管道客户端：连接 → 核对对方程序路径 → 发一条请求 → 收进度直到最终结果。</summary>
public sealed class AgentPipeClient(string pipeName, string expectedServerExe)
{
    public async Task<AgentResponse> SendAsync(AgentRequest request, IProgress<double>? progress,
        TimeSpan connectTimeout, CancellationToken cancellationToken)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            var ms = (int)Math.Clamp(connectTimeout.TotalMilliseconds, 0, int.MaxValue);
            await pipe.ConnectAsync(ms, cancellationToken);
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
        var serverExe = ServerExecutable(pipe);
        if (!string.Equals(serverExe, Path.GetFullPath(expectedServerExe), StringComparison.OrdinalIgnoreCase))
            throw new AgentUnavailableException($"管道另一头不是后台助手（{serverExe ?? "未知程序"}），已拒绝连接", AgentFailureKind.Rejected);

        try
        {
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync(AgentProtocol.Encode(request).AsMemory(), cancellationToken);

            while (true)
            {
                var line = await LineReader.ReadLineAsync(pipe, AgentProtocol.MaxLineChars, cancellationToken)
                           ?? throw new AgentUnavailableException("后台助手中途断开了连接", AgentFailureKind.Broken);
                var message = AgentProtocol.Decode<AgentResponse>(line)
                              ?? throw new AgentUnavailableException("后台助手回复的格式不对", AgentFailureKind.Broken);
                if (message.Type == AgentMessageType.Progress) progress?.Report(message.Progress);
                else return message;
            }
        }
        catch (IOException ex)
        {
            throw new AgentUnavailableException($"和后台助手的连接断开了：{ex.Message}", AgentFailureKind.Broken, ex);
        }
    }

    private static string? ServerExecutable(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid)) return null;
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (process.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var size = (uint)buffer.Capacity;
        return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString() : null;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref uint size);
}

/// <summary>按字节读一行 UTF-8（以 \n 结尾），超过上限返回 null。不用 StreamReader：它会多读、而且行长没有上限。</summary>
internal static class LineReader
{
    public static async Task<string?> ReadLineAsync(Stream stream, int maxChars, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one, cancellationToken);
            if (n == 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
            if (one[0] == (byte)'\n') return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add(one[0]);
            if (bytes.Count > maxChars * 3) return null;   // UTF-8 一个字符最多 3 字节（BMP 内）
        }
    }
}
