using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Updates;

/// <summary>
/// 从 YAML 规则的 update.latest 产生更新候选（winget 里没有的软件靠它）。
/// scan 必须已经套用过 rules（RuleApplier.Apply），这样组上才有 RuleId。
/// </summary>
public sealed class RuleUpdateSource(ScanResult scan, RuleSet rules) : IUpdateSource
{
    public const string PackagePrefix = "rule:";

    public string Name => "规则库";

    public IReadOnlyList<UpdateCandidate> GetAvailableUpdates()
    {
        var byId = rules.Rules.ToDictionary(r => r.Id, StringComparer.Ordinal);
        var result = new List<UpdateCandidate>();
        foreach (var g in scan.Groups)
        {
            if (g.Primary is null || g.RuleId is null || g.Version is null) continue;
            if (!byId.TryGetValue(g.RuleId, out var rule) || rule.Update?.Latest is not { } latest) continue;
            if (string.Equals(latest.Version, g.Version, StringComparison.OrdinalIgnoreCase)) continue;

            result.Add(new UpdateCandidate(PackagePrefix + rule.Id, g.Name, g.Publisher, g.Version, latest.Version,
                [g.Primary.KeyName]));
        }
        return result;
    }
}
