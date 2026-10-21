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
        var dir = SafeDir(id) ?? throw new ArgumentException("备份 Id 不合法", nameof(id));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>只读：按 Id 取备份目录路径（不创建）；Id 不合法返回 null。</summary>
    public string? GetBackupDirectory(string id) => SafeDir(id);

    /// <summary>把 id 解析为 root 之下的隔离目录；id 为空、含路径分隔符或 ..、或解析后不在 root 之内则返回 null。</summary>
    private string? SafeDir(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (id.Contains("..") || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;   // 无效文件名字符已含 / \ :

        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(fullRoot, id));
        if (string.Equals(full, fullRoot, StringComparison.OrdinalIgnoreCase)) return null;
        return full.StartsWith(fullRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
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
                if (JsonSerializer.Deserialize<BackupManifest>(line, Options) is { Id: not null and not "" } manifest) result.Add(manifest);
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

        var drop = new HashSet<BackupManifest>(ReferenceEqualityComparer.Instance);
        var removed = 0;
        foreach (var m in expired)
        {
            var dir = SafeDir(m.Id);
            if (dir is null)
            {
                drop.Add(m);   // Id 不合法：本就没有合法目录，只清清单行，绝不碰磁盘
                continue;
            }
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                drop.Add(m);
                removed++;
            }
            catch (IOException)
            {
                // 删不掉（被占用）：保留清单行，下次启动重试
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        // 保留的清单按时间正序重写（含删失败待重试的），保持追加文件的自然顺序
        var keep = all.Where(m => !drop.Contains(m)).OrderBy(m => m.Time);
        var text = string.Concat(keep.Select(m => JsonSerializer.Serialize(m, Options) + "\n"));
        File.WriteAllText(ManifestPath, text, new UTF8Encoding(false));
        return removed;
    }
}
