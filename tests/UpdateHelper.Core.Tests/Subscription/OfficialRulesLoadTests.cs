using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Rules.Subscription;

namespace UpdateHelper.Core.Tests.Subscription;

public sealed class OfficialRulesLoadTests
{
    // 最小合法规则：RuleParser 必填 id、name、match.displayName
    private static string DirWith(int version, string ruleId)
    {
        var d = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "uh-load-" + Guid.NewGuid().ToString("N"))).FullName;
        File.WriteAllText(Path.Combine(d, $"{ruleId}.yaml"),
            $"id: {ruleId}\nname: {ruleId}\nmatch:\n  displayName: {ruleId}\n");
        RuleBundleBuilder.WriteBuiltinManifest(d, version);
        return d;
    }

    [Fact]
    public void Loader_uses_higher_version_directory()
    {
        var builtin = DirWith(1, "builtinrule");
        var cache = DirWith(9, "cacherule");
        var dir = OfficialRules.SelectDirectory(builtin, cache);
        var set = RuleSetLoader.Load([new RuleSource(dir, RuleTrust.Official)]);
        Assert.Contains(set.Rules, r => r.Id == "cacherule");
        Assert.DoesNotContain(set.Rules, r => r.Id == "builtinrule");
    }
}
