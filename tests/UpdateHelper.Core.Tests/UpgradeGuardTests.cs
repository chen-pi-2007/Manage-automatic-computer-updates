using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public sealed class UpgradeGuardTests
{
    [Theory]
    [InlineData("1.0", "1.1")]
    [InlineData("9.9.20.36330", "9.9.21")]
    [InlineData("2.10.91", "2.10.91.1")]
    public void Allows_newer_versions(string installed, string target)
    {
        Assert.Null(UpgradeGuard.Check(installed, target, InstallScopeHint.Machine, InstallScopeHint.Machine));
    }

    [Theory]
    [InlineData("1.1", "1.0")]
    [InlineData("1.0", "1.0")]
    [InlineData("9.9.21", "9.9.20.36330")]
    public void Refuses_downgrade_or_reinstall(string installed, string target)
    {
        Assert.Contains("不比已安装的", UpgradeGuard.Check(installed, target, InstallScopeHint.Machine, null));
    }

    [Theory]
    [InlineData(null, "1.0")]
    [InlineData("< 3.10.8", "3.10.9")]
    [InlineData("1.0", "not-a-version")]
    public void Refuses_when_versions_cannot_be_compared(string? installed, string target)
    {
        Assert.Contains("无法确认", UpgradeGuard.Check(installed, target, InstallScopeHint.Machine, null));
    }

    [Fact]
    public void Refuses_scope_change_when_installed_scope_is_known()
    {
        Assert.Contains("安装范围", UpgradeGuard.Check("1.0", "1.1", InstallScopeHint.Machine, InstallScopeHint.User));
        Assert.Null(UpgradeGuard.Check("1.0", "1.1", InstallScopeHint.User, InstallScopeHint.User));
        Assert.Null(UpgradeGuard.Check("1.0", "1.1", InstallScopeHint.Machine, null));   // winget 没给出范围：不因此拒绝
    }
}
