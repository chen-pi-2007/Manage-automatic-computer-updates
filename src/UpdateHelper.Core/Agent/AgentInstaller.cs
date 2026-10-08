using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Agent;

/// <summary>启动后台助手（真实实现：运行计划任务）。</summary>
public interface IAgentLauncher
{
    void Start();
}

/// <summary>
/// App 侧的安装器：把"升级某个 winget 包"交给以管理员身份运行的后台助手，不弹确认框。
/// 后台助手没在运行就先通过计划任务启动它；在 startTimeout 内连不上就返回失败（不会卡住）。
/// </summary>
public sealed class AgentInstaller(IAgentLauncher launcher, AgentPipeClient client, TimeSpan? startTimeout = null) : IPackageInstaller
{
    private static readonly TimeSpan QuickTry = TimeSpan.FromMilliseconds(500);
    private const string Hint = "可以在设置里重新启用免确认更新";

    public string Name => "后台助手";

    public async Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var request = new AgentRequest(AgentProtocol.Version, AgentOp.Upgrade, packageId, targetVersion, scope);
        try
        {
            try
            {
                return AgentProtocol.ToReport(await client.SendAsync(request, progress, QuickTry, cancellationToken));
            }
            catch (AgentUnavailableException ex) when (ex.Kind == AgentFailureKind.NotRunning && !cancellationToken.IsCancellationRequested)
            {
                // 没在运行（只有请求还没发出去时才会重试；发出后断开不能重发，否则可能重复安装）：通过计划任务启动，再等它准备好
                launcher.Start();
                return AgentProtocol.ToReport(await client.SendAsync(request, progress,
                    startTimeout ?? TimeSpan.FromSeconds(15), cancellationToken));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // 取消优先：用户已取消时，不把连接错误包装成失败报告
            cancellationToken.ThrowIfCancellationRequested();
            if (ex is AgentUnavailableException { Kind: AgentFailureKind.Broken })
                return new InstallerReport(false, false, $"和后台助手的连接中途断开（{ex.Message}），这次更新可能没有完成，请重新检查更新", null);
            return new InstallerReport(false, false, $"后台助手没有响应（{ex.Message}），{Hint}", null);
        }
    }
}
