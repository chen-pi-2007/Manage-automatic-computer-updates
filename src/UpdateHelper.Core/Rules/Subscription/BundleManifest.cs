using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>规则包里一个文件的名字和内容 sha256（小写十六进制）。</summary>
public sealed record ManifestFile(string Name, string Sha256);

/// <summary>规则包清单。bundleVersion 单调递增，用于择新与防降级。</summary>
public sealed record BundleManifest(
    int BundleVersion, DateTimeOffset GeneratedAt, int RuleCount, IReadOnlyList<ManifestFile> Files);

/// <summary>读写 manifest.json。坏数据返回失败，不抛。</summary>
public static class ManifestParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Serialize(BundleManifest m) => JsonSerializer.Serialize(m, Options);

    public static (bool Ok, BundleManifest? Manifest, string? Error) Parse(string json)
    {
        try
        {
            var m = JsonSerializer.Deserialize<BundleManifest>(json, Options);
            if (m is null || m.Files is null) return (false, null, "manifest 缺少必要字段");
            return (true, m, null);
        }
        catch (JsonException ex)
        {
            return (false, null, $"manifest 解析失败：{ex.Message}");
        }
    }
}
