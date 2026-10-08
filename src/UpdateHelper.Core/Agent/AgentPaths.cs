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

    public static string TaskName(string userSid) => $@"\UpdateHelper\Agent-{userSid}";

    public static string PipeName(string userSid) => $"UpdateHelper.Agent.{userSid}";

    public static string CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("读不到当前用户的 SID");
    }
}
