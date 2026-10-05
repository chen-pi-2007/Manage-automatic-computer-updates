namespace UpdateHelper.Core.Scanning;

/// <summary>从一整行命令里取出程序（或 rundll32 加载的 dll）的路径。纯逻辑，永不抛异常。</summary>
public static class CommandLineParser
{
    public static string? ExtractExecutable(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var text = Environment.ExpandEnvironmentVariables(commandLine).Trim();

        var (program, rest) = SplitFirst(text);
        if (program is null) return null;

        // rundll32 x.dll,入口 —— 返回 dll 路径
        if (Path.GetFileName(program).Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase)
            || program.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            var (dllPart, _) = SplitFirst(rest);
            if (dllPart is null) return null;
            var comma = dllPart.IndexOf(',');
            var dll = (comma >= 0 ? dllPart[..comma] : dllPart).Trim().Trim('"');
            return dll.Length == 0 ? null : dll;
        }

        return program;
    }

    /// <summary>取出第一个"程序"部分和剩余参数。</summary>
    private static (string? First, string Remainder) SplitFirst(string text)
    {
        text = text.TrimStart();
        if (text.Length == 0) return (null, "");

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            if (end < 0) return (null, "");
            var inner = text[1..end].Trim();
            return (inner.Length == 0 ? null : inner, text[(end + 1)..]);
        }

        // 不带引号：逐个累加空格分隔的片段，第一个以 .exe 结尾的就是程序（路径可以含空格）
        var parts = text.Split(' ');
        for (var i = 0; i < parts.Length; i++)
        {
            var candidate = string.Join(' ', parts[..(i + 1)]);
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return (candidate, string.Join(' ', parts[(i + 1)..]));
        }

        // 都不以 .exe 结尾（比如 .cmd、或 "x.dll,入口"）：取第一个片段
        return (parts[0], string.Join(' ', parts[1..]));
    }
}
