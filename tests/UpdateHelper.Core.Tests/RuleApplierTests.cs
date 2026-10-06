using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class RuleApplierTests
{
    private static Rule MakeRule(string id, string displayName, string? publisher = null,
        SoftwareCategory? category = null, params BackgroundRule[] background)
        => new(id, id, new RuleMatch(displayName, publisher), category, null, background, []);

    private static RuleSet Rules(params Rule[] rules) => new(rules, [], []);

    private static BackgroundItem Item(BackgroundKind kind, string name, string? exe = null)
        => new(kind, name, null, exe, exe, null);

    private static SoftwareGroup Find(RuleReport r, string name) => r.Result.Groups.Single(g => g.Name == name);

    [Fact]
    public void Matching_rule_sets_rule_id_and_overrides_category()
    {
        var scan = SoftwareGrouper.Group([Entry("Oopz", "绍兴未来山海科技有限公司")], []);
        var report = RuleApplier.Apply(scan, Rules(MakeRule("oopz", "Oopz", category: SoftwareCategory.Game)));

        var g = Find(report, "Oopz");
        Assert.Equal("oopz", g.RuleId);
        Assert.Equal(SoftwareCategory.Game, g.Category);
    }

    [Fact]
    public void Rule_without_category_keeps_builtin_category()
    {
        var scan = SoftwareGrouper.Group([Entry("Git", "The Git Development Community")], []);
        var report = RuleApplier.Apply(scan, Rules(MakeRule("git", "Git")));
        Assert.Equal(SoftwareCategory.DevTool, Find(report, "Git").Category);
    }

    [Fact]
    public void Publisher_must_match_when_given()
    {
        var scan = SoftwareGrouper.Group([Entry("WPS Office", "Someone Else")], []);
        var report = RuleApplier.Apply(scan, Rules(MakeRule("kingsoft.wps", "WPS Office*", "Kingsoft*")));
        Assert.Null(Find(report, "WPS Office").RuleId);
    }

    [Fact]
    public void First_matching_rule_wins()
    {
        var scan = SoftwareGrouper.Group([Entry("QQ", "Tencent")], []);
        var report = RuleApplier.Apply(scan, Rules(MakeRule("first", "Q*"), MakeRule("second", "QQ")));
        Assert.Equal("first", Find(report, "QQ").RuleId);
    }

    [Fact]
    public void Component_collections_are_never_matched()
    {
        var scan = SoftwareGrouper.Group([Entry("CUBLAS Runtime", "NVIDIA Corporation", hidden: true)], []);
        var report = RuleApplier.Apply(scan, Rules(MakeRule("all", "*")));
        Assert.Null(Assert.Single(report.Result.Groups).RuleId);
    }

    [Fact]
    public void Background_in_group_gets_explanation()
    {
        var wpsTask = Item(BackgroundKind.ScheduledTask, "WpsUpdateTask_chen_pi", @"D:\WPS Office\ksolaunch.exe");
        var scan = SoftwareGrouper.Group([Entry("WPS Office", "Kingsoft Corp.", location: @"D:\WPS Office")], [wpsTask]);

        var report = RuleApplier.Apply(scan, Rules(MakeRule("kingsoft.wps", "WPS Office*", background:
            new BackgroundRule(BackgroundKind.ScheduledTask, "WpsUpdate*", "WPS 的自动更新任务"))));

        Assert.Equal("WPS 的自动更新任务", report.Explanations[wpsTask]);
    }

    [Fact]
    public void Unassigned_item_is_claimed_by_rule_group()
    {
        var gupdate = Item(BackgroundKind.Service, "gupdate", @"C:\Program Files (x86)\Google\Update\GoogleUpdate.exe");
        var scan = SoftwareGrouper.Group(
            [Entry("Google Chrome", "Google LLC", location: @"C:\Program Files\Google\Chrome\Application")], [gupdate]);
        Assert.Single(scan.UnassignedBackground);   // 计划 1 按位置挂不上

        var report = RuleApplier.Apply(scan, Rules(MakeRule("google.chrome", "Google Chrome", background:
            new BackgroundRule(BackgroundKind.Service, "gupdate*", "Google 的自动更新服务"))));

        Assert.Empty(report.Result.UnassignedBackground);
        Assert.Contains(gupdate, Find(report, "Google Chrome").Background);
        Assert.Equal("Google 的自动更新服务", report.Explanations[gupdate]);
    }

    [Fact]
    public void Item_explained_by_rule_without_installed_software_stays_unassigned()
    {
        var jusched = Item(BackgroundKind.RunKey, "SunJavaUpdateSched");
        var scan = SoftwareGrouper.Group([], [jusched]);

        var report = RuleApplier.Apply(scan, Rules(MakeRule("oracle.java", "Java * Update *", background:
            new BackgroundRule(BackgroundKind.RunKey, "SunJavaUpdateSched", "Java 的更新检查程序，开机启动"))));

        Assert.Contains(jusched, report.Result.UnassignedBackground);
        Assert.Equal("Java 的更新检查程序，开机启动", report.Explanations[jusched]);
    }

    [Fact]
    public void Kind_must_match()
    {
        var svc = Item(BackgroundKind.Service, "WpsUpdateTask");
        var scan = SoftwareGrouper.Group([], [svc]);
        var report = RuleApplier.Apply(scan, Rules(MakeRule("w", "W", background:
            new BackgroundRule(BackgroundKind.ScheduledTask, "WpsUpdate*", "任务"))));
        Assert.False(report.Explanations.ContainsKey(svc));
    }

    [Fact]
    public void Explanation_from_own_groups_rule_is_preferred()
    {
        var task = Item(BackgroundKind.ScheduledTask, "Updater", @"C:\B\up.exe");
        var scan = SoftwareGrouper.Group([Entry("B", "BCo", location: @"C:\B")], [task]);

        var report = RuleApplier.Apply(scan, Rules(
            MakeRule("a", "A", background: new BackgroundRule(BackgroundKind.ScheduledTask, "Updater", "A 的说明")),
            MakeRule("b", "B", background: new BackgroundRule(BackgroundKind.ScheduledTask, "Updater", "B 的说明"))));

        Assert.Equal("B 的说明", report.Explanations[task]);
    }

    [Fact]
    public void Groups_are_resorted_after_category_change()
    {
        var scan = SoftwareGrouper.Group([Entry("Aaa", "X"), Entry("Zzz", "Y")], []);
        Assert.Equal(new[] { "Aaa", "Zzz" }, scan.Groups.Select(g => g.Name));   // 都是普通软件，按名称排

        var report = RuleApplier.Apply(scan, Rules(MakeRule("aaa", "Aaa", category: SoftwareCategory.Game)));

        Assert.Equal(new[] { "Zzz", "Aaa" }, report.Result.Groups.Select(g => g.Name));   // Aaa 成了游戏，排到后面
    }

    [Fact]
    public void Empty_rule_set_changes_nothing()
    {
        var item = Item(BackgroundKind.Service, "s");
        var scan = SoftwareGrouper.Group([Entry("A", "X")], [item]);
        var report = RuleApplier.Apply(scan, RuleSet.Empty);
        Assert.Empty(report.Explanations);
        Assert.Single(report.Result.UnassignedBackground);
        Assert.Null(report.Result.Groups[0].RuleId);
    }
}
