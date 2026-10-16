using UpdateHelper.Core.Rules.Subscription;

namespace UpdateHelper.Core.Tests.Subscription;

public sealed class RuleSyncScheduleTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact] public void Never_synced_should_sync() => Assert.True(RuleSyncSchedule.ShouldSync(Now, null));

    [Fact] public void Within_a_day_should_not() =>
        Assert.False(RuleSyncSchedule.ShouldSync(Now, Now.AddHours(-5)));

    [Fact] public void Exactly_a_day_should() =>
        Assert.True(RuleSyncSchedule.ShouldSync(Now, Now.AddHours(-24)));

    [Fact] public void Over_a_day_should() =>
        Assert.True(RuleSyncSchedule.ShouldSync(Now, Now.AddHours(-25)));
}
