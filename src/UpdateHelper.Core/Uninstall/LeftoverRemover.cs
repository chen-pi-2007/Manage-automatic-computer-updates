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
        string? backupDir = null;   // 有真正要备份的项时才创建，避免留下空目录
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
                    backupDir ??= backups.NewBackupDirectory(id);
                    var stored = Path.Combine(backupDir, "files", Guid.NewGuid().ToString("N"));
                    if (mutator.MoveToBackup(item.Path, stored))
                    {
                        // 移入隔离区即"备份 + 删除"一步完成
                        entries.Add(new BackupEntry(item.Type, item.Path, stored, BackupEntryKind.FileOrDir));
                        removed++;
                    }
                    else if (mutator.ScheduleDeleteOnReboot(item.Path))
                    {
                        rebootPending++;   // 被占用：重启后删除（此情形没有备份）
                    }
                    else
                    {
                        failed.Add(item.Path);
                    }
                    break;

                case LeftoverType.RegistryKey:
                    if (SystemPaths.IsProtectedRegistryKey(item.Path))
                    {
                        failed.Add(item.Path);
                        break;
                    }
                    backupDir ??= backups.NewBackupDirectory(id);
                    var regFile = Path.Combine(backupDir, $"{regCount++}.reg");
                    // 导出失败就绝不删除：没有备份不动手
                    if (mutator.ExportRegistryKey(item.Path, regFile))
                    {
                        entries.Add(new BackupEntry(item.Type, item.Path, regFile, BackupEntryKind.RegistryKey));
                        if (mutator.DeleteRegistryKey(item.Path)) removed++;
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

    /// <summary>按清单恢复：文件移回原位、注册表导入 .reg；备份已丢失的项跳过，返回成功恢复的条数。</summary>
    public int Restore(BackupManifest manifest)
    {
        var restored = 0;
        foreach (var entry in manifest.Entries)
        {
            var ok = entry.Kind == BackupEntryKind.RegistryKey
                ? mutator.ImportRegistryFile(entry.StoredPath)
                : mutator.MoveBack(entry.StoredPath, entry.OriginalPath);
            if (ok) restored++;
        }
        return restored;
    }
}
