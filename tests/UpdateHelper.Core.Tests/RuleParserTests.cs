using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Tests;

public class RuleParserTests
{
    private const string Full = """
        id: kingsoft.wps
        name: WPS Office
        category: application
        match:
          displayName: "WPS Office*"
          publisher: "Kingsoft Corp."
        update:
          check:
            url: https://example.com/wps/version
            extract: '"version":"([\d.]+)"'
          latest:
            version: "12.1.0.24000"
            url: https://example.com/wps/setup.exe
          installer:
            signer: "Zhuhai Kingsoft Office Software Co., Ltd"
            silentArgs: "/S"
          risk: careful
        background:
          - task: "WpsUpdate*"
            explain: "WPS 的自动更新任务，登录时和每天运行"
          - service: "wpscloudsvr"
            explain: "WPS 云服务"
        leftovers:
          - path: '%APPDATA%\kingsoft'
            kind: owned
          - path: '%USERPROFILE%\Documents\WPS Cloud Files'
            kind: personal
        """;

    [Fact]
    public void Full_rule_is_parsed()
    {
        var r = RuleParser.Parse(Full, "wps.yaml");

        Assert.Empty(r.Errors);
        var rule = r.Rule!;
        Assert.Equal("kingsoft.wps", rule.Id);
        Assert.Equal("WPS Office", rule.Name);
        Assert.Equal("wps.yaml", rule.SourceFile);
        Assert.Equal(SoftwareCategory.Application, rule.Category);
        Assert.Equal(new RuleMatch("WPS Office*", "Kingsoft Corp."), rule.Match);
        Assert.Equal(RiskLevel.Careful, rule.Update!.Risk);
        Assert.Equal("Zhuhai Kingsoft Office Software Co., Ltd", rule.Update.Signer);
        Assert.Equal("/S", rule.Update.SilentArgs);
        Assert.Equal(@"""version"":""([\d.]+)""", rule.Update.Check!.Extract);
        Assert.Equal("12.1.0.24000", rule.Update.Latest!.Version);
        Assert.Equal(
            new[] { new BackgroundRule(BackgroundKind.ScheduledTask, "WpsUpdate*", "WPS 的自动更新任务，登录时和每天运行"),
                    new BackgroundRule(BackgroundKind.Service, "wpscloudsvr", "WPS 云服务") },
            rule.Background);
        Assert.Equal(
            new[] { new LeftoverRule(@"%APPDATA%\kingsoft", LeftoverKind.Owned),
                    new LeftoverRule(@"%USERPROFILE%\Documents\WPS Cloud Files", LeftoverKind.Personal) },
            rule.Leftovers);
    }

    [Fact]
    public void Minimal_rule_has_defaults()
    {
        var r = RuleParser.Parse("""
            id: tencent.qq
            name: QQ
            match:
              displayName: QQ
            """, "qq.yaml");

        Assert.True(r.Ok);
        Assert.Null(r.Rule!.Category);
        Assert.Null(r.Rule.Update);
        Assert.Null(r.Rule.Match.Publisher);
        Assert.Empty(r.Rule.Background);
        Assert.Empty(r.Rule.Leftovers);
    }

    [Theory]
    [InlineData("run: \"Weixin\"", BackgroundKind.RunKey)]
    [InlineData("startup: \"微信*\"", BackgroundKind.StartupFolder)]
    public void Background_targets_map_to_kinds(string target, BackgroundKind kind)
    {
        var r = RuleParser.Parse($"""
            id: a
            name: A
            match:
              displayName: A
            background:
              - {target}
                explain: 说明
            """, "a.yaml");
        Assert.Equal(kind, Assert.Single(r.Rule!.Background).Kind);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var r = RuleParser.Parse("""
            id: a
            name: A
            futureField: 123
            match:
              displayName: A
              somethingNew: x
            """, "a.yaml");
        Assert.True(r.Ok);
    }

    // —— 以下都应失败，并且错误信息以文件名开头 ——

    public static TheoryData<string, string> Invalid => new()
    {
        { "name: A\nmatch: { displayName: A }", "id" },                                   // 缺 id
        { "id: Bad Id\nname: A\nmatch: { displayName: A }", "id" },                        // id 格式不对
        { "id: a\nmatch: { displayName: A }", "name" },                                    // 缺 name
        { "id: a\nname: A", "match.displayName" },                                         // 缺 match
        { "id: a\nname: A\ncategory: toy\nmatch: { displayName: A }", "category" },
        { "id: a\nname: A\nmatch: { displayName: A }\nupdate: { risk: maybe }", "risk" },
        { "id: a\nname: A\nmatch: { displayName: A }\nupdate: { check: { url: ftp://x } }", "update.check" },
        { "id: a\nname: A\nmatch: { displayName: A }\nupdate: { latest: { version: '1' } }", "update.latest" },
        { "id: a\nname: A\nmatch: { displayName: A }\nbackground: [ { explain: x } ]", "background[0]" },
        { "id: a\nname: A\nmatch: { displayName: A }\nbackground: [ { task: a, service: b, explain: x } ]", "background[0]" },
        { "id: a\nname: A\nmatch: { displayName: A }\nbackground: [ { task: a } ]", "background[0]" },
        { "id: a\nname: A\nmatch: { displayName: A }\nleftovers: [ { path: 'C:\\x', kind: mine } ]", "leftovers[0]" },
        { "id: a\nname: A\nmatch: { displayName: A }\nleftovers: [ { kind: owned } ]", "leftovers[0]" },
        // Review Focus 1：语法错误、类型不对
        { "id: a\nname: [unclosed", "YAML" },
        { "id: a\nname: A\nmatch: { displayName: A }\nbackground: just-a-string", "YAML" },
        { "- 1\n- 2", "YAML" },
        { "", "空" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void Invalid_rules_report_errors(string yaml, string mentions)
    {
        var r = RuleParser.Parse(yaml, "bad.yaml");

        Assert.False(r.Ok);
        Assert.NotEmpty(r.Errors);
        Assert.All(r.Errors, e => Assert.StartsWith("bad.yaml: ", e));
        Assert.Contains(r.Errors, e => e.Contains(mentions, StringComparison.Ordinal));
    }

    [Fact]
    public void Several_problems_are_all_reported()
    {
        var r = RuleParser.Parse("category: toy", "x.yaml");
        Assert.True(r.Errors.Count >= 3);   // id、name、match.displayName、category 都有问题
    }
}
