using System.Text.Json;

namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>验证通过的规则包落盘缓存 + 上次同步状态。写入用"临时目录整体替换"，避免半套。</summary>
public sealed class RuleCacheStore(string cacheDirectory)
{
    private const string SyncStateFile = "sync-state.json";

    public string Directory => cacheDirectory;

    public BundleManifest? TryReadManifest() => OfficialRules.TryReadManifest(cacheDirectory);

    public void Replace(VerifiedBundle bundle)
    {
        var parent = Path.GetDirectoryName(Path.GetFullPath(cacheDirectory))!;
        System.IO.Directory.CreateDirectory(parent);
        var staging = cacheDirectory + ".new-" + Guid.NewGuid().ToString("N");
        System.IO.Directory.CreateDirectory(staging);
        try
        {
            foreach (var (name, data) in bundle.Files)
                File.WriteAllBytes(Path.Combine(staging, name), data);
            File.WriteAllText(Path.Combine(staging, OfficialRules.ManifestFileName),
                ManifestParser.Serialize(bundle.Manifest));

            // 保留旧的同步状态文件（状态由调用方另行写）
            var backup = cacheDirectory + ".old-" + Guid.NewGuid().ToString("N");
            if (System.IO.Directory.Exists(cacheDirectory))
            {
                var oldState = Path.Combine(cacheDirectory, SyncStateFile);
                if (File.Exists(oldState)) File.Copy(oldState, Path.Combine(staging, SyncStateFile));
                System.IO.Directory.Move(cacheDirectory, backup);
            }
            System.IO.Directory.Move(staging, cacheDirectory);
            TryDeleteDir(backup);
        }
        finally
        {
            TryDeleteDir(staging);
        }
    }

    public void SaveSyncState(DateTimeOffset when, string status, int? version)
    {
        try
        {
            System.IO.Directory.CreateDirectory(cacheDirectory);
            File.WriteAllText(Path.Combine(cacheDirectory, SyncStateFile),
                JsonSerializer.Serialize(new SyncStateDto(when, status, version)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* 状态写不进不影响功能 */ }
    }

    public (DateTimeOffset When, string Status, int? Version)? ReadSyncState()
    {
        try
        {
            var path = Path.Combine(cacheDirectory, SyncStateFile);
            if (!File.Exists(path)) return null;
            var dto = JsonSerializer.Deserialize<SyncStateDto>(File.ReadAllText(path));
            return dto is null ? null : (dto.When, dto.Status, dto.Version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (System.IO.Directory.Exists(dir)) System.IO.Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed record SyncStateDto(DateTimeOffset When, string Status, int? Version);
}
