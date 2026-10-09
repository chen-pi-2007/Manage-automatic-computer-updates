using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Core.Uninstall;

/// <summary>系统目录和系统注册表键的白名单保护：这些永远不作为残留列出（spec 第 7 节）。</summary>
public static class SystemPaths
{
    // 整个子树受保护：Windows 目录之内没有任何合法的软件自有残留
    private static readonly string[] SubtreeProtectedDirs =
    [
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
    ];

    // 仅"根本身"受保护：这些容器目录不能整个当残留删，但厂商自己的子目录是真实残留
    private static readonly string?[] RootOnlyDirs = BuildRootOnlyDirs();

    private static string?[] BuildRootOnlyDirs()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return
        [
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "AppData", "LocalLow"),
            string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads"),
            profile,
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            @"C:\Users", @"C:\Users\Public", @"C:\",
        ];
    }

    /// <summary>候选目录是否受保护：
    /// 1) 位于 Windows 目录之内（含本身）→ 受保护；
    /// 2) 等于 Program Files、ProgramData、AppData/LocalAppData/LocalLow 根、用户目录、桌面、文档、下载、C:\Users、公共用户等"容器根"→ 受保护，
    ///    但它们之内的具体子目录（如 Program Files\Foo、%LOCALAPPDATA%\Vendor）不受保护；
    /// 3) 盘符根目录受保护；4) 无法规范化的路径保守起见视为受保护。</summary>
    public static bool IsProtectedDirectory(string path)
    {
        var norm = PathUtil.NormalizeDir(path);
        if (norm is null) return true;
        foreach (var p in SubtreeProtectedDirs)
            if (!string.IsNullOrEmpty(p) && PathUtil.IsUnder(norm, p)) return true;
        foreach (var p in RootOnlyDirs)
        {
            var dir = PathUtil.NormalizeDir(p);
            if (dir is not null && string.Equals(norm, dir, StringComparison.OrdinalIgnoreCase)) return true;
        }
        var trimmed = norm.TrimEnd('\\');
        if (trimmed.Length == 2 && trimmed[1] == ':') return true;
        return false;
    }

    /// <summary>把长名根（HKEY_LOCAL_MACHINE 等）换成短名（HKLM 等）；其它路径只做斜杠和空白规整。</summary>
    public static string NormalizeRegistryPath(string path)
    {
        var p = path.Replace('/', '\\').Trim();
        (string Long, string Short)[] map =
        [
            ("HKEY_LOCAL_MACHINE", "HKLM"), ("HKEY_CURRENT_USER", "HKCU"), ("HKEY_CLASSES_ROOT", "HKCR"),
        ];
        foreach (var (l, s) in map)
        {
            if (p.Equals(l, StringComparison.OrdinalIgnoreCase)) return s;
            if (p.StartsWith(l + "\\", StringComparison.OrdinalIgnoreCase)) return s + p[l.Length..];
        }
        return p;
    }

    /// <summary>系统注册表键：系统分支、容器键（HKLM\SOFTWARE、HKCU\Software、WOW6432Node 本身）、
    /// Microsoft/Classes/Policies 子树、整个 HKCR 不作为残留。厂商键（HKCU\Software\厂商）不在此列。</summary>
    public static bool IsProtectedRegistryKey(string path)
    {
        var p = NormalizeRegistryPath(path).TrimEnd('\\');
        if (p.Equals("HKLM", StringComparison.OrdinalIgnoreCase) || p.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
            || p.Equals("HKCR", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(@"HKCR\", StringComparison.OrdinalIgnoreCase)) return true;

        string[] exact = [@"HKLM\SOFTWARE", @"HKCU\Software", @"HKLM\SOFTWARE\WOW6432Node"];
        if (exact.Any(e => p.Equals(e, StringComparison.OrdinalIgnoreCase))) return true;

        string[] subtree =
        [
            @"HKLM\SYSTEM", @"HKLM\SECURITY", @"HKLM\SAM", @"HKLM\HARDWARE",
            @"HKLM\SOFTWARE\Microsoft", @"HKCU\SOFTWARE\Microsoft", @"HKLM\SOFTWARE\WOW6432Node\Microsoft",
            @"HKLM\SOFTWARE\Classes", @"HKLM\SOFTWARE\Policies", @"HKCU\SOFTWARE\Policies",
        ];
        return subtree.Any(pre =>
            p.Equals(pre, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(pre + "\\", StringComparison.OrdinalIgnoreCase));
    }
}
