namespace UpdateHelper.Core.Rules;

/// <summary>从多个本地目录加载规则。官方优先，坏文件跳过，重复 id 只保留一份。只读。</summary>
public static class RuleSetLoader
{
    public static RuleSet Load(IEnumerable<RuleSource> sources)
    {
        var rules = new List<Rule>();
        var byId = new Dictionary<string, Rule>(StringComparer.Ordinal);
        var errors = new List<string>();
        var warnings = new List<string>();

        // OrderBy 是稳定排序：官方在前，同一可信度保持传入顺序
        foreach (var source in sources.OrderBy(s => s.Trust == RuleTrust.Official ? 0 : 1))
        {
            foreach (var file in FindRuleFiles(source.Directory, errors))
            {
                string text;
                try
                {
                    text = File.ReadAllText(file);   // 自动识别并去掉 UTF-8 BOM
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add($"{file}: 读取失败：{ex.Message}");
                    continue;
                }

                var parsed = RuleParser.Parse(text, file);
                if (!parsed.Ok)
                {
                    errors.AddRange(parsed.Errors);
                    continue;
                }

                var rule = parsed.Rule! with { Trust = source.Trust };
                if (byId.TryGetValue(rule.Id, out var existing))
                {
                    warnings.Add(existing.Trust == RuleTrust.Official && rule.Trust == RuleTrust.ThirdParty
                        ? $"{file}: 第三方规则不能覆盖官方规则 {rule.Id}，已忽略"
                        : $"{file}: id {rule.Id} 与 {existing.SourceFile} 重复，已忽略");
                    continue;
                }

                byId[rule.Id] = rule;
                rules.Add(rule);
            }
        }

        return new RuleSet(rules, errors, warnings);
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
