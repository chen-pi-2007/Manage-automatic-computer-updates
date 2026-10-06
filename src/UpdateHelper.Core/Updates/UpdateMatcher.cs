using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Core.Updates;

/// <summary>按注册表卸载键名，把更新候选对到计划 1 扫描出的软件组。纯逻辑。</summary>
public static class UpdateMatcher
{
    public static SoftwareGroup? FindGroup(UpdateCandidate candidate, IReadOnlyList<SoftwareGroup> groups)
    {
        if (candidate.ProductCodes.Count == 0) return null;
        var codes = candidate.ProductCodes.ToHashSet(StringComparer.OrdinalIgnoreCase);

        return groups.FirstOrDefault(g => g.Primary is not null && codes.Contains(g.Primary.KeyName))
            ?? groups.FirstOrDefault(g => g.Components.Any(c => codes.Contains(c.KeyName)));
    }
}
