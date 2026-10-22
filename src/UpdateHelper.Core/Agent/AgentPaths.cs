using System.Security.Principal;

namespace UpdateHelper.Core.Agent;

/// <summary>后台助手相关的位置和名字。每个 Windows 用户有自己的计划任务和管道。</summary>
public static class AgentPaths
{
    /// <summary>只有管理员能改写的安装目录；计划任务只运行这里的程序（防止普通权限的程序替换文件后借任务提权）。</summary>
    public static string InstallDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "UpdateHelper");

    public const string AgentExeName = "UpdateHelper.Agent.exe";
    public const string VersionFileName = "agent-version.txt";

    public static string AgentExe => Path.Combine(InstallDirectory, AgentExeName);

    /// <summary>计划任务实际运行的原生小程序：清理环境变量后再启动后台助手。</summary>
    public const string LauncherExeName = "UpdateHelper.AgentLauncher.exe";

    public static string LauncherExe => Path.Combine(InstallDirectory, LauncherExeName);

    public static string TaskName(string userSid) => $@"\UpdateHelper\Agent-{userSid}";

    public static string PipeName(string userSid) => $"UpdateHelper.Agent.{userSid}";

    /// <summary>流式监控通道的管道名：与升级/清残留那条不同名，监控长连接不会占住它。</summary>
    public static string MonitorPipeName(string userSid) => $"UpdateHelper.Monitor.{userSid}";

    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("读不到当前用户的 SID");
    }
}
