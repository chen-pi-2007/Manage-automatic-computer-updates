namespace UpdateHelper.Core.Grouping;

public static class PathUtil
{
    /// <summary>规范化为以 \ 结尾的完整目录路径；无效路径返回 null。</summary>
    public static string? NormalizeDir(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        // GetFullPath 遇到 | 等字符不一定抛异常，先自己挡掉
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            return full.TrimEnd('\\') + "\\";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>path 是否位于 dir 目录之内（忽略大小写，按文件夹边界判断）。</summary>
    public static bool IsUnder(string? path, string? dir)
    {
        var d = NormalizeDir(dir);
        var p = NormalizeDir(path);
        return d is not null && p is not null && p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }
}
