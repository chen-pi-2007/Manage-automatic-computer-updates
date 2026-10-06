using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Tests;

/// <summary>仓库里的官方规则必须全部合法——这是给以后提交规则的人的护栏。</summary>
public class OfficialRulesTests
{
    public static string RulesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "UpdateHelper.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("找不到仓库根目录"), "rules");
    }

    private static RuleSet Load() => RuleSetLoader.Load([new RuleSource(RulesDirectory(), RuleTrust.Official)]);

    [Fact]
    public void All_official_rules_load_without_errors_or_warnings()
    {
        var set = Load();
        Assert.Empty(set.Errors);
        Assert.Empty(set.Warnings);
        Assert.True(set.Rules.Count >= 12, $"只加载到 {set.Rules.Count} 条规则");
    }

    [Fact]
    public void File_name_equals_rule_id()
    {
        foreach (var rule in Load().Rules)
            Assert.Equal(rule.Id + ".yaml", Path.GetFileName(rule.SourceFile));
    }

    [Fact]
    public void Every_rule_says_something_useful()
    {
        foreach (var rule in Load().Rules)
            Assert.True(rule.Category is not null || rule.Background.Count > 0,
                $"{rule.Id} 既没有分类也没有后台说明");
    }

    [Theory]
    [InlineData("流氓")]
    [InlineData("恶意")]
    [InlineData("垃圾")]
    public void Explanations_use_neutral_wording(string word)
    {
        foreach (var b in Load().Rules.SelectMany(r => r.Background))
            Assert.DoesNotContain(word, b.Explain);
    }
}
