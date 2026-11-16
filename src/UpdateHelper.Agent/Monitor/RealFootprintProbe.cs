using Microsoft.Win32;
using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Agent.Monitor;

/// <summary>
/// 收尾复验的真实现：足迹条目是否还在磁盘/注册表上。
/// 注册表路径用 HKLM\... / HKCU\... / HKU\... 写法（EtwMonitorSession 翻译出来的就是这种）。
/// 探测出错（无权限、路径非法）时返回 true：无法确认就保守保留，
/// 宁可多留一条让用户看到，也不因探测失败悄悄丢足迹。
/// </summary>
public sealed class RealFootprintProbe : IFootprintProbe
{
    public bool StillExists(FootprintEntry e)
    {
        try
        {
            switch (e.Type)
            {
                case FootprintEntryType.File:
                    return File.Exists(e.Path);
                case FootprintEntryType.Directory:   // 本阶段不产出，仅为完整性保留
                    return Directory.Exists(e.Path);
                case FootprintEntryType.RegistryKey:
                    using (var k = OpenKey(e.Path)) return k is not null;
                case FootprintEntryType.RegistryValue:
                {
                    var i = e.Path.LastIndexOf('\\');
                    if (i <= 0) return true;
                    using var key = OpenKey(e.Path[..i]);
                    return key is not null && key.GetValue(e.Path[(i + 1)..]) is not null;
                }
                case FootprintEntryType.Service:
                case FootprintEntryType.Driver:   // 驱动也是服务键下的一项，名字即服务名
                    using (var s = OpenKey(@"HKLM\SYSTEM\CurrentControlSet\Services\" + e.Path)) return s is not null;
                case FootprintEntryType.ScheduledTask:   // 名字形如 \Vendor\Updater，对应 System32\Tasks 下的任务文件
                    return File.Exists(Path.Combine(RealSystemInventory.TasksRoot, e.Path.TrimStart('\\')));
                default:
                    return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or System.Security.SecurityException or ArgumentException)
        {
            return true;   // 无法确认 → 保守保留
        }
    }

    private static RegistryKey? OpenKey(string path)
    {
        var parts = path.Replace('/', '\\').Split('\\', 2);
        if (parts.Length < 2 || parts[1].Length == 0) return null;
        var root = parts[0].ToUpperInvariant() switch
        {
            "HKLM" => Registry.LocalMachine,
            "HKCU" => Registry.CurrentUser,
            "HKCR" => Registry.ClassesRoot,
            "HKU" => Registry.Users,
            _ => null,
        };
        // Agent 是 x64 进程；路径里若有 Wow6432Node 会被原样保留，直接打开即可
        return root?.OpenSubKey(parts[1]);
    }
}
