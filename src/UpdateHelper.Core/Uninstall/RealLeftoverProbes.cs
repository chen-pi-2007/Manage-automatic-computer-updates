using Microsoft.Win32;

namespace UpdateHelper.Core.Uninstall;

/// <summary>真实只读文件系统探针。所有操作失败都返回"不存在/空/未知"。</summary>
public sealed class RealFileProbe : IFileProbe
{
    public bool DirectoryExists(string path) { try { return Directory.Exists(path); } catch { return false; } }

    public IReadOnlyList<string> GetChildDirectories(string parent)
    {
        try { return Directory.Exists(parent) ? Directory.GetDirectories(parent) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException) { return []; }
    }

    public long? DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return null;
        try
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", new EnumerationOptions
                     {
                         RecurseSubdirectories = true,
                         AttributesToSkip = FileAttributes.ReparsePoint,   // 不跟随符号链接，防成环
                         IgnoreInaccessible = true,
                     }))
            {
                try { total += new FileInfo(file).Length; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>真实只读注册表探针。path 形如 HKCU\Software\Foo。</summary>
public sealed class RealRegistryProbe : IRegistryProbe
{
    public bool KeyExists(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var parts = path.Replace('/', '\\').Split('\\', 2);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1])) return false;
        var hive = parts[0].ToUpperInvariant() switch
        {
            "HKCU" => Registry.CurrentUser,
            "HKLM" => Registry.LocalMachine,
            "HKCR" => Registry.ClassesRoot,
            _ => null,
        };
        if (hive is null) return false;
        try
        {
            using var key = hive.OpenSubKey(parts[1]);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException)
        {
            return false;
        }
    }
}
