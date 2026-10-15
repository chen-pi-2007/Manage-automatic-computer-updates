using System.Security.Cryptography;
using System.Text;

namespace UpdateHelper.Core.Rules.ThirdParty;

/// <summary>一个第三方规则源。Id 由 Url 派生（SHA-256 hex），同时用作缓存目录名。</summary>
public sealed record ThirdPartySource(string Id, string Url, string Note, bool Enabled, bool AllowOverrideOfficial);

/// <summary>第三方源总配置：全局开关 + 源数组（数组顺序即加载优先顺序）。</summary>
public sealed record ThirdPartyConfig(bool ThirdPartyEnabled, IReadOnlyList<ThirdPartySource> Sources)
{
    public static ThirdPartyConfig Default { get; } = new(true, []);
}

public static class SourceId
{
    /// <summary>URL → SHA-256 十六进制小写，作为稳定、安全的目录名。</summary>
    public static string FromUrl(string url) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())));
}
