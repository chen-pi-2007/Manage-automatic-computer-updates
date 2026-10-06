using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public class InstallerRouterTests
{
    private sealed class Recorder(string name) : IPackageInstaller
    {
        public List<string> Ids { get; } = [];
        public string Name => name;
        public Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
            IProgress<double>? progress, CancellationToken cancellationToken)
        {
            Ids.Add(packageId);
            return Task.FromResult(new InstallerReport(true, false, null, null));
        }
    }

    [Fact]
    public async Task Routes_by_prefix()
    {
        var winget = new Recorder("winget");
        var rules = new Recorder("规则库");
        var router = new InstallerRouter(winget, rules);

        await router.UpgradeAsync("Tencent.QQ.NT", "1", InstallScopeHint.Machine, null, CancellationToken.None);
        await router.UpgradeAsync("rule:kingsoft.wps", "1", InstallScopeHint.Machine, null, CancellationToken.None);

        Assert.Equal(new[] { "Tencent.QQ.NT" }, winget.Ids);
        Assert.Equal(new[] { "rule:kingsoft.wps" }, rules.Ids);
    }
}
