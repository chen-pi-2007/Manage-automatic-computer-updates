namespace UpdateHelper.Core.Uninstall;

/// <summary>
/// 残留删除编排：对每个选中项先备份后删除，受系统白名单保护的项一律跳过，
/// 被占用的文件排到重启后删除；全部完成后写一条备份清单，供之后恢复。
/// </summary>
public sealed class LeftoverRemover(ILeftoverMutator mutator, BackupStore backups, Func<DateTimeOffset> now)
{
    public RemovalResult Remove(string softwareName, IReadOnlyList<LeftoverItem> selected)
    {
        var id = Guid.NewGuid().ToString("N");
        // 只解析隔离目录路径、不在磁盘上创建：真正写入由变更器按需建父目录，全部失败时就不会留下空目录
        var backupDir = backups.GetBackupDirectory(id)
            ?? throw new InvalidOperationException("备份目录 Id 不合法");
        var entries = new List<BackupEntry>();
        var failed = new List<string>();
        var removed = 0;
        var rebootPending = 0;
        var regCount = 0;

        foreach (var item in selected)
        {
            switch (item.Type)
            {
                case LeftoverType.InstallDir or LeftoverType.DataDir or LeftoverType.File:
                    if (SystemPaths.IsProtectedDirectory(item.Path))
                    {
                        failed.Add(item.Path);
                        break;
                    }
                    var rel = Path.Combine("files", Guid.NewGuid().ToString("N"));   // 清单里存相对路径
                    var stored = Path.Combine(backupDir, rel);
                    if (mutator.MoveToBackup(item.Path, stored))
                    {
                        // 移入隔离区即"备份 + 删除"一步完成
                        entries.Add(new BackupEntry(item.Type, item.Path, rel, BackupEntryKind.FileOrDir));
                        removed++;
                    }
                    else if (mutator.CopyToBackup(item.Path, stored))
                    {
                        // 移不动（多半被占用）但能复制：备份已就位，再排重启后删除，仍可恢复
                        entries.Add(new BackupEntry(item.Type, item.Path, rel, BackupEntryKind.FileOrDir));
                        if (mutator.ScheduleDeleteOnReboot(item.Path)) rebootPending++;
                        else failed.Add(item.Path);
                    }
                    else
                    {
                        failed.Add(item.Path);   // 连备份都做不了：绝不删除
                    }
                    break;

                case LeftoverType.RegistryKey:
                    if (SystemPaths.IsProtectedRegistryKey(item.Path))
                    {
                        failed.Add(item.Path);
                        break;
                    }
                    var regRel = $"{regCount++}.reg";
                    // 导出失败就绝不删除：没有备份不动手；删除成功后才记入清单，避免留下"其实没删"的幽灵项
                    if (mutator.ExportRegistryKey(item.Path, Path.Combine(backupDir, regRel)))
                    {
                        if (mutator.DeleteRegistryKey(item.Path))
                        {
                            entries.Add(new BackupEntry(item.Type, item.Path, regRel, BackupEntryKind.RegistryKey));
                            removed++;
                        }
                        else failed.Add(item.Path);
                    }
                    else
                    {
                        failed.Add(item.Path);
                    }
                    break;

                default:
                    // 服务/计划任务/启动项本阶段不支持：明确记为失败，不静默丢弃
                    failed.Add(item.Path);
                    break;
            }
        }

        if (entries.Count == 0) return new RemovalResult(removed, rebootPending, failed, null);

        backups.Save(new BackupManifest(id, now(), softwareName, entries));
        return new RemovalResult(removed, rebootPending, failed, id);
    }

    /// <summary>按清单恢复：文件移回原位、注册表导入 .reg；StoredPath 相对备份目录解析，
    /// 备份已丢失或路径越出备份目录的项跳过，返回成功恢复的条数。</summary>
    public int Restore(BackupManifest manifest)
    {
        var dir = backups.GetBackupDirectory(manifest.Id);
        if (dir is null) return 0;

        var restored = 0;
        foreach (var entry in manifest.Entries)
        {
            var stored = ResolveInside(dir, entry.StoredPath);
            if (stored is null) continue;
            var ok = entry.Kind == BackupEntryKind.RegistryKey
                ? mutator.ImportRegistryFile(stored)
                : mutator.MoveBack(stored, entry.OriginalPath);
            if (ok) restored++;
        }
        return restored;
    }

    /// <summary>把相对路径解析到备份目录之内；绝对路径或越界（..）返回 null。</summary>
    private static string? ResolveInside(string dir, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) return null;
        var full = Path.GetFullPath(Path.Combine(dir, relative));
        var prefix = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
