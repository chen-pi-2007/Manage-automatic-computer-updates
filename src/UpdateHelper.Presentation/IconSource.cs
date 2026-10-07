namespace UpdateHelper.Presentation;

/// <summary>软件图标在哪个文件的第几个图标（Index 为负数时表示资源编号）。</summary>
public sealed record IconSource(string Path, int Index)
{
    private static readonly string[] IconFileTypes = [".exe", ".dll", ".ico", ".icl", ".ocx", ".cpl"];

    /// <summary>
    /// 解析注册表里的 DisplayIcon：形如 <c>"C:\x\a.exe",0</c>、<c>a.dll,-101</c>、<c>%SystemRoot%\x.ico</c>。
    /// 不是绝对路径、不是能带图标的文件类型、或者看起来是带参数的命令行时返回 null。只解析文字，不碰文件。
    /// </summary>
    public static IconSource? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = Environment.ExpandEnvironmentVariables(raw.Trim());

        var index = 0;
        var comma = s.LastIndexOf(',');
        if (comma > 0 && int.TryParse(s[(comma + 1)..].Trim(), out var i))
        {
            index = i;
            s = s[..comma].Trim();
        }
        s = s.Trim('"').Trim();

        if (!System.IO.Path.IsPathFullyQualified(s)) return null;
        var ext = System.IO.Path.GetExtension(s);
        if (!IconFileTypes.Contains(ext, StringComparer.OrdinalIgnoreCase)) return null;
        return new IconSource(s, index);
    }
}
