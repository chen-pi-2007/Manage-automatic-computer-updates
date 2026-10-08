using Microsoft.Management.Deployment;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Winget;

/// <summary>
/// 通过 winget 官方接口静默升级一个包。AllowHashMismatch 永远为 false（文件指纹对不上就不装）。
/// 需要管理员权限的安装包会由 winget 弹出系统的"用户账户控制"确认框。
/// </summary>
public sealed class WingetInstaller : IPackageInstaller
{
    public string Name => "winget";

    public async Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var manager = WingetSession.CreateManager();
        var catalog = WingetSession.ConnectInstalledAndRemote(manager);

        var find = new FindPackagesOptions();
        find.Filters.Add(new PackageMatchFilter
        {
            Field = PackageMatchField.Id,
            Option = PackageFieldMatchOption.Equals,
            Value = packageId,
        });
        var found = catalog.FindPackages(find);
        if (found.Status != FindPackagesResultStatus.Ok || found.Matches.Count == 0)
            return new InstallerReport(false, false, $"在 winget 里找不到 {packageId}", null);
        var package = found.Matches[0].CatalogPackage;   // COM 列表按下标取
        // 后台助手以管理员身份运行：只能升级本机已经装了的软件，不能借它装新软件
        if (package.InstalledVersion is null)
            return new InstallerReport(false, false, $"{packageId} 没有安装在这台电脑上，不能升级", null);

        var options = new InstallOptions
        {
            PackageInstallMode = PackageInstallMode.Silent,
            // 保持原安装范围；"OrUnknown" 让没写范围的安装包也能装
            PackageInstallScope = scope == InstallScopeHint.User
                ? PackageInstallScope.UserOrUnknown
                : PackageInstallScope.SystemOrUnknown,
            AcceptPackageAgreements = true,
            AllowHashMismatch = false,
        };

        // 只装判断层评估过、用户确认过的那个版本。winget 源这期间更新了就不装，避免装上没经过判断的版本
        var latest = package.DefaultInstallVersion?.Version;
        if (!string.Equals(latest, targetVersion, StringComparison.OrdinalIgnoreCase))
        {
            var versionId = FindVersion(package, targetVersion);
            if (versionId is null)
                return new InstallerReport(false, false,
                    $"可用版本已经变了（现在是 {latest ?? "未知"}，检查时是 {targetVersion}），请重新检查更新", null);
            options.PackageVersionId = versionId;
        }

        var winProgress = new Progress<InstallProgress>(p =>
            progress?.Report(p.DownloadProgress * 0.5 + p.InstallationProgress * 0.5));

        // 取消时抛 TaskCanceledException（属于 OperationCanceledException），由 UpdateExecutor 记为"已取消"
        var result = await manager.UpgradePackageAsync(package, options).AsTask(cancellationToken, winProgress);

        if (result.Status == InstallResultStatus.Ok)
            return new InstallerReport(true, result.RebootRequired, null, null);

        var detail = result.ExtendedErrorCode?.Message;
        var message = Describe(result.Status) + (string.IsNullOrWhiteSpace(detail) ? "" : $"：{detail}");
        return new InstallerReport(false, false, message, result.InstallerErrorCode);
    }

    /// <summary>在可用版本里找指定版本；COM 列表按下标遍历。</summary>
    private static PackageVersionId? FindVersion(CatalogPackage package, string version)
    {
        var versions = package.AvailableVersions;
        for (var i = 0; i < versions.Count; i++)
        {
            if (string.Equals(versions[i].Version, version, StringComparison.OrdinalIgnoreCase)) return versions[i];
        }
        return null;
    }

    private static string Describe(InstallResultStatus status) => status switch
    {
        InstallResultStatus.BlockedByPolicy => "被系统策略禁止",
        InstallResultStatus.CatalogError => "无法访问 winget 软件源",
        InstallResultStatus.DownloadError => "下载安装包失败（请检查网络）",
        InstallResultStatus.InstallError => "安装程序运行出错",
        InstallResultStatus.ManifestError => "winget 里这个软件的信息有误",
        InstallResultStatus.NoApplicableInstallers => "没有适合这台电脑（系统版本、架构或安装范围）的安装包",
        InstallResultStatus.NoApplicableUpgrade => "没有可用的升级（可能已经是最新版）",
        InstallResultStatus.PackageAgreementsNotAccepted => "需要先同意软件的许可协议",
        _ => $"winget 内部错误（{status}）",
    };
}
