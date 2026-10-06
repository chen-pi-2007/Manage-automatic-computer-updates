using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Updates;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class UpdateJudgeTests
{
    private static UpdateCandidate C(string key, string installed, string available, string? name = null)
        => new($"Pkg.{key}", name ?? key, null, installed, available, [key.ToLowerInvariant()]);

    private static JudgedUpdate JudgeOne(UninstallEntry entry, UpdateCandidate candidate, RuleSet? rules = null)
    {
        var groups = SoftwareGrouper.Group([entry], []).Groups;
        if (rules is not null) RuleApplier.Apply(new ScanResult(groups, [], 1), rules);
        return Assert.Single(UpdateJudge.Judge([candidate], groups, rules ?? RuleSet.Empty));
    }

    [Fact]
    public void Patch_update_of_application_is_low_risk()
    {
        var j = JudgeOne(Entry("QQ", "腾讯科技(深圳)有限公司", "9.9.20.36330", key: "QQ"), C("QQ", "9.9.20.36330", "9.9.33.52230"));
        Assert.Equal(UpdateTier.Low, j.Tier);
        Assert.Equal("QQ", j.Group!.Name);
        Assert.Equal("小版本更新", j.Reason);
    }

    [Fact]
    public void Application_minor_jump_below_ten_is_low_risk()   // 百度网盘 8.2 → 8.8
        => Assert.Equal(UpdateTier.Low, JudgeOne(Entry("百度网盘", "北京度友科技有限公司", key: "BaiduNetdisk"),
            C("BaiduNetdisk", "8.2.7", "8.8.8")).Tier);

    [Fact]
    public void Application_minor_jump_of_ten_or_more_needs_confirmation()
    {
        var j = JudgeOne(Entry("Foo", "Foo Inc", key: "Foo"), C("Foo", "4.46.0", "4.93.0"));
        Assert.Equal(UpdateTier.Careful, j.Tier);
        Assert.Contains("4.46 → 4.93", j.Reason);
    }

    [Fact]
    public void Major_change_needs_confirmation()   // Anaconda 2022 → 2026
    {
        var j = JudgeOne(Entry("Anaconda3 2022.10 (Python 3.9.13 64-bit)", "Anaconda, Inc.", key: "Anaconda3"),
            C("Anaconda3", "2022.10", "2026.07-1"));
        Assert.Equal(UpdateTier.Careful, j.Tier);
        Assert.Contains("2022 → 2026", j.Reason);
    }

    [Fact]
    public void Devtool_minor_change_needs_confirmation()   // VirtualBox 7.0 → 7.2
    {
        var j = JudgeOne(Entry("Oracle VM VirtualBox 7.0.12", "Oracle and/or its affiliates", key: "VBox"),
            C("VBox", "7.0.12", "7.2.20"));
        Assert.Equal(UpdateTier.Careful, j.Tier);
        Assert.Contains("7.0 → 7.2", j.Reason);
    }

    [Fact]
    public void Devtool_patch_is_low_risk()   // Python 3.10.2 → 3.10.11
        => Assert.Equal(UpdateTier.Low, JudgeOne(Entry("Python 3.10.2 (64-bit)", "Python Software Foundation", key: "Py"),
            C("Py", "3.10.2", "3.10.11")).Tier);

    [Fact]
    public void Driver_needs_confirmation()
        => Assert.Equal(UpdateTier.Careful, JudgeOne(Entry("NVIDIA 图形驱动程序 610.62", "NVIDIA Corporation", key: "Drv"),
            C("Drv", "610.62", "610.80")).Tier);

    [Fact]
    public void Game_is_ignored()
        => Assert.Equal(UpdateTier.Ignored, JudgeOne(Entry("Counter-Strike 2", "Valve", key: "Steam App 730"),
            C("Steam App 730", "1.0", "1.1")).Tier);

    [Fact]
    public void Runtime_is_never_auto()
        => Assert.Equal(UpdateTier.NeverAuto, JudgeOne(
            Entry("Microsoft Visual C++ v14 Redistributable (x64) - 14.50.35719", "Microsoft Corporation", key: "VC"),
            C("VC", "14.50.35719.0", "14.51.36247.0")).Tier);

    [Fact]
    public void Approximate_installed_version_is_never_auto()   // Review Focus 1
    {
        var j = JudgeOne(Entry("Python Launcher", "Python Software Foundation", key: "PyLauncher"),
            C("PyLauncher", "< 3.10.8", "3.14.7"));
        Assert.Equal(UpdateTier.NeverAuto, j.Tier);
        Assert.Contains("< 3.10.8", j.Reason);
    }

    [Fact]
    public void Unparseable_version_is_never_auto()   // Review Focus 1
        => Assert.Equal(UpdateTier.NeverAuto, JudgeOne(Entry("Foo", "Foo Inc", key: "Foo"), C("Foo", "unknown", "2.0")).Tier);

    [Fact]
    public void Unmatched_candidate_is_never_auto()   // Review Focus 5
    {
        var j = JudgeOne(Entry("Foo", "Foo Inc", key: "Foo"), C("Other", "1.0", "1.1"));
        Assert.Equal(UpdateTier.NeverAuto, j.Tier);
        Assert.Null(j.Group);
        Assert.Contains("没有在扫描结果里找到", j.Reason);
    }

    [Fact]
    public void Not_actually_newer_is_dropped()   // Review Focus 4
    {
        var groups = SoftwareGrouper.Group([Entry("Foo", "Foo Inc", key: "Foo")], []).Groups;
        Assert.Empty(UpdateJudge.Judge([C("Foo", "2.0", "1.9")], groups, RuleSet.Empty));
        Assert.Empty(UpdateJudge.Judge([C("Foo", "2.0", "2.0.0")], groups, RuleSet.Empty));
    }

    [Fact]
    public void Same_name_registered_twice_is_never_auto()   // Review Focus 2：微信 3.9 + 4.1
    {
        var groups = SoftwareGrouper.Group([
            Entry("微信", "腾讯科技(深圳)有限公司", "3.9.12.51", key: "WeChat"),
            Entry("微信", "腾讯科技(深圳)有限公司", "4.1.15.13", key: "Weixin")], []).Groups;

        var results = UpdateJudge.Judge([C("WeChat", "3.9.12.51", "3.9.12.57", "微信"), C("Weixin", "4.1.15.13", "4.1.16.2", "微信")],
            groups, RuleSet.Empty);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.Equal(UpdateTier.NeverAuto, r.Tier));
        Assert.All(results, r => Assert.Contains("3.9.12.51、4.1.15.13", r.Reason));
    }

    private static RuleSet RuleWithRisk(string displayName, RiskLevel risk) => new(
        [new Rule("r", "r", new RuleMatch(displayName, null), null, new UpdateRule(null, null, null, null, risk), [], [])],
        [], []);

    [Theory]
    [InlineData(RiskLevel.Careful, UpdateTier.Careful)]
    [InlineData(RiskLevel.NeverAuto, UpdateTier.NeverAuto)]
    public void Rule_risk_overrides_builtin_judgement(RiskLevel risk, UpdateTier expected)
    {
        var j = JudgeOne(Entry("QQ", "Tencent", key: "QQ"), C("QQ", "9.9.20", "9.9.33"), RuleWithRisk("QQ", risk));
        Assert.Equal(expected, j.Tier);
        Assert.Contains("规则", j.Reason);
    }

    [Fact]
    public void Results_are_ordered_by_tier_then_name()
    {
        var groups = SoftwareGrouper.Group([
            Entry("Zeta", "Z", key: "Z"),
            Entry("Alpha", "A", key: "A"),
            Entry("Anaconda3", "Anaconda, Inc.", key: "Ana")], []).Groups;

        var results = UpdateJudge.Judge([C("Ana", "2022.10", "2026.07"), C("Z", "1.0", "1.1"), C("A", "1.0", "1.1")],
            groups, RuleSet.Empty);

        Assert.Equal(new[] { "Alpha", "Zeta", "Anaconda3" }, results.Select(r => r.Group!.Name));
    }

    private sealed class FakeSource(Func<IReadOnlyList<UpdateCandidate>> get) : IUpdateSource
    {
        public string Name => "fake";
        public IReadOnlyList<UpdateCandidate> GetAvailableUpdates() => get();
    }

    [Fact]
    public void Service_returns_judged_updates()
    {
        var scan = SoftwareGrouper.Group([Entry("QQ", "Tencent", key: "QQ")], []);
        var report = UpdateService.Check(new FakeSource(() => [C("QQ", "1.0", "1.1")]), scan, RuleSet.Empty);
        Assert.Null(report.Warning);
        Assert.Single(report.Updates);
    }

    [Fact]
    public void Service_turns_source_failure_into_warning()   // Review Focus 3
    {
        var scan = SoftwareGrouper.Group([], []);
        var report = UpdateService.Check(
            new FakeSource(() => throw new InvalidOperationException("没有找到 winget")), scan, RuleSet.Empty);

        Assert.Empty(report.Updates);
        Assert.Contains("没有找到 winget", report.Warning);
        Assert.Contains("fake", report.Warning);
    }
}
