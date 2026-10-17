using UpdateHelper.Core.Install;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Core.Agent;

/// <summary>后台助手以管理员身份安装前的最后一道检查：只升不降，安装范围跟已安装的一致。</summary>
public static class UpgradeGuard
{
    public static string? Check(string? installedVersion, string targetVersion,
        InstallScopeHint requestedScope, InstallScopeHint? installedScope)
    {
        var installed = AppVersion.Parse(installedVersion);
        var target = AppVersion.Parse(targetVersion);
        if (installed is null || target is null || installed.IsApproximate || target.IsApproximate)
            return $"无法确认 {targetVersion} 比已安装的 {installedVersion ?? "（未知）"} 新，后台助手不安装";
        if (target.CompareTo(installed) <= 0)
            return $"目标版本 {targetVersion} 不比已安装的 {installedVersion} 新，后台助手不做降级或重装";
        if (installedScope is { } scope && scope != requestedScope)
            return "请求的安装范围和已安装的不一致，后台助手不安装";
        return null;
    }
}
