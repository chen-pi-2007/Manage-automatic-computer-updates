using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateHelper.Core.Uninstall;

/// <summary>
/// 备份清单与隔离区：清单为 root/manifests.jsonl（每行一个 JSON，坏行读取时跳过），
/// 被移走的文件放在 root/&lt;id&gt;/ 下，过期后整体清理。
/// 所有清单文件访问（读、写、整文件重写）都走一把按清单路径命名的跨进程 Mutex，
/// 让 App 与提权的后台助手彼此串行；任何整文件重写都先写临时文件再原子改名，绝不留半截清单。
/// </summary>
public class BackupStore(string root, Func<DateTimeOffset> now)
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

    /// <summary>追加/更新一条清单：等价于 <see cref="SaveOrReplace"/>（同 Id 覆盖，新 Id 追加）。</summary>
    public void Save(BackupManifest manifest) => SaveOrReplace(manifest);

    /// <summary>
    /// 按 Id 幂等写入一条清单：加跨进程锁后读出全部，替换同 Id 的那条（没有则追加），再整文件原子重写。
    /// 供残留清理"边删边持久化"逐项落盘用。清单很小，每次 O(n) 重写可接受。
    /// </summary>
    public virtual void SaveOrReplace(BackupManifest manifest)
    {
        WithLock(() =>
        {
            var all = ReadAllCore();
            var idx = all.FindIndex(m => m.Id == manifest.Id);
            if (idx >= 0) all[idx] = manifest; else all.Add(manifest);
            AtomicRewrite(all);
        });
    }

    /// <summary>读取全部清单，按时间倒序；坏行跳过。</summary>
    public IReadOnlyList<BackupManifest> ReadAll() =>
        WithLock(() => (IReadOnlyList<BackupManifest>)ReadAllCore().OrderByDescending(m => m.Time).ToList());

    public BackupManifest? Find(string id) => ReadAll().FirstOrDefault(m => m.Id == id);

    /// <summary>删除早于保留期的备份（隔离目录 + 清单行），返回删除条数。</summary>
    public int PruneExpired(TimeSpan retention)
    {
        return WithLock(() =>
        {
            var all = ReadAllCore();
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
            var keep = all.Where(m => !drop.Contains(m)).OrderBy(m => m.Time).ToList();
            AtomicRewrite(keep);
            return removed;
        });
    }

    /// <summary>不加锁的原始读取（文件追加顺序，不排序）；调用方负责持锁。坏行/半截行跳过。</summary>
    private List<BackupManifest> ReadAllCore()
    {
        var result = new List<BackupManifest>();
        if (!File.Exists(ManifestPath)) return result;

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
        return result;
    }

    /// <summary>原子整文件重写：先写同目录临时文件并刷盘，再改名覆盖，绝不留半截清单。调用方负责持锁。</summary>
    private void AtomicRewrite(IReadOnlyList<BackupManifest> manifests)
    {
        Directory.CreateDirectory(root);
        var bytes = Encoding.UTF8.GetBytes(string.Concat(manifests.Select(m => JsonSerializer.Serialize(m, Options) + "\n")));
        var temp = ManifestPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, ManifestPath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* 临时文件清不掉就算了 */ }
            throw;
        }
    }

    /// <summary>按清单路径生成一把会话内命名 Mutex 的名字，让同一会话里的 App 与提权助手串行访问清单。</summary>
    private string MutexName()
    {
        var key = Path.GetFullPath(ManifestPath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return "UpdateHelper-backup-" + hash[..32];   // 不加 Global\：标准用户建不了全局对象，会话命名空间已覆盖 UAC 提权的助手
    }

    private void WithLock(Action body) => WithLock(() => { body(); return true; });

    private T WithLock<T>(Func<T> body)
    {
        using var mutex = new Mutex(false, MutexName());
        var held = false;
        try
        {
            try { held = mutex.WaitOne(); }
            catch (AbandonedMutexException) { held = true; }   // 上个持有者异常退出，锁已归我们，照常继续
            return body();
        }
        finally
        {
            if (held) mutex.ReleaseMutex();
        }
    }
}
