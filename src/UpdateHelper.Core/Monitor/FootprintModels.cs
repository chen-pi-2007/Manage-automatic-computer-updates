namespace UpdateHelper.Core.Monitor;

/// <summary>足迹可信度：确定（进程树内且落点明确）/ 可能（代办进程或落点模糊）。</summary>
public enum FootprintConfidence { Certain, Possible }

/// <summary>足迹条目类型。</summary>
public enum FootprintEntryType { File, Directory, RegistryKey, RegistryValue, Service, Driver, ScheduledTask }

/// <summary>一条软件足迹。VolumeSerial 仅文件类条目可能有值（可移动/非系统卷）。</summary>
public sealed record FootprintEntry(
    FootprintEntryType Type, string Path, string? VolumeSerial, FootprintConfidence Confidence,
    int Pid, string? Exe, DateTimeOffset FirstSeen, bool IsNew);

/// <summary>完整性：漏记必标。不完整时 Reasons 给出面向用户的中文原因。</summary>
public sealed record FootprintCompleteness(bool IsComplete, IReadOnlyList<string> Reasons);

/// <summary>一次监控的最终足迹。SoftwareKey 为树内新建的 Uninstall 键（无则 null）。</summary>
/// <remarks>
/// EngineVersion：采集引擎版本。0（旧记录里没有这个字段）= 0.9.0 采集的，它的“新增”标记有已知缺陷
/// （写默认值被当成新建键、空 REG_NONE 值、改名覆盖已有文件），将来按足迹删除时必须拒绝使用。
/// </remarks>
public sealed record MonitorFootprint(
    string Id, DateTimeOffset Start, DateTimeOffset End, int RootPid, string RootExe,
    string? SoftwareKey, FootprintCompleteness Completeness, IReadOnlyList<FootprintEntry> Entries,
    int EngineVersion = 0)
{
    /// <summary>当前采集引擎版本。改了“新增”判定的语义就要加一。</summary>
    public const int CurrentEngineVersion = 2;
}

/// <summary>收尾复验：真实现查磁盘/注册表，测试用假实现。</summary>
public interface IFootprintProbe
{
    bool StillExists(FootprintEntry e);

    /// <summary>
    /// 这条“新建”的东西是不是其实在 since 之前就有了（真实现：文件的创建时间早于监控开始）。
    /// 兜的是“先写临时文件、再改名覆盖用户原有文件”：Windows 的文件名隧道会让覆盖后的文件继承原文件的创建时间。
    /// 判断不了时返回 false。
    /// </summary>
    bool ExistedBefore(FootprintEntry e, DateTimeOffset since) => false;
}

/// <summary>系统名册里的一项：服务 / 驱动 / 计划任务，用名字标识。</summary>
public sealed record InventoryItem(FootprintEntryType Type, string Name);

/// <summary>
/// 系统名册快照。服务、驱动、计划任务是由系统进程（服务控制管理器、任务计划服务）代写的，
/// 按进程树归不到安装器头上；但它们是数量有限的系统名册，用户不会在装软件的那几十秒里手建，
/// 所以在监控开始和结束各取一次、取差集，作为「可能」档足迹。真实现在 Agent，测试用假实现。
/// </summary>
public interface ISystemInventory
{
    IReadOnlyCollection<InventoryItem> Snapshot();
}
