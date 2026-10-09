namespace UpdateHelper.Core.Uninstall;

/// <summary>残留的类型。</summary>
public enum LeftoverType { InstallDir, DataDir, File, RegistryKey, Service, ScheduledTask, StartupEntry }

/// <summary>残留归属判断（spec 第 7 节）。</summary>
public enum LeftoverCategory { Owned, Possible, Shared, Personal }

/// <summary>一条残留。SizeBytes 为 null 表示大小未知；Note 是给用户看的中文提示/警告。</summary>
public sealed record LeftoverItem(
    LeftoverType Type,
    string Path,
    LeftoverCategory Category,
    long? SizeBytes,
    bool DefaultChecked,
    string? Note);
