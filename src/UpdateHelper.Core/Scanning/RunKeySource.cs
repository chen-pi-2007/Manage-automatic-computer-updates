using System.Security;
using Microsoft.Win32;

namespace UpdateHelper.Core.Scanning;

/// <summary>读取 3 处 Run 键里的开机自启项。只读。</summary>
public sealed class RunKeySource : IBackgroundSource
{
    private static readonly (RegistryHive Hive, RegistryView View, string Label)[] Locations =
    [
        (RegistryHive.LocalMachine, RegistryView.Registry64, @"HKLM\...\Run"),
        (RegistryHive.LocalMachine, RegistryView.Registry32, @"HKLM\...\Run (32 位)"),
        (RegistryHive.CurrentUser, RegistryView.Default, @"HKCU\...\Run"),
    ];

    public IReadOnlyList<BackgroundItem> Read()
    {
        var result = new List<BackgroundItem>();
        foreach (var (hive, view, label) in Locations)
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var run = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run");
                if (run is null) continue;
                foreach (var name in run.GetValueNames())
                {
                    var item = ToItem(name, run.GetValue(name), label);
                    if (item is not null) result.Add(item);
                }
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { }
        }
        return result;
    }

    public static BackgroundItem? ToItem(string name, object? value, string location)
    {
        if (value is not string command || string.IsNullOrWhiteSpace(command)) return null;
        return new BackgroundItem(BackgroundKind.RunKey, name, null, command,
            CommandLineParser.ExtractExecutable(command), location);
    }
}
