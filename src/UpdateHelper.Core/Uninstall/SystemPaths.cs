using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Core.Uninstall;

/// <summary>系统目录和系统注册表键的白名单保护：这些永远不作为残留列出（spec 第 7 节）。</summary>
public static class SystemPaths
{
    private static readonly string[] ProtectedDirs =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        @"C:\Users", @"C:\",
    ];

    /// <summary>候选目录是否受保护：等于或位于上面任一系统目录"本身"，或是盘符根目录。
    /// 注意：系统目录"之内"的具体子目录（如某软件装在 Program Files\Foo）不算受保护。</summary>
    public static bool IsProtectedDirectory(string path)
    {
        var norm = PathUtil.NormalizeDir(path);
        if (norm is null) return true;   // 规范化不了的，保守起见不列
        foreach (var p in ProtectedDirs)
        {
            var dir = PathUtil.NormalizeDir(p);
            if (dir is not null && string.Equals(norm, dir, StringComparison.OrdinalIgnoreCase)) return true;
        }
        // 盘符根目录（C:\ D:\ …）
        var trimmed = norm.TrimEnd('\\');
        if (trimmed.Length == 2 && trimmed[1] == ':') return true;
        return false;
    }

    /// <summary>系统注册表键：HKLM\SYSTEM、HKLM\SOFTWARE\Microsoft\Windows 等不作为残留。
    /// 规则声明的 HKCU/HKLM\Software\<厂商> 不在此列。</summary>
    public static bool IsProtectedRegistryKey(string path)
    {
        var p = path.Replace('/', '\\').Trim().TrimEnd('\\');
        string[] protectedPrefixes =
        [
            @"HKLM\SYSTEM", @"HKLM\SECURITY", @"HKLM\SAM", @"HKLM\HARDWARE",
            @"HKLM\SOFTWARE\Microsoft\Windows", @"HKLM\SOFTWARE\Microsoft\Windows NT",
            @"HKCU\SOFTWARE\Microsoft\Windows",
        ];
        // 等于保护前缀，或是它的祖先（如单独一个 HKLM、HKCU）
        if (p.Equals("HKLM", StringComparison.OrdinalIgnoreCase) || p.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
            || p.Equals("HKCR", StringComparison.OrdinalIgnoreCase)) return true;
        return protectedPrefixes.Any(pre =>
            p.Equals(pre, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(pre + "\\", StringComparison.OrdinalIgnoreCase));
    }
}
