using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using UpdateHelper.Core.Agent;
using UpdateHelper.Winget;

// 更新管理小助手的后台助手。
//   --serve                     由计划任务启动：在管道上等 App 的请求，空闲 2 分钟后退出
//   --install --user-sid <SID>  由 App 以管理员身份运行一次：复制到 Program Files、注册计划任务
//   --disable --user-sid <SID>  由 App 以管理员身份运行：删除这个用户的计划任务
// 退出码：0 成功、2 参数不对、3 复制失败、4 计划任务操作失败、5 没有管理员权限

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

if (args.Contains("--serve"))
{
    var sid = AgentPaths.CurrentUserSid();
    var server = new AgentPipeServer(AgentPaths.PipeName(sid), new AgentRequestHandler(new WingetInstaller(), elevated, version));
    await server.RunAsync(TimeSpan.FromMinutes(2), CancellationToken.None);
    return 0;
}

var userSid = Arg("--user-sid");
if (userSid is null || !AgentSetup.IsValidUserSid(userSid)) return 2;
if (!elevated) return 5;

var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");

if (args.Contains("--disable"))
    return Run(schtasks, $"/delete /tn \"{AgentPaths.TaskName(userSid)}\" /f") == 0 ? 0 : 4;

if (!args.Contains("--install")) return 2;

// 1. 结束安装目录里正在运行的后台助手（文件被占用就复制不了）
var target = AgentPaths.InstallDirectory;
foreach (var p in Process.GetProcessesByName("UpdateHelper.Agent"))
{
    try
    {
        if (p.Id != Environment.ProcessId && p.MainModule?.FileName is { } file
            && file.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            p.Kill();
            p.WaitForExit(5000);
        }
    }
    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    finally { p.Dispose(); }
}

// 2. 复制整个程序目录到 Program Files（只有管理员能改写）
try
{
    AgentSetup.Deploy(AppContext.BaseDirectory, target, version);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
{
    return 3;
}

// 3. 注册计划任务（XML 先写在 Program Files 里，用完删掉）
var xmlFile = Path.Combine(target, "agent-task.xml");
try
{
    File.WriteAllText(xmlFile, AgentSetup.BuildTaskXml(userSid, Path.Combine(target, AgentPaths.AgentExeName)), System.Text.Encoding.Unicode);
    return Run(schtasks, $"/create /tn \"{AgentPaths.TaskName(userSid)}\" /xml \"{xmlFile}\" /f") == 0 ? 0 : 4;
}
finally
{
    try { File.Delete(xmlFile); } catch (IOException) { }
}

static int Run(string exe, string arguments)
{
    using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true })!;
    p.WaitForExit();
    return p.ExitCode;
}
