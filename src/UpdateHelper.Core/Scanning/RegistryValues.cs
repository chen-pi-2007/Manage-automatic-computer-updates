using System.Globalization;
using System.Security;

namespace UpdateHelper.Core.Scanning;

/// <summary>把注册表里类型不可靠的值安全地转换成需要的类型；转换不了就当作缺失。</summary>
public static class RegistryValues
{
    public static string? AsString(object? value) => value switch
    {
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        int or long or uint or ulong => Convert.ToString(value, CultureInfo.InvariantCulture),
        _ => null,
    };

    public static long? AsLong(object? value) => value switch
    {
        int i => i,
        long l => l,
        uint u => u,
        string s when long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => null,
    };

    public static bool AsFlag(object? value) => AsLong(value) is { } n && n != 0;

    /// <summary>
    /// 用 getValue（按值名取原始值）组装一条卸载登记。
    /// 没有 DisplayName 的键不算软件，返回 null；键不可读（权限不足等）也返回 null。
    /// </summary>
    public static UninstallEntry? ToEntry(string keyName, UninstallHive hive, Func<string, object?> getValue)
    {
        try
        {
            var name = AsString(getValue("DisplayName"));
            if (name is null) return null;

            return new UninstallEntry(
                KeyName: keyName,
                Hive: hive,
                DisplayName: name,
                DisplayVersion: AsString(getValue("DisplayVersion")),
                Publisher: AsString(getValue("Publisher")),
                InstallLocation: AsString(getValue("InstallLocation")),
                UninstallString: AsString(getValue("UninstallString")),
                QuietUninstallString: AsString(getValue("QuietUninstallString")),
                IsSystemComponent: AsFlag(getValue("SystemComponent")),
                ParentKeyName: AsString(getValue("ParentKeyName")),
                ReleaseType: AsString(getValue("ReleaseType")),
                EstimatedSizeKb: AsLong(getValue("EstimatedSize")));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            return null;
        }
    }
}
