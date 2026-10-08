using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Agent;

/// <summary>
/// 后台助手处理一条请求：校验 → 调安装器 → 回传进度和结果。
/// 对每个请求恰好回一条 Result（或 Pong）；安装器抛异常也转成失败结果，不让 Agent 崩溃。
/// </summary>
public sealed class AgentRequestHandler(IPackageInstaller installer, bool elevated, string agentVersion)
{
    public async Task HandleAsync(AgentRequest request, Func<AgentResponse, Task> send, CancellationToken cancellationToken)
    {
        if (AgentRequestValidator.Validate(request) is { } error)
        {
            await send(Fail(error));
            return;
        }

        if (request.Op == AgentOp.Ping)
        {
            await send(new AgentResponse(AgentMessageType.Pong, AgentVersion: agentVersion, Elevated: elevated));
            return;
        }

        if (!elevated)
        {
            await send(Fail("这个 Windows 账户没有管理员权限，后台助手无法免确认安装。请用管理员账户登录，或关闭免确认更新"));
            return;
        }

        // 进度回调来自安装器的线程：排队按顺序发出，避免并发写管道。
        // 关门（closed）之后迟到的进度直接丢弃，保证没有进度排在结果后面、也没有写操作在 HandleAsync 返回后还在跑。
        var gate = new object();
        var closed = false;
        var pending = Task.CompletedTask;
        var progress = new SyncProgress(p =>
        {
            lock (gate)
            {
                if (closed) return;
                pending = pending.ContinueWith(_ => send(new AgentResponse(AgentMessageType.Progress, Progress: p))).Unwrap();
            }
        });

        // 关门，并等关门前已排队的进度发完
        async Task CloseAndDrainAsync()
        {
            Task last;
            lock (gate)
            {
                closed = true;
                last = pending;
            }
            try { await last; } catch (Exception) { /* 进度没发出去不影响结果 */ }
        }

        AgentResponse result;
        try
        {
            var report = await installer.UpgradeAsync(request.PackageId!, request.TargetVersion!, request.Scope, progress, cancellationToken);
            result = AgentProtocol.ToResult(report);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CloseAndDrainAsync();
            return;   // App 已经断开或取消，没人接收结果
        }
        catch (Exception ex)
        {
            result = Fail($"后台助手安装时出错：{ex.Message}");
        }

        await CloseAndDrainAsync();
        await send(result);
    }

    private static AgentResponse Fail(string message) => new(AgentMessageType.Result, Success: false, ErrorMessage: message);

    /// <summary>同步调用回调的 IProgress（Progress&lt;T&gt; 会切到同步上下文，后台助手里没有界面线程，不需要）。</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double>
    {
        public void Report(double value) => report(value);
    }
}
