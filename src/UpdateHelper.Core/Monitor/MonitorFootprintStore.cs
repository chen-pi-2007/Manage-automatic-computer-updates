using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateHelper.Core.Monitor;

/// <summary>
/// 足迹存储：root/footprints.jsonl，每行一个 <see cref="MonitorFootprint"/>（坏行读取时跳过）。
/// 机制照搬 BackupStore：所有访问走一把按文件路径命名的跨进程 Mutex，让 App 与提权助手串行；
/// 整文件重写先写临时文件并刷盘再原子改名，绝不留半截文件。
/// 注意：目录的 ACL（管理员可写、普通用户只读）由启用监控模块时的提权助手建立（后续任务），
/// 本类只负责读写，不设置任何 ACL。
/// </summary>
public class MonitorFootprintStore(string root, Func<DateTimeOffset> now)
{
    /// <summary>受保护区：%ProgramData%\UpdateHelper\footprints（普通用户无法篡改的位置，防止足迹被伪造）。</summary>
    public static string ProtectedRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UpdateHelper", "footprints");

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文原样写入，文件可直接阅读
        Converters = { new JsonStringEnumConverter() },
    };

    private string FilePath => Path.Combine(root, "footprints.jsonl");

    /// <summary>按 Id 写入一条足迹：同 Id 覆盖，新 Id 追加；整文件原子重写。</summary>
    public void Save(MonitorFootprint f)
    {
        WithLock(() =>
        {
            var all = ReadAllCore();
            var idx = all.FindIndex(x => x.Id == f.Id);
            if (idx >= 0) all[idx] = f; else all.Add(f);
            AtomicRewrite(all);
        });
    }

    /// <summary>读取全部足迹，按开始时间倒序；坏行跳过。</summary>
    public IReadOnlyList<MonitorFootprint> ReadAll() =>
        WithLock(() => (IReadOnlyList<MonitorFootprint>)ReadAllCore().OrderByDescending(x => x.Start).ToList());

    public MonitorFootprint? Find(string id) => ReadAll().FirstOrDefault(x => x.Id == id);

    /// <summary>删除早于保留期（按结束时间 End 算）的足迹，返回删除条数。</summary>
    public int PruneExpired(TimeSpan retention)
    {
        return WithLock(() =>
        {
            var all = ReadAllCore();
            var cutoff = now() - retention;
            var keep = all.Where(x => x.End >= cutoff).ToList();
            var removed = all.Count - keep.Count;
            if (removed == 0) return 0;
            AtomicRewrite(keep);
            return removed;
        });
    }

    /// <summary>不加锁的原始读取（文件追加顺序）；调用方负责持锁。坏行/半截行/缺 Id 的行跳过。</summary>
    private List<MonitorFootprint> ReadAllCore()
    {
        var result = new List<MonitorFootprint>();
        if (!File.Exists(FilePath)) return result;

        foreach (var line in File.ReadLines(FilePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<MonitorFootprint>(line, Options) is { Id: not null and not "" } f) result.Add(f);
            }
            catch (JsonException)
            {
                // 坏行跳过
            }
        }
        return result;
    }

    /// <summary>原子整文件重写：先写同目录临时文件并刷盘，再改名覆盖。调用方负责持锁。</summary>
    private void AtomicRewrite(IReadOnlyList<MonitorFootprint> items)
    {
        Directory.CreateDirectory(root);
        var bytes = Encoding.UTF8.GetBytes(string.Concat(items.Select(x => JsonSerializer.Serialize(x, Options) + "\n")));
        var temp = FilePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            File.Move(temp, FilePath, overwrite: true);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { /* 临时文件清不掉就算了 */ }
            throw;
        }
    }

    /// <summary>按文件路径生成会话内命名 Mutex 的名字，让同一会话里的 App 与提权助手串行访问。</summary>
    private string MutexName()
    {
        var key = Path.GetFullPath(FilePath).ToLowerInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return "UpdateHelper-footprint-" + hash[..32];   // 不加 Global\：标准用户建不了全局对象，会话命名空间已覆盖 UAC 提权的助手
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
