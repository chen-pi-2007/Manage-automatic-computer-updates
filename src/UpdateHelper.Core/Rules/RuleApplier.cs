using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Rules;

/// <summary>套用规则后的扫描结果，以及每个后台项目的一句话说明。</summary>
public sealed record RuleReport(ScanResult Result, IReadOnlyDictionary<BackgroundItem, string> Explanations);

/// <summary>把 YAML 规则套到扫描结果上。纯逻辑；会修改传入的 SoftwareGroup。</summary>
public static class RuleApplier
{
    public static RuleReport Apply(ScanResult scan, RuleSet rules)
    {
        var ruleById = rules.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);

        // 1. 认软件
        foreach (var g in scan.Groups.Where(g => g.Primary is not null))
        {
            var rule = rules.Rules.FirstOrDefault(r => MatchesGroup(r, g));
            if (rule is null) continue;
            g.RuleId = rule.Id;
            if (rule.Category is { } category) g.Category = category;
        }

        var ordered = scan.Groups
            .OrderBy(g => g.Category)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        // 2. 写说明（已归属的项目优先用本组规则）
        var explanations = new Dictionary<BackgroundItem, string>();
        foreach (var g in ordered)
        {
            var own = g.RuleId is null ? null : ruleById[g.RuleId];
            foreach (var item in g.Background)
            {
                var explain = (own is null ? null : FindExplain(own, item))
                              ?? rules.Rules.Select(r => FindExplain(r, item)).FirstOrDefault(e => e is not null);
                if (explain is not null) explanations[item] = explain;
            }
        }

        // 2+3. 未归属的项目：写说明，并尝试认领
        var stillUnassigned = new List<BackgroundItem>();
        foreach (var item in scan.UnassignedBackground)
        {
            var rule = rules.Rules.FirstOrDefault(r => FindExplain(r, item) is not null);
            if (rule is null)
            {
                stillUnassigned.Add(item);
                continue;
            }

            explanations[item] = FindExplain(rule, item)!;
            var owner = ordered.FirstOrDefault(g => g.RuleId == rule.Id);
            if (owner is not null) owner.Background.Add(item);
            else stillUnassigned.Add(item);
        }

        return new RuleReport(new ScanResult(ordered, stillUnassigned, scan.TotalEntries), explanations);
    }

    private static bool MatchesGroup(Rule rule, SoftwareGroup g) =>
        GlobPattern.IsMatch(rule.Match.DisplayName, g.Name)
        && (rule.Match.Publisher is null || GlobPattern.IsMatch(rule.Match.Publisher, g.Publisher));

    private static string? FindExplain(Rule rule, BackgroundItem item) =>
        rule.Background
            .FirstOrDefault(b => b.Kind == item.Kind && GlobPattern.IsMatch(b.NamePattern, item.Name))
            ?.Explain;
}
