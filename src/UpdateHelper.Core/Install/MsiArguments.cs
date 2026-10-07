using System.Text;
using System.Text.RegularExpressions;

namespace UpdateHelper.Core.Install;

/// <summary>
/// 检查规则里给 msiexec 的静默参数是否安全。msiexec 能通过 TRANSFORMS=、PATCH=、/p 等加载额外文件，
/// 那些文件没有经过签名校验却能以安装权限执行代码（独立审查发现），所以这里只放行白名单：
/// 少数界面/重启开关，加上普通的公开属性（全大写名字，且不是会加载文件或改变动作的属性）。
/// </summary>
public static partial class MsiArguments
{
    private static readonly HashSet<string> AllowedSwitches = new(StringComparer.OrdinalIgnoreCase)
    {
        "/qn", "/qb", "/qb-", "/qb!", "/quiet", "/passive", "/norestart",
    };

    /// <summary>会加载额外文件或改变 msiexec 动作的属性，一律禁止。</summary>
    private static readonly HashSet<string> ForbiddenProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "TRANSFORMS", "PATCH", "MSIPATCHREMOVE", "MSINEWINSTANCE", "ACTION",
    };

    /// <summary>返回第一个不安全的参数（原样）；全部安全时返回 null；参数为空时返回"（空）"。</summary>
    public static string? FindUnsafe(string arguments)
    {
        var tokens = Tokenize(arguments);
        if (tokens.Count == 0) return "（空）";

        foreach (var token in tokens)
        {
            if (token.StartsWith('/') || token.StartsWith('-'))
            {
                if (!AllowedSwitches.Contains(token)) return token;
                continue;
            }

            var eq = token.IndexOf('=');
            if (eq <= 0) return token;                                   // 既不是开关也不是属性
            var name = token[..eq];
            if (!PublicProperty().IsMatch(name)) return token;           // 私有属性（小写开头等）
            if (ForbiddenProperties.Contains(name)) return token;
        }
        return null;
    }

    /// <summary>按空白切分，双引号里的空白不切（INSTALLDIR="C:\Program Files\X" 是一个参数）。</summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var ch in text)
        {
            if (ch == '"') inQuotes = !inQuotes;
            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0) tokens.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    [GeneratedRegex("^[A-Z][A-Z0-9_]*$")]
    private static partial Regex PublicProperty();
}
