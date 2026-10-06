using System.Diagnostics;

namespace UpdateHelper.Core.Install;

public interface IProcessRunner
{
    /// <summary>运行程序并返回退出码。取消只在启动前生效。</summary>
    Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken);
}

/// <summary>
/// 运行安装包。UseShellExecute=true：安装包要求管理员权限时由系统弹出"用户账户控制"确认框。
/// 安装程序启动后不会因为取消而被结束——强行结束安装程序可能把软件装坏。
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<int> RunAsync(string fileName, string arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var process = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        }) ?? throw new InvalidOperationException($"无法启动 {Path.GetFileName(fileName)}");

        await process.WaitForExitAsync(CancellationToken.None);
        return process.ExitCode;
    }
}
