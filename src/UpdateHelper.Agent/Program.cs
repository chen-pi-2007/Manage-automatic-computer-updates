using System.Diagnostics;
using System.Security.Principal;
using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Rules.Subscription;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Uninstall;
using UpdateHelper.Winget;

// 更新管理小助手的后台助手。
//   --serve                     由计划任务启动：在管道上等 App 的请求，空闲 2 分钟后退出
//   --install --user-sid <SID>  由 App 以管理员身份运行一次：复制到 Program Files、注册计划任务
//   --disable --user-sid <SID>  由 App 以管理员身份运行：删除这个用户的计划任务
// 退出码：0 成功、2 参数不对、3 复制失败、4 计划任务操作失败、5 没有管理员权限、6 程序包里没有原生启动器

var version = AgentVersion.Current;   // 和 App 侧同一个来源（见 AgentVersion），保证状态核对版本一致
var elevated = new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

if (args.Contains("--serve"))
{
    var sid = AgentPaths.CurrentUserSid();
    var fileProbe = new RealFileProbe();
    var registryProbe = new RealRegistryProbe();

    // 清残留自扫：助手自己重扫该软件，带上匹配到的官方规则（否则注册表键和规则声明的残留扫不出来，会被漏删）。
    // 和 App 一致：内置 rules 目录与已同步缓存择新；助手旁没有内置 rules 时，只认用户已同步的缓存（Phase A 限制）。
    ResiduePreScan Prescan(string keyName, UninstallHive hive)
    {
        var scan = SystemScanner.CreateDefault().Scan();
        var group = scan.Result.Groups.FirstOrDefault(g =>
            g.Primary is { } p && p.KeyName == keyName && p.Hive == hive);
        if (group is null) return new ResiduePreScan(null, []);
        var officialDir = OfficialRules.SelectDirectory(
            Path.Combine(AppContext.BaseDirectory, "rules"), RuleSubscriptionConfig.DefaultCacheDirectory);
        var rules = RuleSetLoader.Load([new RuleSource(officialDir, RuleTrust.Official)]);
        var rule = rules.Rules.FirstOrDefault(r => r.Id == group.RuleId);
        var residueItems = new LeftoverScanner(fileProbe, registryProbe).Scan(group, rule, scan.Result);
        return new ResiduePreScan(group.Primary, residueItems);
    }

    // 运行软件自带的卸载程序。命令由 ResidueRemovalService 从自扫到的注册表项解析（绝不取自请求），这里只负责启动并等退出。
    static bool RunUninstaller(UninstallCommand command)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(command.ExecutablePath, command.Arguments) { UseShellExecute = true });
            if (p is null) return false;
            // 给个宽松上限：卸载程序挂住（或把自己复制到临时目录后秒退、迟迟不收尾）时，别无限期占着助手的管道。
            // 超时不杀它——用户可能正在它的界面上操作——只当作“没干净退出”返回，后续清残留照常进行。
            if (!p.WaitForExit((int)TimeSpan.FromMinutes(10).TotalMilliseconds)) return false;
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    var remover = new LeftoverRemover(new RealLeftoverMutator(), new BackupStore(BackupStore.DefaultRoot, () => DateTimeOffset.Now), () => DateTimeOffset.Now);
    var residue = new ResidueRemovalService(Prescan, remover, fileProbe, registryProbe, RunUninstaller);
    var server = new AgentPipeServer(AgentPaths.PipeName(sid), new AgentRequestHandler(new WingetInstaller(agentMode: true), elevated, version, residue));
    await server.RunAsync(TimeSpan.FromMinutes(2), CancellationToken.None);
    return 0;
}

var userSid = Arg("--user-sid");
if (userSid is null || !AgentSetup.IsValidUserSid(userSid)) return 2;
if (!elevated) return 5;

var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");

if (args.Contains("--disable"))
{
    try
    {
        return Run(schtasks, $"/delete /tn \"{AgentPaths.TaskName(userSid)}\" /f") == 0 ? 0 : 4;
    }
    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
    {
        return 4;
    }
}

if (!args.Contains("--install")) return 2;

// 0. 程序包里必须有原生（AOT）启动器；普通编译的启动器会读取用户环境里的运行时配置，失去加固意义
if (!AgentSetup.IsNativeLauncher(AppContext.BaseDirectory)) return 6;

// 1. 结束安装目录里正在运行的后台助手和启动器（文件被占用就复制不了）
var target = AgentPaths.InstallDirectory;
foreach (var name in new[] { "UpdateHelper.Agent", "UpdateHelper.AgentLauncher" })
foreach (var p in Process.GetProcessesByName(name))
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

// 2.5 复制后再核对一次 Program Files 里的最终副本是不是原生启动器：
//     防止源目录（用户可写的下载目录）在第 0 步检查和这次复制之间被并发进程换成 JIT 版（TOCTOU）
if (!AgentSetup.IsNativeLauncher(target)) return 6;

// 3. 注册计划任务（XML 先写在 Program Files 里，用完删掉）
var xmlFile = Path.Combine(target, "agent-task.xml");
try
{
    try
    {
        File.WriteAllText(xmlFile, AgentSetup.BuildTaskXml(userSid, Path.Combine(target, AgentPaths.LauncherExeName)), System.Text.Encoding.Unicode);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        return 3;
    }
    return Run(schtasks, $"/create /tn \"{AgentPaths.TaskName(userSid)}\" /xml \"{xmlFile}\" /f") == 0 ? 0 : 4;
}
catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
{
    return 4;
}
finally
{
    try { File.Delete(xmlFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
}

static int Run(string exe, string arguments)
{
    using var p = Process.Start(new ProcessStartInfo(exe, arguments) { UseShellExecute = false, CreateNoWindow = true });
    if (p is null) return -1;
    p.WaitForExit();
    return p.ExitCode;
}
