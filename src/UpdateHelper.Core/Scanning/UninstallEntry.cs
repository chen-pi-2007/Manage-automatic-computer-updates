namespace UpdateHelper.Core.Scanning;

/// <summary>卸载信息所在的注册表位置。</summary>
public enum UninstallHive
{
    /// <summary>HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall</summary>
    LocalMachine64,
    /// <summary>HKLM\SOFTWARE\WOW6432Node\...\Uninstall（32 位软件）</summary>
    LocalMachine32,
    /// <summary>HKCU\...\Uninstall（只给当前用户装的软件）</summary>
    CurrentUser,
}

/// <summary>注册表里的一条卸载登记。</summary>
public sealed record UninstallEntry(
    string KeyName,
    UninstallHive Hive,
    string DisplayName,
    string? DisplayVersion,
    string? Publisher,
    string? InstallLocation,
    string? UninstallString,
    string? QuietUninstallString,
    bool IsSystemComponent,
    string? ParentKeyName,
    string? ReleaseType,
    long? EstimatedSizeKb)
{
    /// <summary>是不是某个软件的补丁或更新包（不是独立软件）。</summary>
    public bool IsUpdateOrPatch => ParentKeyName is not null || ReleaseType is not null;
}
