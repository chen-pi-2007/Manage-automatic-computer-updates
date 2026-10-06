using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Updates;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class RuleUpdateSourceTests
{
    private static Rule WpsRule(string? latest = "12.1.0.24000") => new(
        "kingsoft.wps", "WPS Office", new RuleMatch("WPS Office*", null), null,
        latest is null ? null : new UpdateRule(null, new UpdateLatest(latest, "https://example.com/wps.exe"),
            "Zhuhai Kingsoft Office Software Co., Ltd", "/S", RiskLevel.Normal),
        [], []);

    private static (ScanResult Scan, RuleSet Rules) Setup(Rule rule)
    {
        var scan = SoftwareGrouper.Group(
            [Entry("WPS Office (12.1.0.23125)", "Kingsoft Corp.", "12.1.0.23125", key: "Kingsoft Office")], []);
        var rules = new RuleSet([rule], [], []);
        RuleApplier.Apply(scan, rules);
        return (scan, rules);
    }

    [Fact]
    public void Rule_with_latest_produces_candidate()
    {
        var (scan, rules) = Setup(WpsRule());
        var c = Assert.Single(new RuleUpdateSource(scan, rules).GetAvailableUpdates());

        Assert.Equal("rule:kingsoft.wps", c.PackageId);
        Assert.Equal("WPS Office (12.1.0.23125)", c.Name);
        Assert.Equal("12.1.0.23125", c.InstalledVersion);
        Assert.Equal("12.1.0.24000", c.AvailableVersion);
        Assert.Equal(new[] { "Kingsoft Office" }, c.ProductCodes);
    }

    [Fact]
    public void Rule_without_update_section_produces_nothing()
    {
        var (scan, rules) = Setup(WpsRule(latest: null));
        Assert.Empty(new RuleUpdateSource(scan, rules).GetAvailableUpdates());
    }

    [Fact]
    public void Same_version_produces_nothing()
    {
        var (scan, rules) = Setup(WpsRule("12.1.0.23125"));
        Assert.Empty(new RuleUpdateSource(scan, rules).GetAvailableUpdates());
    }

    private sealed class FixedSource(string name, params UpdateCandidate[] candidates) : IUpdateSource
    {
        public string Name => name;
        public IReadOnlyList<UpdateCandidate> GetAvailableUpdates() => candidates;
    }

    private sealed class BrokenSource(string name) : IUpdateSource
    {
        public string Name => name;
        public IReadOnlyList<UpdateCandidate> GetAvailableUpdates() => throw new InvalidOperationException("连不上");
    }

    private static UpdateCandidate C(string id, string code) => new(id, "QQ", null, "1.0", "1.1", [code]);

    [Fact]
    public void Earlier_source_wins_when_both_report_same_software()   // Review Focus 5
    {
        var scan = SoftwareGrouper.Group([Entry("QQ", "Tencent", "1.0", key: "QQ")], []);
        var report = UpdateService.Check(
            [new FixedSource("winget", C("Tencent.QQ.NT", "qq")), new FixedSource("规则库", C("rule:tencent.qq", "QQ"))],
            scan, RuleSet.Empty);

        Assert.Equal("Tencent.QQ.NT", Assert.Single(report.Updates).Candidate.PackageId);
    }

    [Fact]
    public void One_broken_source_does_not_hide_the_others()
    {
        var scan = SoftwareGrouper.Group([Entry("QQ", "Tencent", "1.0", key: "QQ")], []);
        var report = UpdateService.Check(
            [new BrokenSource("winget"), new FixedSource("规则库", C("rule:tencent.qq", "QQ"))], scan, RuleSet.Empty);

        Assert.Equal("rule:tencent.qq", Assert.Single(report.Updates).Candidate.PackageId);
        Assert.Equal("无法从 winget 获取更新信息：连不上", report.Warning);
    }

    [Fact]
    public void Warnings_from_several_sources_are_joined()
    {
        var scan = SoftwareGrouper.Group([], []);
        var report = UpdateService.Check([new BrokenSource("a"), new BrokenSource("b")], scan, RuleSet.Empty);
        Assert.Equal("无法从 a 获取更新信息：连不上；无法从 b 获取更新信息：连不上", report.Warning);
    }
}
