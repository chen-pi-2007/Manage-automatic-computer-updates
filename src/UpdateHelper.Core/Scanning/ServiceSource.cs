using System.Security;
using Microsoft.Win32;

namespace UpdateHelper.Core.Scanning;

/// <summary>从 HKLM\SYSTEM\CurrentControlSet\Services 读取普通服务（跳过驱动）。只读。</summary>
public sealed class ServiceSource : IBackgroundSource
{
    private const int Win32ServiceMask = 0x10 | 0x20; // 独占进程 / 共享进程

    public IReadOnlyList<BackgroundItem> Read()
    {
        var result = new List<BackgroundItem>();
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is null) return result;
            foreach (var name in services.GetSubKeyNames())
            {
                try
                {
                    using var key = services.OpenSubKey(name);
                    if (key is null) continue;
                    var item = ToItem(name, v => key.GetValue(v));
                    if (item is not null) result.Add(item);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException or IOException) { }
        return result;
    }

    public static BackgroundItem? ToItem(string name, Func<string, object?> getValue)
    {
        var type = RegistryValues.AsLong(getValue("Type")) ?? 0;
        if ((type & Win32ServiceMask) == 0) return null;   // 驱动等不是普通服务

        var imagePath = RegistryValues.AsString(getValue("ImagePath"));
        if (imagePath is null) return null;

        var detail = RegistryValues.AsLong(getValue("Start")) switch
        {
            2 => "自动启动",
            3 => "手动启动",
            4 => "已禁用",
            _ => null,
        };

        return new BackgroundItem(BackgroundKind.Service, name,
            RegistryValues.AsString(getValue("DisplayName")),
            imagePath, CommandLineParser.ExtractExecutable(imagePath), detail);
    }
}
