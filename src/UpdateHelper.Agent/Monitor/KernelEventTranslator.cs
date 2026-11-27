using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Agent.Monitor;

/// <summary>
/// 把内核事件的原始字段翻译成 Core 认识的 MonitorEvent。纯逻辑：不碰 ETW 类型，字段由 EtwMonitorSession 取好再传进来，
/// 所以“哪种情况算新建、改名怎么找旧名、注册表路径怎么拼”都能脱离系统单测。
/// 只翻译，不做归属 / 落点 / 净效果（那些在 Core）。事件按到达顺序在同一线程上调用。
/// </summary>
public sealed class KernelEventTranslator(
    Func<string, (string Path, string? VolumeSerial)> resolveFilePath,
    string? currentUserSid,
    Func<int, string> imagePathOf)
{
    // 文件对象 → 打开时的路径。只为给“改名”找旧名：改名事件只带新路径和文件对象，旧名得从它之前的打开事件里查。
    // 两代轮换（当前代写满就丢掉上一代），不整表清空：避免清表恰好落在“打开”和“改名”之间而查不到旧名。
    // 真查不到时 OldPath 为 null，Core 会把足迹标不完整。
    private const int FileGenerationSize = 100_000;
    private Dictionary<ulong, string> _openFiles = new();
    private Dictionary<ulong, string> _openFilesPrev = new();
    private readonly RegistryObjectTable _keys = new();

    // FILE_DIRECTORY_FILE：这是在建目录。目录本身不进足迹，只用来判断改名目标是否落在本次新建的目录里
    private const uint FileDirectoryFile = 0x1;
    // REG_CREATED_NEW_KEY：这次调用真的新建了键（2 = REG_OPENED_EXISTING_KEY，只是打开了已有的）
    private const int RegCreatedNewKey = 1;

    public void FileOpened(ulong fileObject, string? ntPath)
    {
        if (fileObject == 0 || string.IsNullOrEmpty(ntPath)) return;
        if (_openFiles.Count >= FileGenerationSize && !_openFiles.ContainsKey(fileObject))
        {
            _openFilesPrev = _openFiles;
            _openFiles = new Dictionary<ulong, string>();
        }
        _openFiles[fileObject] = ntPath;
        _openFilesPrev.Remove(fileObject);
    }

    private string? OpenedPath(ulong fileObject) =>
        _openFiles.TryGetValue(fileObject, out var p) || _openFilesPrev.TryGetValue(fileObject, out p) ? p : null;

    /// <summary>
    /// 内核只在磁盘上真的新建了文件（或目录）时才发这个事件；打开已有的不会走到这里。
    /// 目录也产出（IsDirectory=true）：Core 不把它记进足迹，只用来判断改名目标是否落在本次新建的目录里。
    /// </summary>
    public MonitorEvent? FileCreatedNew(int pid, string? ntPath, uint createOptions, DateTimeOffset time)
    {
        if (string.IsNullOrEmpty(ntPath)) return null;
        var (path, serial) = resolveFilePath(ntPath);
        return new FileEvent(pid, path, IsCreate: true, time, serial, IsDirectory: (createOptions & FileDirectoryFile) != 0);
    }

    public MonitorEvent? FileDeleted(int pid, string? ntPath, DateTimeOffset time)
    {
        if (string.IsNullOrEmpty(ntPath)) return null;
        var (path, serial) = resolveFilePath(ntPath);
        return new FileEvent(pid, path, IsCreate: false, time, serial);
    }

    public MonitorEvent? FileRenamed(int pid, ulong fileObject, string? newNtPath, DateTimeOffset time)
    {
        if (string.IsNullOrEmpty(newNtPath)) return null;
        string? oldPath = null;
        if (fileObject != 0 && OpenedPath(fileObject) is { } oldNt)
            oldPath = resolveFilePath(oldNt).Path;
        if (fileObject != 0) FileOpened(fileObject, newNtPath);   // 同一个句柄可能接着再改名
        var (path, serial) = resolveFilePath(newNtPath);
        return new FileRenameEvent(pid, oldPath, path, time, serial);
    }

    /// <summary>
    /// 打开或新建键。每次都要记进对象表（后面的写值靠它找路径）；只有 disposition 表明“真的新建了”才产出事件。
    /// 新建了但拼不出路径：产出空路径的事件，让 Core 把足迹标不完整，而不是悄悄漏掉。
    /// </summary>
    public MonitorEvent? KeyOpenedOrCreated(int pid, ulong baseObject, ulong keyObject, int status, int disposition,
        string? baseName, string? relativeName, DateTimeOffset time)
    {
        var full = _keys.Record(baseObject, keyObject, status, baseName, relativeName);
        if (status != 0 || disposition != RegCreatedNewKey) return null;
        return new RegistryEvent(pid, ToHivePath(full), null, MonitorEventKind.RegistryCreate, time);
    }

    /// <summary>
    /// 写值。旧值类型和大小都是 0 表示这个值之前不存在（新值）；否则是改写已有的值。
    /// 例外：新写的也是空的 REG_NONE（类型 0、大小 0）时，“原本不存在”和“原本就是个空 REG_NONE”分不清，标为不确定。
    /// 值名 null 或空串都是键的默认值，统一传空串——它是值，不是键。
    /// </summary>
    public MonitorEvent? ValueSet(int pid, ulong keyObject, int status, string? valueName,
        int newType, int newSize, int previousType, int previousSize, DateTimeOffset time)
    {
        if (status != 0) return null;
        var key = _keys.Resolve(keyObject);
        // 后台活动调节器在每个进程退出时，用内核自己打开的键记一笔“最后运行时间”，值名是程序的设备路径。
        // 那个键不是进程打开的、查不到路径，也不是软件的改动：键解析不出且值名是设备路径时才丢，免得被当成“没解析出的注册表改动”
        if (key is null && valueName is not null && valueName.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)) return null;
        var isNew = previousType == 0 && previousSize == 0;
        return new RegistryEvent(pid, ToHivePath(key), valueName ?? "", MonitorEventKind.RegistrySet, time,
            IsNew: isNew, IsNewUncertain: isNew && newType == 0 && newSize == 0);
    }

    public MonitorEvent? ValueDeleted(int pid, ulong keyObject, int status, string? valueName, DateTimeOffset time) =>
        status != 0 ? null
            : new RegistryEvent(pid, ToHivePath(_keys.Resolve(keyObject)), valueName ?? "", MonitorEventKind.RegistryDelete, time);

    public MonitorEvent? KeyDeleted(int pid, ulong keyObject, int status, DateTimeOffset time) =>
        status != 0 ? null
            : new RegistryEvent(pid, ToHivePath(_keys.Resolve(keyObject)), null, MonitorEventKind.RegistryDelete, time);

    public MonitorEvent ProcessStarted(int pid, int parentPid, DateTimeOffset time) =>
        new ProcessStartEvent(pid, parentPid, imagePathOf(pid), time);

    public MonitorEvent ProcessStopped(int pid, DateTimeOffset time) => new ProcessStopEvent(pid, time);

    private string ToHivePath(string? kernelPath) => RegPath(kernelPath, currentUserSid);

    /// <summary>
    /// 内核注册表路径 \REGISTRY\MACHINE\... → HKLM\...；\REGISTRY\USER\&lt;当前用户SID&gt;\... → HKCU\...；
    /// 其他用户 → HKU\&lt;sid&gt;\...。null/空 → 空串（表示没解析出来）。
    /// </summary>
    public static string RegPath(string? kernelPath, string? currentUserSid)
    {
        if (string.IsNullOrEmpty(kernelPath)) return "";
        var p = kernelPath.TrimStart('\\');
        if (p.StartsWith("REGISTRY\\", StringComparison.OrdinalIgnoreCase)) p = p["REGISTRY\\".Length..];

        if (p.StartsWith("MACHINE", StringComparison.OrdinalIgnoreCase) && (p.Length == 7 || p[7] == '\\'))
            return "HKLM" + p[7..];

        if (p.StartsWith("USER\\", StringComparison.OrdinalIgnoreCase))
        {
            var rest = p["USER\\".Length..];
            if (currentUserSid is not null && rest.StartsWith(currentUserSid, StringComparison.OrdinalIgnoreCase)
                && (rest.Length == currentUserSid.Length || rest[currentUserSid.Length] == '\\'))
                return "HKCU" + rest[currentUserSid.Length..];
            return "HKU\\" + rest;
        }
        return p;   // 认不出的形式原样交给 Core，不瞎猜
    }
}
