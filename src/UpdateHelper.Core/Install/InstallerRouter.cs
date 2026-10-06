using UpdateHelper.Core.Updates;

namespace UpdateHelper.Core.Install;

/// <summary>按包 id 分流：rule: 开头的交给规则库安装器，其余交给 winget。</summary>
public sealed class InstallerRouter(IPackageInstaller winget, IPackageInstaller rules) : IPackageInstaller
{
    public string Name => $"{winget.Name} + {rules.Name}";

    public Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
        IProgress<double>? progress, CancellationToken cancellationToken)
        => (packageId.StartsWith(RuleUpdateSource.PackagePrefix, StringComparison.Ordinal) ? rules : winget)
            .UpgradeAsync(packageId, targetVersion, scope, progress, cancellationToken);
}
