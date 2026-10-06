namespace UpdateHelper.Core.Rules;

/// <summary>一个存放规则文件的本地目录，以及它的可信度。</summary>
public sealed record RuleSource(string Directory, RuleTrust Trust);

/// <summary>加载好的规则，以及加载过程中的错误（被跳过的文件）和警告（被忽略的重复规则）。</summary>
public sealed record RuleSet(IReadOnlyList<Rule> Rules, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public static RuleSet Empty { get; } = new([], [], []);
}
