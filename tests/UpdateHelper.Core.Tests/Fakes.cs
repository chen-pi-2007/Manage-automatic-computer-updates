using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Tests;

/// <summary>快速构造测试数据。</summary>
public static class Fakes
{
    public static UninstallEntry Entry(
        string name,
        string? publisher = null,
        string? version = null,
        string? location = null,
        bool hidden = false,
        string? key = null,
        UninstallHive hive = UninstallHive.LocalMachine64,
        string? parentKey = null)
        => new(key ?? name, hive, name, version, publisher, location,
               UninstallString: null, QuietUninstallString: null,
               IsSystemComponent: hidden, ParentKeyName: parentKey, ReleaseType: null, EstimatedSizeKb: null);
}
