using System.Text.RegularExpressions;
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Uninstall;

/// <summary>把注册表里的 UninstallString / QuietUninstallString 解析成可运行的命令。纯函数。</summary>
public static partial class UninstallCommandParser
{
    /// <summary>解析卸载命令。返回 null 表示没有可用命令或卸载程序文件缺失（应走强制删除）。</summary>
    public static UninstallCommand? Parse(UninstallEntry entry, IFileProbe files, bool preferSilent)
    {
        var quiet = string.IsNullOrWhiteSpace(entry.QuietUninstallString) ? null : entry.QuietUninstallString;
        var normal = string.IsNullOrWhiteSpace(entry.UninstallString) ? null : entry.UninstallString;

        var usingQuiet = preferSilent && quiet is not null;
        var raw = usingQuiet ? quiet : normal ?? quiet;
        if (raw is null) return null;
        if (normal is null && quiet is not null) usingQuiet = true;   // 只有静默串时，它本身就是静默命令

        string expanded;
        try { expanded = Environment.ExpandEnvironmentVariables(raw.Trim()); }
        catch (ArgumentException) { return null; }

        var split = Split(expanded, files);
        if (split is not { } parts) return null;
        var (exe, args) = parts;

        var isMsi = Path.GetFileNameWithoutExtension(exe).Equals("msiexec", StringComparison.OrdinalIgnoreCase);
        if (isMsi)
        {
            // msiexec 是系统自带的，不查文件；/I 是"安装/修复"，卸载必须是 /X
            var msiArgs = InstallSwitch().Replace(args, "$1/X");
            var silent = preferSilent || usingQuiet || QuietSwitch().IsMatch(msiArgs);
            if (silent && !QuietSwitch().IsMatch(msiArgs)) msiArgs = (msiArgs + " /qn").Trim();
            return new UninstallCommand(exe, msiArgs, true, silent);
        }

        if (!files.FileExists(exe)) return null;
        return new UninstallCommand(exe, args, false, usingQuiet);
    }

    /// <summary>拆出可执行文件和参数。带引号取引号内；不带引号时逐段试探带空格的路径。</summary>
    private static (string Exe, string Args)? Split(string text, IFileProbe files)
    {
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            if (end <= 1) return null;
            return (text[1..end], text[(end + 1)..].Trim());
        }

        // 不带引号：优先找"前缀恰好是存在的文件"的最短切分，兼容 C:\Program Files\X\u.exe /S
        for (var i = text.IndexOf(' '); i >= 0; i = text.IndexOf(' ', i + 1))
        {
            var candidate = text[..i];
            if (files.FileExists(candidate)) return (candidate, text[(i + 1)..].Trim());
        }
        if (files.FileExists(text)) return (text, "");

        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..].Trim());
    }

    // 匹配开头或空白后的 /I（大小写无关）
    [GeneratedRegex(@"(^|\s)/[Ii](?=\s|\{|$)")]
    private static partial Regex InstallSwitch();

    [GeneratedRegex(@"(^|\s)/(qn|quiet|passive|qb|qr)\b", RegexOptions.IgnoreCase)]
    private static partial Regex QuietSwitch();
}
