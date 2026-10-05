using System.Security;
using Microsoft.Win32;

namespace UpdateHelper.Core.Scanning;

/// <summary>从 3 处注册表读取卸载登记（包括隐藏条目）。只读。</summary>
public sealed class RegistryUninstallSource : IUninstallSource
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<UninstallEntry> Read()
    {
        var result = new List<UninstallEntry>();
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry64, UninstallHive.LocalMachine64, result);
        ReadHive(RegistryHive.LocalMachine, RegistryView.Registry32, UninstallHive.LocalMachine32, result);
        ReadHive(RegistryHive.CurrentUser, RegistryView.Default, UninstallHive.CurrentUser, result);
        return result;
    }

    private static void ReadHive(RegistryHive hive, RegistryView view, UninstallHive tag, List<UninstallEntry> into)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = root.OpenSubKey(UninstallPath);
            if (uninstall is null) return;

            foreach (var name in uninstall.GetSubKeyNames())
            {
                try
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key is null) continue;
                    var entry = RegistryValues.ToEntry(name, tag, v => key.GetValue(v, null, RegistryValueOptions.None));
                    if (entry is not null) into.Add(entry);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
                {
                    // 单个键读不了就跳过（Review Focus 2）
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
        {
            // 整个位置读不了也不影响其他位置
        }
    }
}
