namespace UpdateHelper.Core.Rules;

/// <summary>从多个本地目录加载规则。三档优先级（开覆盖第三方 &gt; 官方 &gt; 普通第三方），坏文件/坏文档跳过，去重带来源。只读。</summary>
public static class RuleSetLoader
{
    public static RuleSet Load(IEnumerable<RuleSource> sources)
    {
        var rules = new List<Rule>();
        var byId = new Dictionary<string, Rule>(StringComparer.Ordinal);
        var errors = new List<string>();
        var warnings = new List<string>();

        // 稳定排序：开覆盖第三方(0) → 官方(1) → 普通第三方(2)；同档保持传入顺序（即 rule-sources.json 的顺序）
        foreach (var source in sources.OrderBy(SortKey))
        {
            foreach (var file in FindRuleFiles(source.Directory, errors))
            {
                string text;
                try { text = File.ReadAllText(file); }   // 自动去 UTF-8 BOM
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{file}: 读取失败：{ex.Message}");
                    continue;
                }

                foreach (var parsed in RuleParser.ParseAll(text, file))
                {
                    if (!parsed.Ok) { errors.AddRange(parsed.Errors); continue; }

                    var rule = parsed.Rule! with
                    {
                        Trust = source.Trust,
                        SourceUrl = source.SourceUrl,
                        SourceName = source.SourceName,
                    };
                    if (byId.TryGetValue(rule.Id, out var existing))
                    {
                        warnings.Add(DuplicateWarning(existing, rule, file));
                        continue;
                    }
                    byId[rule.Id] = rule;
                    rules.Add(rule);
                }
            }
        }

        return new RuleSet(rules, errors, warnings);
    }

    private static int SortKey(RuleSource s) =>
        s.Trust == RuleTrust.ThirdParty && s.AllowOverrideOfficial ? 0
        : s.Trust == RuleTrust.Official ? 1
        : 2;

    private static string DuplicateWarning(Rule existing, Rule incoming, string file)
    {
        // 排序保证：官方被顶掉时，existing 必是"开了覆盖的第三方"
        if (existing.Trust == RuleTrust.ThirdParty && incoming.Trust == RuleTrust.Official)
            return $"官方规则 {incoming.Id} 已被你允许覆盖的第三方源「{existing.SourceName}」覆盖";
        if (existing.Trust == RuleTrust.Official && incoming.Trust == RuleTrust.ThirdParty)
            return $"第三方规则 {incoming.Id}（来自「{incoming.SourceName}」）与官方规则同名，未开启覆盖，已忽略";
        return $"{file}: id {incoming.Id} 与 {existing.SourceFile} 重复，已忽略";
    }

    private static IEnumerable<string> FindRuleFiles(string directory, List<string> errors)
    {
        if (!Directory.Exists(directory)) return [];
        try
        {
            return Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(f => f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"{directory}: 无法列出规则文件：{ex.Message}");
            return [];
        }
    }
}
