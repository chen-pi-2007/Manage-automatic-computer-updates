using System.Security.AccessControl;
using System.Security.Principal;
using UpdateHelper.Core.Agent;

namespace UpdateHelper.Core.Tests.Agent;

public class FootprintsDirectoryAclTests
{
    [Fact]
    public void Admins_full_control_users_read_only_no_inheritance()
    {
        using var temp = new TempDir();
        var dir = System.IO.Path.Combine(temp.Path, "footprints");

        AgentSetup.EnsureFootprintsDirectory(dir);

        var rules = new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>().ToList();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);

        Assert.Contains(rules, r => r.IdentityReference == admins && r.FileSystemRights.HasFlag(FileSystemRights.FullControl));
        var userRule = Assert.Single(rules, r => r.IdentityReference == users);
        Assert.False(userRule.FileSystemRights.HasFlag(FileSystemRights.WriteData));
        Assert.False(userRule.FileSystemRights.HasFlag(FileSystemRights.AppendData));
        Assert.True(userRule.FileSystemRights.HasFlag(FileSystemRights.ReadData));
        // 不能有继承来的规则（否则 ProgramData 默认的“Users 可写”会渗进来）
        Assert.DoesNotContain(rules, r => r.IsInherited);
    }
}
