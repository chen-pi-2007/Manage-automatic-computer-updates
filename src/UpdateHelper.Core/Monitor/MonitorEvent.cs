namespace UpdateHelper.Core.Monitor;

/// <summary>监控事件种类。</summary>
public enum MonitorEventKind
{
    FileCreate, FileDelete,
    RegistryCreate, RegistrySet, RegistryDelete,
    ProcessStart, ProcessStop,
    ServiceInstall, DriverLoad,
}

/// <summary>
/// 监控事件的纯数据基类。所有监控逻辑只依赖这些记录，不依赖 ETW，
/// 这样归属判定、足迹累积都能脱离系统 API 单元测试。
/// </summary>
public abstract record MonitorEvent(int Pid, DateTimeOffset Time);

/// <summary>进程启动（带父 PID 与映像路径，用于建进程树）。</summary>
public sealed record ProcessStartEvent(int Pid, int ParentPid, string ImagePath, DateTimeOffset Time)
    : MonitorEvent(Pid, Time);

/// <summary>进程退出（用于界定 PID 化身的存活区间）。</summary>
public sealed record ProcessStopEvent(int Pid, DateTimeOffset Time) : MonitorEvent(Pid, Time);

/// <summary>
/// 文件事件。IsCreate=true 表示【磁盘上真的新建了一个文件】（不是"打开时带了可新建的标志"——
/// 打开已有文件绝不能算新建，否则用户原有的文件会被当成软件的）；IsCreate=false 表示删除。
/// VolumeSerial 由 Agent 侧适配器为可移动/非系统卷填充，其余为 null。
/// </summary>
/// <remarks>
/// IsDirectory：这是目录。目录本身不进足迹；记下"本次新建的目录"只为一件事——判断改名的目标原来是否存在：
/// 落在本次新建目录里的东西不可能是用户原有的。
/// </remarks>
public sealed record FileEvent(int Pid, string Path, bool IsCreate, DateTimeOffset Time, string? VolumeSerial = null,
    bool IsDirectory = false)
    : MonitorEvent(Pid, Time);

/// <summary>
/// 文件（或目录）改名/移动。"先写临时名再改成最终名"是安装器最常见的写法，
/// 不跟着改名走，最终文件就会漏记。OldPath 查不到时为 null（此时新路径只能算"动过"，不算新建）。
/// </summary>
public sealed record FileRenameEvent(int Pid, string? OldPath, string NewPath, DateTimeOffset Time, string? VolumeSerial = null)
    : MonitorEvent(Pid, Time);

/// <summary>
/// 注册表事件；Kind 取 RegistryCreate / RegistrySet / RegistryDelete。
/// RegistryCreate 只表示【真的新建了键】（打开已有键不发）；RegistrySet 的 IsNew 表示该值之前不存在。
/// KeyPath 为空表示采集侧没能确定这次改动落在哪个键上（Core 据此标不完整，不瞎猜）。
/// </summary>
/// <remarks>
/// ValueName 为 null 表示这是针对【键】的事件；空串 "" 表示键的【默认值】（它是一个值，不是键——
/// 写默认值绝不能被当成“新建了这个键”，否则删除时会连同用户原有的子键一起删掉）。
/// IsNewUncertain：写值前的旧值类型和大小都是 0，而新写的也是空的 REG_NONE——“原本不存在”与
/// “原本就是个空的 REG_NONE 值”无法区分（文件关联的 OpenWithProgids 全是这种），只有所在的键是本次新建的才算新值。
/// </remarks>
public sealed record RegistryEvent(int Pid, string KeyPath, string? ValueName, MonitorEventKind Kind, DateTimeOffset Time,
    bool IsNew = false, bool IsNewUncertain = false)
    : MonitorEvent(Pid, Time);

/// <summary>服务安装或驱动加载。</summary>
public sealed record ServiceOrDriverEvent(int Pid, string Name, string ImagePath, bool IsDriver, DateTimeOffset Time)
    : MonitorEvent(Pid, Time);
