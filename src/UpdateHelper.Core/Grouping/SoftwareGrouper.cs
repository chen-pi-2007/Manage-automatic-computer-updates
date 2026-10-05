using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Grouping;

/// <summary>把卸载登记和后台项目整理成"一个软件一组"。纯逻辑。</summary>
public static class SoftwareGrouper
{
    public static bool IsPlaceholderName(string name) =>
        name.Contains("{{", StringComparison.Ordinal) || name.Contains("${", StringComparison.Ordinal);

    public static ScanResult Group(IReadOnlyList<UninstallEntry> entries, IReadOnlyList<BackgroundItem> background)
    {
        // 1. 去重
        var unique = entries
            .DistinctBy(e => (e.DisplayName.ToUpperInvariant(), e.DisplayVersion?.ToUpperInvariant(), e.Publisher?.ToUpperInvariant()))
            .ToList();

        // 2. 主条目
        var groups = new List<SoftwareGroup>();
        var byPrimary = new Dictionary<UninstallEntry, SoftwareGroup>();
        foreach (var e in unique.Where(e => !e.IsSystemComponent && !e.IsUpdateOrPatch))
        {
            var g = new SoftwareGroup
            {
                Name = e.DisplayName,
                Publisher = e.Publisher,
                Version = e.DisplayVersion,
                Category = SoftwareClassifier.Classify(e),
                Primary = e,
                IsIncomplete = IsPlaceholderName(e.DisplayName),
            };
            groups.Add(g);
            byPrimary[e] = g;
        }

        // 3. 其余条目找主人
        var collections = new Dictionary<string, SoftwareGroup>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in unique.Where(e => e.IsSystemComponent || e.IsUpdateOrPatch))
        {
            var owner = FindOwner(e, byPrimary.Keys);
            if (owner is not null)
            {
                byPrimary[owner].Components.Add(e);
                continue;
            }

            var label = e.Publisher is null ? "未知发布者的组件" : $"{e.Publisher} 组件";
            if (!collections.TryGetValue(label, out var col))
            {
                col = new SoftwareGroup { Name = label, Publisher = e.Publisher, Category = SoftwareCategory.SystemComponent };
                collections[label] = col;
                groups.Add(col);
            }
            col.Components.Add(e);
        }

        // 5. 后台项目
        var unassigned = new List<BackgroundItem>();
        foreach (var item in background)
        {
            var best = groups
                .SelectMany(g => g.InstallLocations.Select(loc => (Group: g, Dir: PathUtil.NormalizeDir(loc))))
                .Where(x => x.Dir is not null && PathUtil.IsUnder(item.ExecutablePath, x.Dir))
                .OrderByDescending(x => x.Dir!.Length)
                .FirstOrDefault();
            if (best.Group is not null) best.Group.Background.Add(item);
            else unassigned.Add(item);
        }

        var ordered = groups
            .OrderBy(g => g.Category)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new ScanResult(ordered, unassigned, entries.Count);
    }

    private static UninstallEntry? FindOwner(UninstallEntry e, IEnumerable<UninstallEntry> primaries)
    {
        var list = primaries.ToList();

        // a. ParentKeyName
        if (e.ParentKeyName is not null)
        {
            var byKey = list.FirstOrDefault(p => p.KeyName.Equals(e.ParentKeyName, StringComparison.OrdinalIgnoreCase));
            if (byKey is not null) return byKey;
        }

        // b. 安装位置（最深的胜出）
        if (e.InstallLocation is not null)
        {
            var byLocation = list
                .Where(p => p.InstallLocation is not null && PathUtil.IsUnder(e.InstallLocation, p.InstallLocation))
                .OrderByDescending(p => PathUtil.NormalizeDir(p.InstallLocation)!.Length)
                .FirstOrDefault();
            if (byLocation is not null) return byLocation;
        }

        // c. 同发布者 + 名称开头共同词
        if (e.Publisher is null) return null;
        var publisherWords = Words(e.Publisher).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list
            .Where(p => string.Equals(p.Publisher, e.Publisher, StringComparison.OrdinalIgnoreCase))
            .Select(p => (Primary: p, Score: SharedLeadingWords(e.DisplayName, p.DisplayName, publisherWords)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .Select(x => x.Primary)
            .FirstOrDefault();
    }

    /// <summary>两个名称从开头起连续相同的词数，发布者名里出现的词不计分（但仍需相同才能继续往后比）。</summary>
    private static int SharedLeadingWords(string a, string b, HashSet<string> ignored)
    {
        var wa = Words(a).ToList();
        var wb = Words(b).ToList();
        var score = 0;
        for (var i = 0; i < Math.Min(wa.Count, wb.Count); i++)
        {
            if (!wa[i].Equals(wb[i], StringComparison.OrdinalIgnoreCase)) break;
            if (!ignored.Contains(wa[i])) score++;
        }
        return score;
    }

    private static IEnumerable<string> Words(string s) =>
        s.Split([' ', '(', ')', '-', ','], StringSplitOptions.RemoveEmptyEntries);
}
