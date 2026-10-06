using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Updates;

/// <summary>判断好的更新列表，以及来源不可用时的提示。</summary>
public sealed record UpdateReport(IReadOnlyList<JudgedUpdate> Updates, string? Warning);

/// <summary>向来源要候选并分档。来源出任何问题都转成中文提示，不向外抛异常。</summary>
public static class UpdateService
{
    public static UpdateReport Check(IUpdateSource source, ScanResult scan, RuleSet rules)
    {
        IReadOnlyList<UpdateCandidate> candidates;
        try
        {
            candidates = source.GetAvailableUpdates();
        }
        catch (Exception ex)
        {
            return new UpdateReport([], $"无法从 {source.Name} 获取更新信息：{ex.Message}");
        }

        return new UpdateReport(UpdateJudge.Judge(candidates, scan.Groups, rules), null);
    }
}
