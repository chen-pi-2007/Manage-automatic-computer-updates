namespace UpdateHelper.Core.Uninstall;

/// <summary>备份条目的种类：文件/目录（移入隔离区），或注册表项（导出为 .reg）。</summary>
public enum BackupEntryKind { FileOrDir, RegistryKey }

/// <summary>一条备份：原路径、在隔离区里的存放位置（相对备份目录）。</summary>
public sealed record BackupEntry(LeftoverType Type, string OriginalPath, string StoredPath, BackupEntryKind Kind);

/// <summary>一次卸载对应的备份清单。</summary>
public sealed record BackupManifest(string Id, DateTimeOffset Time, string SoftwareName, IReadOnlyList<BackupEntry> Entries);
