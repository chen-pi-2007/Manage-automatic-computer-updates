namespace UpdateHelper.Presentation;

/// <summary>环形图的一段：从 StartAngle 开始，顺时针占 SweepAngle 度（12 点方向为 0 度）。</summary>
public sealed record ChartSlice(string Label, int Count, double StartAngle, double SweepAngle, string Color);

/// <summary>条形图的一条：Percent 是相对最长一条的百分比（0~100）。</summary>
public sealed record ChartBar(string Label, int Count, double Percent);

/// <summary>首页图表的计算。只算角度和比例，画图在界面层。</summary>
public static class Charts
{
    /// <summary>按数量把整圈分给各段；数量为 0 的不画，全为 0 时返回空。</summary>
    public static IReadOnlyList<ChartSlice> Donut(IEnumerable<(string Label, int Count, string Color)> parts)
    {
        var list = parts.Where(p => p.Count > 0).ToList();
        var total = list.Sum(p => p.Count);
        var result = new List<ChartSlice>();
        double start = 0;
        foreach (var (label, count, color) in list)
        {
            var sweep = 360.0 * count / total;
            result.Add(new ChartSlice(label, count, start, sweep, color));
            start += sweep;
        }
        return result;
    }

    /// <summary>条形按原顺序排列，长度相对最大的一条；数量为 0 的不显示。</summary>
    public static IReadOnlyList<ChartBar> Bars(IEnumerable<(string Label, int Count)> items)
    {
        var list = items.Where(i => i.Count > 0).ToList();
        if (list.Count == 0) return [];
        var max = list.Max(i => i.Count);
        return list.Select(i => new ChartBar(i.Label, i.Count, 100.0 * i.Count / max)).ToList();
    }
}
