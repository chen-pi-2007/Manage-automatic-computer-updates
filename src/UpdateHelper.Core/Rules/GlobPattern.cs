using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace UpdateHelper.Core.Rules;

/// <summary>只认 * 和 ? 两个通配符的匹配，其他字符一律按字面处理。忽略大小写，整串匹配。</summary>
public static class GlobPattern
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new();

    public static bool IsMatch(string pattern, string? text)
    {
        if (text is null) return false;
        var regex = Cache.GetOrAdd(pattern, static p =>
        {
            var escaped = Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".");
            return new Regex($"^{escaped}$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);
        });
        return regex.IsMatch(text);
    }
}
