namespace UpdateHelper.Core.Monitor;

/// <summary>落点分类：驻留（保留）/ 噪声（丢弃）/ 模糊（留待后续判断）。</summary>
public enum LocationClass { Keep, Drop, Ambiguous }

/// <summary>
/// 落点判定所用的目录基准。做成可注入的记录，测试可传固定值，
/// 不依赖本机真实的 Program Files / 用户目录。
/// </summary>
public sealed record KnownFolders(
    string ProgramFiles, string ProgramFilesX86, string ProgramData,
    string LocalAppData, string RoamingAppData, string UserTemp, string Windows)
{
    /// <summary>取自当前机器；systemDrive 用于推出 Windows 目录。</summary>
    public static KnownFolders Current(string? systemDrive = "C:")
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var drive = string.IsNullOrEmpty(systemDrive) ? "C:" : systemDrive;
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        return new KnownFolders(
            F(Environment.SpecialFolder.ProgramFiles),
            F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData),
            local,
            F(Environment.SpecialFolder.ApplicationData),
            Path.Combine(local, "Temp"),
            drive + @"\Windows");
    }
}

/// <summary>
/// 按路径把足迹分成三档。先看黑名单（噪声优先，因为 Temp 就在 LocalAppData 之下），
/// 再看驻留白名单，都不中则为模糊。大小写不敏感，前缀按目录边界比较。
/// </summary>
public static class LocationClassifier
{
    public static LocationClass Classify(string win32Path, string? systemDrive = "C:")
        => Classify(win32Path, KnownFolders.Current(systemDrive));

    public static LocationClass Classify(string win32Path, KnownFolders k)
    {
        // .etl 是系统追踪日志，无论在哪都是噪声
        if (win32Path.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)) return LocationClass.Drop;

        string[] drop =
        {
            k.UserTemp,
            Path.Combine(k.Windows, "Temp"),
            Path.Combine(k.Windows, "Prefetch"),
            Path.Combine(k.ProgramData, "Microsoft", "Windows", "WER"), // 错误报告
        };
        if (drop.Any(d => IsUnder(win32Path, d))) return LocationClass.Drop;

        string[] keep = { k.ProgramFiles, k.ProgramFilesX86, k.ProgramData, k.LocalAppData, k.RoamingAppData };
        if (keep.Any(d => IsUnder(win32Path, d))) return LocationClass.Keep;

        return LocationClass.Ambiguous;
    }

    /// <summary>path 是否等于 dir 或在其之下（按目录边界，Program Files2 不算在 Program Files 下）。</summary>
    private static bool IsUnder(string path, string dir)
    {
        if (string.IsNullOrEmpty(dir)) return false; // 基准取不到时不参与匹配
        dir = dir.TrimEnd('\\');
        if (!path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) return false;
        return path.Length == dir.Length || path[dir.Length] == '\\';
    }
}
