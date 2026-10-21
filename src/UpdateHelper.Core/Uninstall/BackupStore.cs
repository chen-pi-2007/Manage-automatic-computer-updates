using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateHelper.Core.Uninstall;

/// <summary>
/// 备份清单与隔离区：清单为 root/manifests.jsonl（每行一个 JSON，追加写入，坏行读取时跳过），
/// 被移走的文件放在 root/&lt;id&gt;/ 下，过期后整体清理。
/// </summary>
public sealed class BackupStore(string root, Func<DateTimeOffset> now)
{
    public static string DefaultRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHelper", "backups");

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文原样写入，文件可直接阅读
        Converters = { new JsonStringEnumConverter() },
    };

    private string ManifestPath => Path.Combine(root, "manifests.jsonl");

    /// <summary>在 root/&lt;id&gt;/ 建隔离目录并返回其路径。</summary>
    public string NewBackupDirectory(string id)
    {
        var dir = Path.Combine(root, id);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void Save(BackupManifest manifest)
    {
        Directory.CreateDirectory(root);
        File.AppendAllText(ManifestPath, JsonSerializer.Serialize(manifest, Options) + "\n", new UTF8Encoding(false));
    }

    /// <summary>读取全部清单，按时间倒序；坏行跳过。</summary>
    public IReadOnlyList<BackupManifest> ReadAll()
    {
        if (!File.Exists(ManifestPath)) return [];

        var result = new List<BackupManifest>();
        foreach (var line in File.ReadLines(ManifestPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<BackupManifest>(line, Options) is { } manifest) result.Add(manifest);
            }
            catch (JsonException)
            {
                // 坏行跳过
            }
        }
        return result.OrderByDescending(m => m.Time).ToList();
    }

    public BackupManifest? Find(string id) => ReadAll().FirstOrDefault(m => m.Id == id);

    /// <summary>删除早于保留期的备份（隔离目录 + 清单行），返回删除条数。</summary>
    public int PruneExpired(TimeSpan retention)
    {
        var all = ReadAll();
        var cutoff = now() - retention;
        var expired = all.Where(m => m.Time < cutoff).ToList();
        if (expired.Count == 0) return 0;

        foreach (var m in expired)
        {
            var dir = Path.Combine(root, m.Id);
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch (IOException)
            {
                // 删不掉（被占用）就留着，下次启动再清；清单行仍剔除以免无限堆积
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // 保留的清单按时间正序重写，保持追加文件的自然顺序
        var keep = all.Where(m => m.Time >= cutoff).OrderBy(m => m.Time);
        var text = string.Concat(keep.Select(m => JsonSerializer.Serialize(m, Options) + "\n"));
        File.WriteAllText(ManifestPath, text, new UTF8Encoding(false));
        return expired.Count;
    }
}
