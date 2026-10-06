using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Updates;

/// <summary>更新的档位（spec 第 5 节）。</summary>
public enum UpdateTier
{
    /// <summary>低风险：分级自动模式下空闲时自动安装</summary>
    Low,
    /// <summary>需确认：只提醒</summary>
    Careful,
    /// <summary>不自动：任何模式都不自动安装，并显示原因</summary>
    NeverAuto,
    /// <summary>不管：不出现在更新列表里（游戏等）</summary>
    Ignored,
}

/// <summary>一条判断好的更新：候选、对应的软件组（可能对不上）、档位、给人看的理由。</summary>
public sealed record JudgedUpdate(UpdateCandidate Candidate, SoftwareGroup? Group, UpdateTier Tier, string Reason);

/// <summary>给更新候选分档。纯逻辑，规则见计划 3 Task 3 的判断顺序表。</summary>
public static class UpdateJudge
{
    private const int ApplicationMinorJumpThreshold = 10;

    public static IReadOnlyList<JudgedUpdate> Judge(
        IReadOnlyList<UpdateCandidate> candidates, IReadOnlyList<SoftwareGroup> groups, RuleSet rules)
    {
        var ruleById = rules.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var results = new List<JudgedUpdate>();

        foreach (var c in candidates)
        {
            var installed = AppVersion.Parse(c.InstalledVersion);
            var available = AppVersion.Parse(c.AvailableVersion);

            // 0. winget 说有更新，但新版本其实不比现在新
            if (installed is { IsApproximate: false } && available is not null && available.CompareTo(installed) <= 0)
                continue;

            var group = UpdateMatcher.FindGroup(c, groups);
            var (tier, reason) = Decide(c, group, installed, available, groups, ruleById);
            results.Add(new JudgedUpdate(c, group, tier, reason));
        }

        return results
            .OrderBy(r => r.Tier)
            .ThenBy(r => r.Group?.Name ?? r.Candidate.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static (UpdateTier, string) Decide(
        UpdateCandidate c, SoftwareGroup? group, AppVersion? installed, AppVersion? available,
        IReadOnlyList<SoftwareGroup> groups, IReadOnlyDictionary<string, Rule> ruleById)
    {
        if (group is null)
            return (UpdateTier.NeverAuto, "没有在扫描结果里找到对应的软件，无法确认装的是哪一个");

        switch (group.Category)
        {
            case SoftwareCategory.Game:
                return (UpdateTier.Ignored, "游戏由 Steam 等平台负责更新");
            case SoftwareCategory.SystemComponent:
                return (UpdateTier.NeverAuto, "这是系统或厂商组件，由对应的软件负责更新");
        }

        var sameName = groups.Where(g => g.Primary is not null
                                         && string.Equals(g.Name, group.Name, StringComparison.OrdinalIgnoreCase))
                             .ToList();
        if (sameName.Count > 1)
        {
            var versions = string.Join("、", sameName.Select(g => g.Version ?? "未知版本").Order(StringComparer.Ordinal));
            return (UpdateTier.NeverAuto, $"电脑上登记了多个\"{group.Name}\"（{versions}），无法确定该更新哪一个");
        }

        if (group.Category == SoftwareCategory.Runtime)
            return (UpdateTier.NeverAuto, "运行库通常多个版本并存，由需要它的软件负责安装");

        if (installed is null || available is null || installed.IsApproximate)
            return (UpdateTier.NeverAuto, $"版本号无法准确比较（{c.InstalledVersion} → {c.AvailableVersion}）");

        var risk = group.RuleId is not null && ruleById.TryGetValue(group.RuleId, out var rule) ? rule.Update?.Risk : null;
        if (risk == RiskLevel.NeverAuto) return (UpdateTier.NeverAuto, "规则标记为永不自动更新");
        if (risk == RiskLevel.Careful) return (UpdateTier.Careful, "规则标记为需要确认后再更新");

        if (available.Major != installed.Major)
            return (UpdateTier.Careful, $"大版本变化（{installed.Major} → {available.Major}），可能有较大改动");

        if (group.Category == SoftwareCategory.Driver)
            return (UpdateTier.Careful, "驱动更新可能影响硬件，建议确认后再装");

        var minorText = $"{installed.Major}.{installed.Minor} → {available.Major}.{available.Minor}";
        if (group.Category == SoftwareCategory.DevTool && available.Minor != installed.Minor)
            return (UpdateTier.Careful, $"开发工具跨了小版本（{minorText}），可能影响开发环境");

        if (available.Minor - installed.Minor >= ApplicationMinorJumpThreshold)
            return (UpdateTier.Careful, $"一次跳过了很多版本（{minorText}）");

        return (UpdateTier.Low, "小版本更新");
    }
}
