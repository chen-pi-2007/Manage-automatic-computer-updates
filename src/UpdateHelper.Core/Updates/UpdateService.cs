using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Updates;

/// <summary>判断好的更新列表，以及来源不可用时的提示。</summary>
public sealed record UpdateReport(IReadOnlyList<JudgedUpdate> Updates, string? Warning);

/// <summary>向各来源要候选并分档。来源出任何问题都转成中文提示，不向外抛异常。</summary>
public static class UpdateService
{
    public static UpdateReport Check(IUpdateSource source, ScanResult scan, RuleSet rules)
        => Check([source], scan, rules);

    /// <summary>按顺序问每个来源（winget 放前面）；同一个软件只保留最先报告它的来源的候选。</summary>
    public static UpdateReport Check(IReadOnlyList<IUpdateSource> sources, ScanResult scan, RuleSet rules)
    {
        var candidates = new List<UpdateCandidate>();
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<string>();

        foreach (var source in sources)
        {
            IReadOnlyList<UpdateCandidate> found;
            try
            {
                found = source.GetAvailableUpdates();
            }
            catch (Exception ex)
            {
                warnings.Add($"无法从 {source.Name} 获取更新信息：{ex.Message}");
                continue;
            }

            foreach (var c in found)
            {
                if (c.ProductCodes.Any(seenCodes.Contains)) continue;   // 前面的来源已经报告过这个软件
                candidates.Add(c);
                seenCodes.UnionWith(c.ProductCodes);
            }
        }

        return new UpdateReport(UpdateJudge.Judge(candidates, scan.Groups, rules),
            warnings.Count == 0 ? null : string.Join("；", warnings));
    }
}
