using System.Text.RegularExpressions;
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Grouping;

/// <summary>按名称、发布者、安装位置给软件分类。内置规则，计划 2 起可被 YAML 规则覆盖。</summary>
public static partial class SoftwareClassifier
{
    public static SoftwareCategory Classify(UninstallEntry e)
    {
        if (IsGame(e)) return SoftwareCategory.Game;
        if (RuntimePattern().IsMatch(e.DisplayName)) return SoftwareCategory.Runtime;
        if (DriverPattern().IsMatch(e.DisplayName)) return SoftwareCategory.Driver;
        if (DevToolPattern().IsMatch(e.DisplayName) || DevPublisherPattern().IsMatch(e.Publisher ?? ""))
            return SoftwareCategory.DevTool;
        return SoftwareCategory.Application;
    }

    private static bool IsGame(UninstallEntry e)
    {
        if (e.KeyName.StartsWith("Steam App ", StringComparison.OrdinalIgnoreCase)) return true;
        var loc = e.InstallLocation ?? "";
        if (loc.Contains(@"\Launcher", StringComparison.OrdinalIgnoreCase)) return false;   // 平台本身不是游戏
        return GameLocationPattern().IsMatch(loc);
    }

    [GeneratedRegex(@"\\steamapps\\common\\|\\Epic Games\\|\\WeGameApps\\|\\Ubisoft Game Launcher\\games\\", RegexOptions.IgnoreCase)]
    private static partial Regex GameLocationPattern();

    [GeneratedRegex(@"Visual C\+\+.*Redistributable|\.NET.*Runtime|Desktop Runtime|^Java \d+ Update|WebView2 Runtime|WindowsAppRuntime|PhysX|DirectX", RegexOptions.IgnoreCase)]
    private static partial Regex RuntimePattern();

    [GeneratedRegex(@"驱动|\bDriver\b|Chipset|Management Engine", RegexOptions.IgnoreCase)]
    private static partial Regex DriverPattern();

    [GeneratedRegex(@"^Git$|\bGit\b|\bCMake\b|Node\.js|\bPython\b|Visual Studio|\bDocker\b|Anaconda|MSYS2|GitHub CLI|\bArduino\b|\bUnity\b|\bCUDA\b|Nsight|Navicat|Apifox|VirtualBox|VMware|\bCursor\b", RegexOptions.IgnoreCase)]
    private static partial Regex DevToolPattern();

    [GeneratedRegex(@"JetBrains", RegexOptions.IgnoreCase)]
    private static partial Regex DevPublisherPattern();
}
