using Microsoft.Win32;
using UpdateHelper.Presentation;

namespace UpdateHelper.App;

/// <summary>把"开机自动启动"开关落到注册表 HKCU Run 键。只在值需要变化时写；失败不抛异常。</summary>
public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void Apply(bool enabled)
    {
        var desired = AutoStartPolicy.DesiredCommand(enabled, Environment.ProcessPath!);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            var current = key.GetValue(AutoStartPolicy.ValueName) as string;
            if (desired is null)
            {
                if (current is not null) key.DeleteValue(AutoStartPolicy.ValueName, throwOnMissingValue: false);
            }
            else if (!string.Equals(current, desired, StringComparison.Ordinal))
            {
                key.SetValue(AutoStartPolicy.ValueName, desired, RegistryValueKind.String);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException) { }
    }
}
