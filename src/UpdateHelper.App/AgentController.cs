using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using UpdateHelper.Core.Agent;

namespace UpdateHelper.App;

/// <summary>App 侧管理后台助手：查状态、以管理员身份启用 / 关闭、通过计划任务启动、创建安装器。</summary>
public sealed class AgentController
{
    private static readonly string Schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
    private readonly string _sid = AgentPaths.CurrentUserSid();

    public static string Version { get; } = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public AgentStatus GetStatus() =>
        AgentSetup.GetStatus(AgentPaths.InstallDirectory, Version, TaskExists());

    public IAgentLauncher Launcher => new TaskLauncher(_sid);

    public AgentInstaller CreateInstaller() =>
        new(Launcher, new AgentPipeClient(AgentPaths.PipeName(_sid), AgentPaths.AgentExe));

    /// <summary>弹一次管理员确认框，复制到 Program Files 并注册计划任务。null = 成功。</summary>
    public Task<string?> EnableAsync() => RunElevatedAsync("--install");

    /// <summary>弹一次管理员确认框，删除计划任务。Program Files 里的文件保留（以后的完整安装程序负责卸载）。</summary>
    public Task<string?> DisableAsync() => RunElevatedAsync("--disable");

    private Task<string?> RunElevatedAsync(string verb) => Task.Run<string?>(() =>
    {
        // 用当前程序目录里的那份（启用前 Program Files 里还没有）
        var agent = Path.Combine(AppContext.BaseDirectory, AgentPaths.AgentExeName);
        if (!File.Exists(agent)) return "程序目录里缺少 UpdateHelper.Agent.exe，请重新下载完整的程序包";
        try
        {
            using var p = Process.Start(new ProcessStartInfo(agent, $"{verb} --user-sid {_sid}")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            })!;
            p.WaitForExit();
            return p.ExitCode switch
            {
                0 => null,
                3 => "复制到 Program Files 失败，可能有文件被占用，请关闭后重试",
                4 => "创建或删除计划任务失败",
                5 => "没有获得管理员权限",
                _ => $"后台助手返回错误（{p.ExitCode}）",
            };
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "已取消（在确认框里点了“否”）";
        }
    });

    private bool TaskExists()
    {
        using var p = Process.Start(new ProcessStartInfo(Schtasks, $"/query /tn \"{AgentPaths.TaskName(_sid)}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        if (!p.WaitForExit(10_000))
        {
            KillQuietly(p);
            return false;
        }
        return p.ExitCode == 0;
    }

    private static void KillQuietly(Process p)
    {
        try { p.Kill(); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
    }

    private sealed class TaskLauncher(string sid) : IAgentLauncher
    {
        public void Start()
        {
            using var p = Process.Start(new ProcessStartInfo(Schtasks, $"/run /tn \"{AgentPaths.TaskName(sid)}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            if (!p.WaitForExit(10_000))
            {
                KillQuietly(p);
                throw new InvalidOperationException("启动后台助手的计划任务超时");
            }
            if (p.ExitCode != 0)
                throw new InvalidOperationException("启动后台助手的计划任务失败（可能已被删除）");
        }
    }
}
