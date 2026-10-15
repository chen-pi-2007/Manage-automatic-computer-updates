using System.Text.Json;

namespace UpdateHelper.Core.Rules.ThirdParty;

/// <summary>第三方源配置（rule-sources.json）读写 + 缓存目录管理。读坏数据不抛，回退默认。</summary>
public sealed class RuleSourceStore(string configPath, string cacheRoot)
{
    public static string DefaultConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UpdateHelper", "rule-sources.json");

    public static string DefaultCacheRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHelper", "thirdparty-cache");

    public string CacheRoot => cacheRoot;
    public string CacheDirFor(string id) => Path.Combine(cacheRoot, id);

    public ThirdPartyConfig Read()
    {
        try
        {
            if (!File.Exists(configPath)) return ThirdPartyConfig.Default;
            var dto = JsonSerializer.Deserialize<ConfigDto>(File.ReadAllText(configPath), JsonOpts);
            if (dto is null) return ThirdPartyConfig.Default;
            var sources = (dto.Sources ?? [])
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Url))
                .Select(s => new ThirdPartySource(
                    SourceId.FromUrl(s!.Url!.Trim()),   // 一律由 Url 重算 Id，忽略文件里的 id（防路径穿越）
                    s.Url!.Trim(), s.Note ?? "", s.Enabled, s.AllowOverrideOfficial))
                .ToList();
            return new ThirdPartyConfig(dto.ThirdPartyEnabled, sources);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return ThirdPartyConfig.Default;
        }
    }

    public void Write(ThirdPartyConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(configPath))!);
        var dto = new ConfigDto
        {
            ThirdPartyEnabled = config.ThirdPartyEnabled,
            Sources = config.Sources.Select(s => new SourceDto
            {
                Id = s.Id, Url = s.Url, Note = s.Note, Enabled = s.Enabled, AllowOverrideOfficial = s.AllowOverrideOfficial,
            }).ToList(),
        };
        File.WriteAllText(configPath, JsonSerializer.Serialize(dto, JsonOpts));
    }

    /// <summary>删掉缓存里不在当前配置中的源目录（删源 / 改 URL 后的残留清理）。</summary>
    public void CleanOrphanCaches(ThirdPartyConfig config)
    {
        try
        {
            if (!Directory.Exists(cacheRoot)) return;
            var keep = config.Sources.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var d in Directory.GetDirectories(cacheRoot))
            {
                if (keep.Contains(Path.GetFileName(d))) continue;
                try { Directory.Delete(d, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>把"已启用的第三方源"映射成加载用的 RuleSource；全局关 → 空。</summary>
    public IReadOnlyList<RuleSource> BuildRuleSources(ThirdPartyConfig config)
    {
        if (!config.ThirdPartyEnabled) return [];
        return config.Sources
            .Where(s => s.Enabled)
            .Select(s => new RuleSource(CacheDirFor(s.Id), RuleTrust.ThirdParty,
                s.AllowOverrideOfficial, s.Url, string.IsNullOrWhiteSpace(s.Note) ? s.Url : s.Note))
            .ToList();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private sealed class ConfigDto
    {
        public bool ThirdPartyEnabled { get; set; } = true;   // 缺省键 → 默认开
        public List<SourceDto>? Sources { get; set; }
    }

    private sealed class SourceDto
    {
        public string? Id { get; set; }
        public string? Url { get; set; }
        public string? Note { get; set; }
        public bool Enabled { get; set; } = true;
        public bool AllowOverrideOfficial { get; set; }
    }
}
