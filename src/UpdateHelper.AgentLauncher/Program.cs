using System.Collections;
using System.Diagnostics;
using Microsoft.Win32;
using UpdateHelper.AgentLauncher;

// 计划任务运行的就是这个程序。它只认一个参数 --monitor（监控任务用），其余参数一律忽略：
// 读系统级 Path → 只保留白名单里的环境变量 → 以 --serve（监控任务为 --monitor-serve）启动同目录下的后台助手
// → 等它结束，返回它的退出码。模式只有这两种固定取值，绝不把外部参数原样传给助手。

var directory = AppContext.BaseDirectory;
var agent = Path.Combine(directory, "UpdateHelper.Agent.exe");
if (!File.Exists(agent)) return 10;

var systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
string? machinePath = null;
try
{
    using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
    if (key?.GetValue("Path") is string raw) machinePath = ExpandSystem(raw, systemRoot);
}
catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }

var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
    if (e.Key is string k && e.Value is string v) current[k] = v;

var mode = args.Length == 1 && args[0] == "--monitor" ? "--monitor-serve" : "--serve";
var start = new ProcessStartInfo(agent, mode)
{
    UseShellExecute = false,
    CreateNoWindow = true,
    WorkingDirectory = directory,
};
start.Environment.Clear();
foreach (var (name, value) in CleanEnvironment.Build(current, machinePath, systemRoot))
    start.Environment[name] = value;

try
{
    using var process = Process.Start(start);
    if (process is null) return 11;
    process.WaitForExit();
    return process.ExitCode;
}
catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
{
    return 11;
}

// 系统级 Path 里只会用到 %SystemRoot% 这类系统变量；不用 Environment.ExpandEnvironmentVariables（那会用到用户的变量）
static string ExpandSystem(string value, string systemRoot) =>
    value.Replace("%SystemRoot%", systemRoot, StringComparison.OrdinalIgnoreCase)
         .Replace("%windir%", systemRoot, StringComparison.OrdinalIgnoreCase);
