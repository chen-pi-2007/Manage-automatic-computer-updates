using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation.Tests;

public sealed class CheckScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(8));
    private static readonly AppSettings Default = new();   // 只提醒、24 小时

    [Fact]
    public void Does_not_check_while_busy()
    {
        Assert.False(CheckSchedule.ShouldCheck(T0.AddDays(3), T0, false, T0, Default, busy: true));
    }

    [Fact]
    public void Open_only_mode_never_checks_in_background()
    {
        var s = Default with { Mode = UpdateMode.OnOpenOnly };
        Assert.False(CheckSchedule.ShouldCheck(T0.AddDays(3), T0, false, T0, s, busy: false));
        Assert.False(CheckSchedule.ShouldCheck(T0.AddMinutes(10), T0, true, null, s, busy: false));
    }

    [Fact]
    public void Login_start_waits_five_minutes_before_first_check()
    {
        Assert.False(CheckSchedule.ShouldCheck(T0.AddMinutes(4), T0, startedAtLogin: true, null, Default, false));
        Assert.True(CheckSchedule.ShouldCheck(T0.AddMinutes(5), T0, startedAtLogin: true, null, Default, false));
    }

    [Fact]
    public void Manual_start_checks_immediately_when_never_checked()
    {
        Assert.True(CheckSchedule.ShouldCheck(T0, T0, startedAtLogin: false, null, Default, false));
    }

    [Fact]
    public void Checks_again_after_interval()
    {
        Assert.False(CheckSchedule.ShouldCheck(T0.AddHours(23).AddMinutes(59), T0, false, T0, Default, false));
        Assert.True(CheckSchedule.ShouldCheck(T0.AddHours(24), T0, false, T0, Default, false));
    }

    [Fact]
    public void Uses_interval_from_settings()
    {
        var s = Default with { CheckIntervalHours = 6 };
        Assert.True(CheckSchedule.ShouldCheck(T0.AddHours(6), T0, false, T0, s, false));
    }

    [Fact]
    public void Long_gap_triggers_one_check()
    {
        // 睡眠三天后唤醒：到点就检查一次；检查完 lastChecked 更新，下一分钟不会再检查
        var wake = T0.AddDays(3);
        Assert.True(CheckSchedule.ShouldCheck(wake, T0, false, T0, Default, false));
        Assert.False(CheckSchedule.ShouldCheck(wake.AddMinutes(1), T0, false, wake, Default, false));
    }
}
