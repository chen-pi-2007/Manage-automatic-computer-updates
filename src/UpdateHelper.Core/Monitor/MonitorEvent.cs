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

/// <summary>文件事件；IsCreate=false 表示删除。VolumeSerial 由 Agent 侧适配器为可移动/非系统卷填充，其余为 null。</summary>
public sealed record FileEvent(int Pid, string Path, bool IsCreate, DateTimeOffset Time, string? VolumeSerial = null)
    : MonitorEvent(Pid, Time);

/// <summary>注册表事件；Kind 取 RegistryCreate / RegistrySet / RegistryDelete。</summary>
public sealed record RegistryEvent(int Pid, string KeyPath, string? ValueName, MonitorEventKind Kind, DateTimeOffset Time)
    : MonitorEvent(Pid, Time);

/// <summary>服务安装或驱动加载。</summary>
public sealed record ServiceOrDriverEvent(int Pid, string Name, string ImagePath, bool IsDriver, DateTimeOffset Time)
    : MonitorEvent(Pid, Time);
