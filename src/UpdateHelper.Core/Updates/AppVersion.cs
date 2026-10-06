using System.Globalization;

namespace UpdateHelper.Core.Updates;

/// <summary>版本号中的一段：开头的数字 + 后面的文字（如 "45f2c1" → 45, "f2c1"）。</summary>
public readonly record struct VersionPart(long Number, string Suffix);

/// <summary>能比较各种软件版本号的解析结果。解析规则见计划 3 Task 1。</summary>
public sealed class AppVersion : IComparable<AppVersion>
{
    private static readonly char[] Separators = ['.', '-', '_', '+'];

    private AppVersion(string original, IReadOnlyList<VersionPart> parts, bool approximate)
    {
        Original = original;
        Parts = parts;
        IsApproximate = approximate;
    }

    public string Original { get; }
    public IReadOnlyList<VersionPart> Parts { get; }

    /// <summary>原文带 &lt; 或 &gt;：winget 只知道大概范围，不知道确切版本。</summary>
    public bool IsApproximate { get; }

    public long Major => Parts[0].Number;
    public long Minor => Parts.Count > 1 ? Parts[1].Number : 0;

    public static AppVersion? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();

        var approximate = false;
        if (s[0] is '<' or '>')
        {
            approximate = true;
            s = s[1..].Trim();
        }
        if (s.Length > 0 && s[0] is 'v' or 'V') s = s[1..];

        var parts = new List<VersionPart>();
        foreach (var segment in s.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var digits = 0;
            while (digits < segment.Length && char.IsAsciiDigit(segment[digits])) digits++;

            if (digits == 0)
            {
                if (parts.Count == 0) return null;          // 第一段必须以数字开头
                parts.Add(new VersionPart(0, segment));
                continue;
            }
            if (digits > 18) return null;                   // 太长，long 放不下
            var number = long.Parse(segment.AsSpan(0, digits), CultureInfo.InvariantCulture);
            parts.Add(new VersionPart(number, segment[digits..]));
        }

        return parts.Count == 0 ? null : new AppVersion(text.Trim(), parts, approximate);
    }

    public int CompareTo(AppVersion? other)
    {
        if (other is null) return 1;
        var n = Math.Max(Parts.Count, other.Parts.Count);
        for (var i = 0; i < n; i++)
        {
            var a = i < Parts.Count ? Parts[i] : new VersionPart(0, "");
            var b = i < other.Parts.Count ? other.Parts[i] : new VersionPart(0, "");

            var byNumber = a.Number.CompareTo(b.Number);
            if (byNumber != 0) return byNumber;

            var bySuffix = CompareSuffix(a.Suffix, b.Suffix);
            if (bySuffix != 0) return bySuffix;
        }
        return 0;
    }

    /// <summary>没有后缀的比有后缀的大（正式版 &gt; 预览版）；都有后缀时按字典序。</summary>
    private static int CompareSuffix(string a, string b)
    {
        if (a.Length == 0 && b.Length == 0) return 0;
        if (a.Length == 0) return 1;
        if (b.Length == 0) return -1;
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public override string ToString() => Original;
}
