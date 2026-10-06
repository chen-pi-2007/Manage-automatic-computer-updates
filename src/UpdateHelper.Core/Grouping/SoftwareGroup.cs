using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Grouping;

/// <summary>归组后的"一个软件"：主条目 + 它的组件 + 它的后台项目。</summary>
public sealed class SoftwareGroup
{
    public required string Name { get; init; }
    public string? Publisher { get; init; }
    public string? Version { get; init; }
    /// <summary>分类。先由内置规则判断，命中 YAML 规则且规则写了 category 时被覆盖。</summary>
    public SoftwareCategory Category { get; set; }

    /// <summary>命中的 YAML 规则 id；没有命中为 null。</summary>
    public string? RuleId { get; set; }

    /// <summary>主条目；"组件合集"没有主条目。</summary>
    public UninstallEntry? Primary { get; init; }

    public List<UninstallEntry> Components { get; } = [];
    public List<BackgroundItem> Background { get; } = [];

    /// <summary>登记信息不完整（比如名称是没替换的模板占位符）。</summary>
    public bool IsIncomplete { get; init; }

    /// <summary>主条目和组件的所有安装位置。</summary>
    public IEnumerable<string> InstallLocations =>
        (Primary is null ? Components : Components.Prepend(Primary))
            .Select(e => e.InstallLocation)
            .OfType<string>();
}

/// <summary>一次扫描的整理结果。</summary>
/// <param name="TotalEntries">去重前的登记总数（用于和系统实际数量核对）。</param>
public sealed record ScanResult(
    IReadOnlyList<SoftwareGroup> Groups,
    IReadOnlyList<BackgroundItem> UnassignedBackground,
    int TotalEntries);
