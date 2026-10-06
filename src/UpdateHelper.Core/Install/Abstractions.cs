namespace UpdateHelper.Core.Install;

/// <summary>能把一个包升级到最新版的安装器（如 winget）。</summary>
public interface IPackageInstaller
{
    string Name { get; }

    /// <summary>
    /// 静默升级到 targetVersion（判断层评估过的那个版本）。可用版本已经变了就返回失败，不能装别的版本。
    /// 出错可以抛异常，也可以返回 Success=false；取消时抛 OperationCanceledException。
    /// </summary>
    Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
        IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>重新读取某个卸载登记的当前版本，用于装后核对。</summary>
public interface IVersionProbe
{
    /// <summary>键不存在时返回 null。</summary>
    string? GetInstalledVersion(string uninstallKeyName);
}

/// <summary>找出程序文件位于给定目录内、正在运行的进程。</summary>
public interface IRunningProcessProbe
{
    /// <summary>返回去重后的进程名（如 "QQ.exe"）。</summary>
    IReadOnlyList<string> FindRunningUnder(IReadOnlyList<string> directories);
}

/// <summary>更新历史。</summary>
public interface IUpdateHistory
{
    void Append(HistoryRecord record);
    IReadOnlyList<HistoryRecord> ReadAll();
}
