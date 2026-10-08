using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public sealed class AgentProtocolTests
{
    [Fact]
    public void Request_round_trips_through_one_json_line()
    {
        var request = new AgentRequest(AgentProtocol.Version, AgentOp.Upgrade, "Tencent.QQ", "9.9.21", InstallScopeHint.User);
        var line = AgentProtocol.Encode(request);

        Assert.DoesNotContain('\n', line);
        Assert.Equal(request, AgentProtocol.Decode<AgentRequest>(line));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"Op\":\"FormatDisk\"}")]
    [InlineData("[1,2,3]")]
    public void Decode_returns_null_for_garbage(string line)
    {
        Assert.Null(AgentProtocol.Decode<AgentRequest>(line));
    }

    [Fact]
    public void Result_converts_both_ways()
    {
        var report = new InstallerReport(false, false, "安装程序运行出错", 0x80070005);
        Assert.Equal(report, AgentProtocol.ToReport(AgentProtocol.ToResult(report)));
    }

    [Theory]
    [InlineData("Tencent.QQ", "9.9.21")]
    [InlineData("Microsoft.VisualStudioCode", "1.105.1")]
    [InlineData("7zip.7zip", "25.01")]
    [InlineData("Python.Python.3.10", "3.10.11")]
    [InlineData("Valve.Steam", "2.10.91.91-beta_1")]
    public void Validator_accepts_normal_winget_upgrades(string id, string version)
    {
        Assert.Null(AgentRequestValidator.Validate(new AgentRequest(AgentProtocol.Version, AgentOp.Upgrade, id, version)));
    }

    [Theory]
    [InlineData("rule:wps", "12.1", "包 id 格式不对")]                 // 规则库的包不经过后台助手
    [InlineData("Tencent.QQ & calc", "1.0", "包 id 格式不对")]
    [InlineData("QQ", "1.0", "包 id 格式不对")]                       // winget id 一定是 发布者.名称
    [InlineData("", "1.0", "包 id 格式不对")]
    [InlineData(null, "1.0", "包 id 格式不对")]
    [InlineData("Tencent.QQ", "", "版本号格式不对")]
    [InlineData("Tencent.QQ", "< 3.10.8", "版本号格式不对")]         // 模糊版本不能作为安装目标
    [InlineData("Tencent.QQ", "1.0\" --override \"x", "版本号格式不对")]
    public void Validator_rejects_bad_upgrades(string? id, string version, string expected)
    {
        var error = AgentRequestValidator.Validate(new AgentRequest(AgentProtocol.Version, AgentOp.Upgrade, id, version));
        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void Validator_rejects_other_protocol_versions_and_overlong_values()
    {
        Assert.Contains("版本不一致", AgentRequestValidator.Validate(new AgentRequest(99, AgentOp.Ping)));
        Assert.Contains("包 id 格式不对", AgentRequestValidator.Validate(
            new AgentRequest(AgentProtocol.Version, AgentOp.Upgrade, "A." + new string('b', 200), "1.0")));
        Assert.Contains("安装范围", AgentRequestValidator.Validate(
            new AgentRequest(AgentProtocol.Version, AgentOp.Upgrade, "Tencent.QQ", "1.0", (InstallScopeHint)7)));
    }

    [Fact]
    public void Ping_needs_no_package()
    {
        Assert.Null(AgentRequestValidator.Validate(new AgentRequest(AgentProtocol.Version, AgentOp.Ping)));
    }
}
