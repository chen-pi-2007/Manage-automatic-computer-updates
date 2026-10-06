using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Tests;

public class RuleSetLoaderTests
{
    private static string RuleYaml(string id, string name = "X") => $"""
        id: {id}
        name: {name}
        match:
          displayName: "{name}"
        """;

    [Fact]
    public void Loads_yaml_and_yml_recursively_in_path_order()
    {
        using var dir = new TempDir();
        dir.Write("b.yaml", RuleYaml("b"));
        dir.Write("a.yml", RuleYaml("a"));
        dir.Write(@"tencent\qq.yaml", RuleYaml("tencent.qq"));
        dir.Write("readme.txt", "不是规则");

        var set = RuleSetLoader.Load([new RuleSource(dir.Path, RuleTrust.Official)]);

        Assert.Equal(new[] { "a", "b", "tencent.qq" }, set.Rules.Select(r => r.Id));
        Assert.All(set.Rules, r => Assert.Equal(RuleTrust.Official, r.Trust));
        Assert.Empty(set.Errors);
        Assert.Empty(set.Warnings);
    }

    [Fact]
    public void Missing_or_empty_directories_give_no_rules_and_no_errors()   // Review Focus 4
    {
        using var empty = new TempDir();
        var set = RuleSetLoader.Load([
            new RuleSource(System.IO.Path.Combine(empty.Path, "不存在"), RuleTrust.Official),
            new RuleSource(empty.Path, RuleTrust.ThirdParty)]);

        Assert.Empty(set.Rules);
        Assert.Empty(set.Errors);
    }

    [Fact]
    public void Bad_file_is_skipped_and_others_still_load()   // Review Focus 1
    {
        using var dir = new TempDir();
        var bad = dir.Write("bad.yaml", "id: a\nname: [unclosed");
        dir.Write("good.yaml", RuleYaml("good"));

        var set = RuleSetLoader.Load([new RuleSource(dir.Path, RuleTrust.Official)]);

        Assert.Equal("good", Assert.Single(set.Rules).Id);
        Assert.Contains(set.Errors, e => e.StartsWith(bad, StringComparison.Ordinal));
    }

    [Fact]
    public void Bom_and_chinese_text_load_fine()   // Review Focus 5
    {
        using var dir = new TempDir();
        dir.Write("wx.yaml", RuleYaml("tencent.wechat", "微信"), withBom: true);

        var set = RuleSetLoader.Load([new RuleSource(dir.Path, RuleTrust.Official)]);

        Assert.Equal("微信", Assert.Single(set.Rules).Name);
    }

    [Fact]
    public void Duplicate_id_in_same_source_keeps_first_with_warning()   // Review Focus 2
    {
        using var dir = new TempDir();
        dir.Write("1.yaml", RuleYaml("same", "First"));
        dir.Write("2.yaml", RuleYaml("same", "Second"));

        var set = RuleSetLoader.Load([new RuleSource(dir.Path, RuleTrust.Official)]);

        Assert.Equal("First", Assert.Single(set.Rules).Name);
        Assert.Contains(set.Warnings, w => w.Contains("same") && w.Contains("重复"));
    }

    [Fact]
    public void Third_party_cannot_override_official_even_if_listed_first()   // Review Focus 2
    {
        using var official = new TempDir();
        using var third = new TempDir();
        official.Write("wps.yaml", RuleYaml("kingsoft.wps", "官方"));
        third.Write("wps.yaml", RuleYaml("kingsoft.wps", "第三方"));
        third.Write("extra.yaml", RuleYaml("community.extra", "社区补充"));

        var set = RuleSetLoader.Load([
            new RuleSource(third.Path, RuleTrust.ThirdParty),      // 故意把第三方写在前面
            new RuleSource(official.Path, RuleTrust.Official)]);

        Assert.Equal("官方", set.Rules.Single(r => r.Id == "kingsoft.wps").Name);
        Assert.Equal(RuleTrust.ThirdParty, set.Rules.Single(r => r.Id == "community.extra").Trust);
        Assert.Contains(set.Warnings, w => w.Contains("不能覆盖官方规则"));
    }
}
