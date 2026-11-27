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
                    return ValueExists(e.Path);
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

    /// <summary>
    /// 文件的创建时间早于监控开始（留 2 秒余量）→ 它不是这次装出来的。
    /// 改名覆盖用户原有文件时，Windows 的文件名隧道会让结果继承原文件的创建时间，所以这能兜住"临时文件改名覆盖原有文件"。
    /// 查不了就返回 false（不改变原判断）。
    /// </summary>
    public bool ExistedBefore(FootprintEntry e, DateTimeOffset since)
    {
        if (e.Type != FootprintEntryType.File) return false;
        try
        {
            return File.GetCreationTimeUtc(e.Path) < since.UtcDateTime.AddSeconds(-2);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 值的路径是"键路径\值名"，但值名本身可以带反斜杠（比如 SharedDLLs 拿完整文件路径当值名），
    /// 不能只按最后一个反斜杠切。键名不能含反斜杠，所以挨个切分点去试：左边是存在的键、右边是其中存在的值，就算存在。
    /// </summary>
    private static bool ValueExists(string path)
    {
        var first = path.IndexOf('\\');
        if (first <= 0) return true;   // 连根都没有：认不出，保守保留
        for (var i = path.IndexOf('\\', first + 1); i > 0; i = path.IndexOf('\\', i + 1))
        {
            using var key = OpenKey(path[..i]);
            if (key is null) break;   // 更长的前缀只会更不存在
            if (key.GetValue(path[(i + 1)..]) is not null) return true;
        }
        return false;
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
