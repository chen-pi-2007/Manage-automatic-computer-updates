using System.Security;
using Microsoft.Win32;
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Install;

/// <summary>按卸载键名，在 3 处注册表里重新读取 DisplayVersion。只读。</summary>
public sealed class RegistryVersionProbe : IVersionProbe
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    private static readonly (RegistryHive Hive, RegistryView View)[] Locations =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64),
        (RegistryHive.LocalMachine, RegistryView.Registry32),
        (RegistryHive.CurrentUser, RegistryView.Default),
    ];

    public string? GetInstalledVersion(string uninstallKeyName)
    {
        foreach (var (hive, view) in Locations)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var key = root.OpenSubKey($@"{UninstallPath}\{uninstallKeyName}");
                if (key is null) continue;
                return RegistryValues.AsString(key.GetValue("DisplayVersion"));
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException)
            {
                // 读不了就看下一处
            }
        }
        return null;
    }
}
