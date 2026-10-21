namespace UpdateHelper.Core.Uninstall;

/// <summary>残留的删除与恢复变更器。所有方法失败都返回 false，不抛异常。</summary>
public interface ILeftoverMutator
{
    /// <summary>把文件/目录移动到隔离区。成功返回 true。</summary>
    bool MoveToBackup(string originalPath, string storedPath);

    /// <summary>复制（不移动）文件/目录到隔离区：被占用但可读的文件也能复制成功。失败返回 false 且清理半成品。</summary>
    bool CopyToBackup(string originalPath, string storedPath);

    /// <summary>被占用无法移动时，排到下次重启时删除。</summary>
    bool ScheduleDeleteOnReboot(string path);

    /// <summary>把注册表键导出为 .reg 文件。keyPath 形如 HKCU\Software\Foo。</summary>
    bool ExportRegistryKey(string keyPath, string storedRegFile);

    /// <summary>删除注册表键（含子键）。</summary>
    bool DeleteRegistryKey(string keyPath);

    /// <summary>恢复：把隔离区的文件/目录移回原位置。</summary>
    bool MoveBack(string storedPath, string originalPath);

    /// <summary>恢复：导入 .reg 文件。</summary>
    bool ImportRegistryFile(string storedRegFile);
}
