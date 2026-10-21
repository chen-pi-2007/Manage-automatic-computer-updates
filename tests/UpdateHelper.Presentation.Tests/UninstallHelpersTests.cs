using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Uninstall;
using UpdateHelper.Presentation;

namespace UpdateHelper.Presentation.Tests;

public sealed class UninstallHelpersTests
{
    [Fact]
    public void Message_mixes_removed_reboot_and_failed()
    {
        var msg = UninstallMessages.Build(new RemovalResult(3, 2, ["a", "b", "c"], "id"), nativeRan: true, nativeSkipped: false);
        Assert.Contains("已清理 3 项", msg);
        Assert.Contains("2 项将在重启后删除", msg);
        Assert.Contains("3 项未能处理", msg);
    }

    [Fact]
    public void Message_only_removed_has_no_noise()
    {
        var msg = UninstallMessages.Build(new RemovalResult(2, 0, [], "id"), nativeRan: true, nativeSkipped: false);
        Assert.Contains("已清理 2 项", msg);
        Assert.DoesNotContain("重启", msg);
        Assert.DoesNotContain("未能处理", msg);
    }

    [Fact]
    public void Message_nothing_done_says_so()
    {
        var msg = UninstallMessages.Build(new RemovalResult(0, 0, [], null), nativeRan: false, nativeSkipped: false);
        Assert.Contains("没有清理任何残留", msg);
    }

    [Fact]
    public void Message_mentions_skipped_native_uninstaller()
    {
        var msg = UninstallMessages.Build(new RemovalResult(1, 0, [], "id"), nativeRan: false, nativeSkipped: true);
        Assert.Contains("找不到", msg);
        Assert.Contains("强制删除", msg);
    }

    [Fact]
    public void Message_all_failed_is_not_reported_as_success()
    {
        var msg = UninstallMessages.Build(new RemovalResult(0, 0, ["a"], null), nativeRan: true, nativeSkipped: false);
        Assert.Contains("1 项未能处理", msg);
        Assert.DoesNotContain("已清理", msg);
    }

    [Fact]
    public void Message_surfaces_quarantine_path_when_persist_failed()
    {
        var msg = UninstallMessages.Build(
            new RemovalResult(1, 0, [], "id", @"C:\Backups\abc"), nativeRan: true, nativeSkipped: false);
        Assert.Contains(@"C:\Backups\abc", msg);
        Assert.Contains("手动", msg);   // 明确告诉用户可从该目录手动找回
    }

    [Theory]
    [InlineData(UninstallHive.CurrentUser, false)]
    [InlineData(UninstallHive.LocalMachine64, true)]
    [InlineData(UninstallHive.LocalMachine32, true)]
    [InlineData(null, true)]
    public void Routing_machine_scope_goes_to_agent(UninstallHive? hive, bool viaAgent)
        => Assert.Equal(viaAgent, UninstallRouting.UsesAgent(hive));
}
