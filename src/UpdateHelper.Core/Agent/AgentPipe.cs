using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace UpdateHelper.Core.Agent;

/// <summary>连不上后台助手、对方不是后台助手、或连接中途断开。Message 是给用户看的中文。</summary>
public sealed class AgentUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// 后台助手的管道服务端。只允许当前用户连接、拒绝网络访问；一次处理一个连接、每个连接一条请求；
/// idleTimeout 内没有新连接就返回（Agent 进程随之退出，不常驻）。
/// </summary>
public sealed class AgentPipeServer(string pipeName, AgentRequestHandler handler)
{
    public async Task RunAsync(TimeSpan idleTimeout, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            using var pipe = CreatePipe();
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

            try
            {
                await ServeOneAsync(pipe, cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // 对方断开：处理下一个连接
            }
        }
    }

    private async Task ServeOneAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Task Send(AgentResponse r) => writer.WriteLineAsync(AgentProtocol.Encode(r));

        var line = await LineReader.ReadLineAsync(pipe, AgentProtocol.MaxLineChars, cancellationToken);
        var request = line is null ? null : AgentProtocol.Decode<AgentRequest>(line);
        if (request is null)
        {
            await Send(new AgentResponse(AgentMessageType.Result, ErrorMessage: "请求格式不对"));
            return;
        }

        // App 断开连接（用户取消、程序被关）时取消安装器：另起一个读取，读到连接关闭就取消
        using var disconnected = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _ = WatchDisconnectAsync(pipe, disconnected);
        await handler.HandleAsync(request, Send, disconnected.Token);
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
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));
        return NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
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
            await pipe.ConnectAsync((int)connectTimeout.TotalMilliseconds, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new AgentUnavailableException("后台助手没有响应", ex);
        }
        catch (IOException ex)
        {
            throw new AgentUnavailableException($"连接后台助手失败：{ex.Message}", ex);
        }

        // 防冒充：管道另一头必须是安装目录里的后台助手
        var serverExe = ServerExecutable(pipe);
        if (!string.Equals(serverExe, Path.GetFullPath(expectedServerExe), StringComparison.OrdinalIgnoreCase))
            throw new AgentUnavailableException($"管道另一头不是后台助手（{serverExe ?? "未知程序"}），已拒绝连接");

        try
        {
            var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
            await writer.WriteLineAsync(AgentProtocol.Encode(request).AsMemory(), cancellationToken);

            while (true)
            {
                var line = await LineReader.ReadLineAsync(pipe, AgentProtocol.MaxLineChars, cancellationToken)
                           ?? throw new AgentUnavailableException("后台助手中途断开了连接");
                var message = AgentProtocol.Decode<AgentResponse>(line)
                              ?? throw new AgentUnavailableException("后台助手回复的格式不对");
                if (message.Type == AgentMessageType.Progress) progress?.Report(message.Progress);
                else return message;
            }
        }
        catch (IOException ex)
        {
            throw new AgentUnavailableException($"和后台助手的连接断开了：{ex.Message}", ex);
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
