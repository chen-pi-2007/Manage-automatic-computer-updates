using System.Globalization;
using System.Security;

namespace UpdateHelper.Core.Scanning;

/// <summary>把注册表里类型不可靠的值安全地转换成需要的类型；转换不了就当作缺失。</summary>
public static class RegistryValues
{
    public static string? AsString(object? value) => value switch
    {
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        int or long or uint or ulong => Convert.ToString(value, CultureInfo.InvariantCulture),
        _ => null,
    };

    public static long? AsLong(object? value) => value switch
    {
        int i => i,
        long l => l,
        uint u => u,
        string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    public static bool AsFlag(object? value) => AsLong(value) is { } n && n != 0;

    /// <summary>
    /// 用 getValue（按值名取原始值）组装一条卸载登记。
    /// 没有 DisplayName 的键不算软件，返回 null；键不可读（权限不足等）也返回 null。
    /// </summary>
    public static UninstallEntry? ToEntry(string keyName, UninstallHive hive, Func<string, object?> getValue)
    {
        try
        {
            var name = AsString(getValue("DisplayName"));
            if (name is null) return null;

            return new UninstallEntry(
                KeyName: keyName,
                Hive: hive,
                DisplayName: name,
                DisplayVersion: AsString(getValue("DisplayVersion")),
                Publisher: AsString(getValue("Publisher")),
                InstallLocation: AsString(getValue("InstallLocation"))
                    ?? InferInstallLocation(AsString(getValue("DisplayIcon")), AsString(getValue("UninstallString"))),
                UninstallString: AsString(getValue("UninstallString")),
                QuietUninstallString: AsString(getValue("QuietUninstallString")),
                IsSystemComponent: AsFlag(getValue("SystemComponent")),
                ParentKeyName: AsString(getValue("ParentKeyName")),
                ReleaseType: AsString(getValue("ReleaseType")),
                EstimatedSizeKb: AsLong(getValue("EstimatedSize")));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// 登记里没写安装位置时，用图标所在目录、再用卸载程序所在目录推断。
    /// 推断出 Windows 目录、安装包缓存或磁盘根目录时不采用（这些目录不属于任何一个软件）。
    /// </summary>
    public static string? InferInstallLocation(string? displayIcon, string? uninstallString)
    {
        foreach (var source in new[] { StripIconIndex(displayIcon), uninstallString })
        {
            var exe = CommandLineParser.ExtractExecutable(source);
            var dir = exe is null ? null : SafeDirectoryName(exe);
            if (dir is not null && IsUsableDirectory(dir)) return dir;
        }
        return null;
    }

    /// <summary>"C:\x\a.exe,0" → "C:\x\a.exe"</summary>
    private static string? StripIconIndex(string? icon)
    {
        if (icon is null) return null;
        var comma = icon.LastIndexOf(',');
        return comma > 0 && int.TryParse(icon[(comma + 1)..], out _) ? icon[..comma] : icon;
    }

    private static string? SafeDirectoryName(string path)
    {
        try { return Path.GetDirectoryName(path.Trim('"')); }
        catch (ArgumentException) { return null; }
    }

    private static bool IsUsableDirectory(string dir)
    {
        if (!Path.IsPathFullyQualified(dir)) return false;
        if (Path.GetPathRoot(dir)?.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) == true)
            return false;   // 磁盘根目录

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var packageCache = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Package Cache");
        return !IsUnderDir(dir, windows) && !IsUnderDir(dir, packageCache);
    }

    private static bool IsUnderDir(string dir, string parent) =>
        parent.Length > 0 &&
        (dir.TrimEnd('\\') + "\\").StartsWith(parent.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
