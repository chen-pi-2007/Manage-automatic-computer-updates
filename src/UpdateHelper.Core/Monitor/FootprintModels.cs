namespace UpdateHelper.Core.Monitor;

/// <summary>足迹可信度：确定（进程树内且落点明确）/ 可能（代办进程或落点模糊）。</summary>
public enum FootprintConfidence { Certain, Possible }

/// <summary>足迹条目类型。</summary>
public enum FootprintEntryType { File, Directory, RegistryKey, RegistryValue, Service, Driver }

/// <summary>一条软件足迹。VolumeSerial 仅文件类条目可能有值（可移动/非系统卷）。</summary>
public sealed record FootprintEntry(
    FootprintEntryType Type, string Path, string? VolumeSerial, FootprintConfidence Confidence,
    int Pid, string? Exe, DateTimeOffset FirstSeen, bool IsNew);

/// <summary>完整性：漏记必标。不完整时 Reasons 给出面向用户的中文原因。</summary>
public sealed record FootprintCompleteness(bool IsComplete, IReadOnlyList<string> Reasons);

/// <summary>一次监控的最终足迹。SoftwareKey 为树内新建的 Uninstall 键（无则 null）。</summary>
public sealed record MonitorFootprint(
    string Id, DateTimeOffset Start, DateTimeOffset End, int RootPid, string RootExe,
    string? SoftwareKey, FootprintCompleteness Completeness, IReadOnlyList<FootprintEntry> Entries);

/// <summary>收尾复验：真实现查磁盘/注册表，测试用假实现。</summary>
public interface IFootprintProbe
{
    bool StillExists(FootprintEntry e);
}
