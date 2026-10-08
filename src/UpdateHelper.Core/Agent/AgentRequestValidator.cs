using System.Text.RegularExpressions;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Agent;

/// <summary>
/// Agent 收到请求后先过这一关。只放行"升级一个 winget 包到一个具体版本"：
/// 包 id 必须是 winget 的"发布者.名称"格式（不含空格、引号、冒号），版本号只能是字母数字和 . _ + -。
/// 这样请求里的任何字段都没法变成命令行参数或别的东西。
/// </summary>
public static partial class AgentRequestValidator
{
    /// <returns>null 表示通过；否则是给用户看的中文原因。</returns>
    public static string? Validate(AgentRequest r)
    {
        if (r.Version != AgentProtocol.Version)
            return $"程序和后台助手的版本不一致（{r.Version} / {AgentProtocol.Version}），请在设置里更新后台助手";
        if (r.Op == AgentOp.Ping) return null;
        if (r.Op != AgentOp.Upgrade) return "不认识的操作";

        if (r.PackageId is null || !PackageIdPattern().IsMatch(r.PackageId))
            return $"包 id 格式不对：{Shorten(r.PackageId)}";
        if (r.TargetVersion is null || !VersionPattern().IsMatch(r.TargetVersion))
            return $"版本号格式不对：{Shorten(r.TargetVersion)}";
        if (!Enum.IsDefined(r.Scope))
            return "安装范围不对";
        return null;
    }

    private static string Shorten(string? s) => s is null ? "（空）" : s.Length <= 40 ? s : s[..40] + "…";

    /// <summary>winget 包 id：发布者.名称（可以有多段），总长不超过 128。</summary>
    [GeneratedRegex(@"^(?=.{3,128}$)[A-Za-z0-9][A-Za-z0-9_+\-]*(\.[A-Za-z0-9_+\-]+)+$")]
    private static partial Regex PackageIdPattern();

    /// <summary>具体版本号：不能有空格和 &lt; &gt; 这类模糊写法，总长不超过 64。</summary>
    [GeneratedRegex(@"^(?=.{1,64}$)[A-Za-z0-9][A-Za-z0-9._+\-]*$")]
    private static partial Regex VersionPattern();
}
