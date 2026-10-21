namespace UpdateHelper.Core.Uninstall;

/// <summary>
/// 一次删除的结果：已删除数、排到重启后删除的数、失败/跳过的项（路径）、本次备份 Id（没有任何备份则为 null）；
/// QuarantinePath 仅在"源已移入隔离区、但恢复记录落盘失败"时给出该隔离目录，供用户手动找回（正常时为 null）。
/// </summary>
public sealed record RemovalResult(
    int Removed, int RebootPending, IReadOnlyList<string> Failed, string? BackupId, string? QuarantinePath = null);
