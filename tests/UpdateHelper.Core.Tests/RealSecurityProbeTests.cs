using UpdateHelper.Core.Security;

namespace UpdateHelper.Core.Tests;

public sealed class RealSecurityProbeTests
{
    // 这些是集成测试：读本机真实 WMI。断言的是"不抛异常、返回合理结构"，不假设具体杀软。
    [Fact]
    public void Query_registered_does_not_throw()
    {
        var probe = new RealSecurityProbe();
        var list = probe.QueryRegistered();   // 可能为空（看机器），但不能抛
        Assert.NotNull(list);
        Assert.All(list, a => Assert.False(string.IsNullOrWhiteSpace(a.DisplayName)));
    }

    [Fact]
    public void Query_defender_does_not_throw()
    {
        var probe = new RealSecurityProbe();
        var d = probe.QueryDefender();   // 可能为 null（没有 Defender 命名空间），但不能抛
        if (d is not null)
            Assert.False(string.IsNullOrWhiteSpace(d.AmRunningMode));
    }

    [Fact]
    public void Evaluate_on_real_machine_produces_a_status()
    {
        var probe = new RealSecurityProbe();
        var status = SecurityJudge.Evaluate(probe.QueryRegistered(), probe.QueryDefender());
        Assert.False(string.IsNullOrWhiteSpace(status.Headline));
    }
}
