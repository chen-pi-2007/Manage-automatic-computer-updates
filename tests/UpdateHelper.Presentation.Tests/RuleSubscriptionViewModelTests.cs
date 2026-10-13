using UpdateHelper.Core.Rules.Subscription;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

public sealed class RuleSubscriptionViewModelTests
{
    private static RuleSubscriptionViewModel Vm(RuleSubscriptionInfo info, SyncOutcome? sync = null)
    {
        var backend = new FakeBackend
        {
            OnGetRuleSubscriptionInfo = () => info,
            OnSyncRules = () => sync ?? new SyncOutcome(SyncStatus.AlreadyLatest, info.CurrentVersion, DateTimeOffset.Now, null),
        };
        return new RuleSubscriptionViewModel(backend);
    }

    [Fact]
    public async Task Shows_version_and_updated_state()
    {
        var vm = Vm(new RuleSubscriptionInfo("https://x/rules", 5, 12,
            new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero), SyncStatus.Updated));
        await vm.RefreshAsync();
        Assert.Contains("5", vm.VersionText);
        Assert.Contains("12", vm.VersionText);
        Assert.Contains("最新", vm.StateText);
    }

    [Fact]
    public async Task Network_failure_state_is_shown_not_thrown()
    {
        var vm = Vm(new RuleSubscriptionInfo("https://x/rules", null, 12, null, null),
            new SyncOutcome(SyncStatus.NetworkFailed, null, DateTimeOffset.Now, "断网"));
        await vm.SyncAsync();
        Assert.Contains("失败", vm.StateText);
    }

    [Fact]
    public async Task No_sync_yet_shows_placeholder()
    {
        var vm = Vm(new RuleSubscriptionInfo("https://x/rules", 1, 12, null, null));
        await vm.RefreshAsync();
        Assert.Contains("尚未", vm.LastSyncText);
    }

    [Fact]
    public async Task Cache_write_failure_state_is_friendly()
    {
        var vm = Vm(new RuleSubscriptionInfo("https://x/rules", 1, 12, DateTimeOffset.Now, SyncStatus.CacheWriteFailed));
        await vm.RefreshAsync();
        Assert.Contains("失败", vm.StateText);
        Assert.Contains("无法写入", vm.StateText);
    }
}
