using UpdateHelper.Core.Security;

namespace UpdateHelper.Core.Tests;

public sealed class SecurityJudgeTests
{
    private static RegisteredAntivirus Av(string name) => new(name, 0, null);
    private static DefenderStatus Def(bool rtp) => new("Normal", rtp, rtp, "1.400", new DateTimeOffset(2026, 10, 9, 0, 0, 0, TimeSpan.Zero));

    [Fact]
    public void Third_party_av_shown_as_protected()
    {
        var s = SecurityJudge.Evaluate([Av("火绒安全软件")], null);
        Assert.Equal(AntivirusState.Protected, s.State);
        Assert.Equal("火绒安全软件", s.ProviderName);
        Assert.Contains("由 火绒安全软件 保护", s.Headline);
    }

    [Fact]
    public void Third_party_wins_over_defender()
    {
        // 同时注册了第三方和 Defender，且 Defender 没在跑：以第三方为准
        var s = SecurityJudge.Evaluate([Av("Windows Defender"), Av("卡巴斯基")], Def(false));
        Assert.Equal(AntivirusState.Protected, s.State);
        Assert.Equal("卡巴斯基", s.ProviderName);
    }

    [Fact]
    public void Defender_running_is_protected()
    {
        var s = SecurityJudge.Evaluate([Av("Windows Defender")], Def(true));
        Assert.Equal(AntivirusState.Protected, s.State);
        Assert.Equal("Windows Defender", s.ProviderName);
        Assert.Contains("2026-10-09", s.Detail);
    }

    [Fact]
    public void Defender_registered_but_off_is_warned()
    {
        var s = SecurityJudge.Evaluate([Av("Windows Defender")], Def(false));
        Assert.Equal(AntivirusState.RealTimeOff, s.State);
        Assert.Contains("实时保护未开启", s.Headline);
        Assert.Contains("安全中心", s.Detail);
    }

    [Fact]
    public void Null_defender_is_unknown()
    {
        var s = SecurityJudge.Evaluate([Av("Windows Defender")], null);
        Assert.Equal(AntivirusState.Unknown, s.State);
        Assert.Contains("无法读取", s.Headline);
    }

    [Fact]
    public void No_av_detected()
    {
        var s = SecurityJudge.Evaluate([], null);
        Assert.Equal(AntivirusState.NotDetected, s.State);
        Assert.Contains("没有检测到", s.Headline);
    }

    [Fact]
    public void Defender_name_match_is_case_insensitive()
    {
        var s = SecurityJudge.Evaluate([Av("windows defender")], Def(true));
        Assert.Equal(AntivirusState.Protected, s.State);
        Assert.Equal("Windows Defender", s.ProviderName);
    }
}
