using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace UpdateHelper.Core.Uninstall;

/// <summary>真实变更器：文件用 Move，注册表用 reg.exe 导出/导入 + Registry 删键，重启删用 MoveFileEx。失败一律返回 false。</summary>
public sealed class RealLeftoverMutator : ILeftoverMutator
{
    private const uint MoveFileDelayUntilReboot = 0x4;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool MoveFileEx(string? lpExistingFileName, string? lpNewFileName, uint dwFlags);

    private static bool IsExpected(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or System.Security.SecurityException
            or ArgumentException or NotSupportedException or Win32Exception or InvalidOperationException;

    public bool MoveToBackup(string originalPath, string storedPath) => Move(originalPath, storedPath);

    public bool MoveBack(string storedPath, string originalPath) => Move(storedPath, originalPath);

    private static bool Move(string from, string to)
    {
        try
        {
            var parent = Path.GetDirectoryName(to);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (Directory.Exists(from))
            {
                if (Directory.Exists(to) || File.Exists(to)) return false;
                Directory.Move(from, to);
                return true;
            }
            if (File.Exists(from))
            {
                if (Directory.Exists(to) || File.Exists(to)) return false;
                File.Move(from, to);
                return true;
            }
            return false;
        }
        catch (Exception ex) when (IsExpected(ex)) { return false; }
    }

    public bool ScheduleDeleteOnReboot(string path)
    {
        try { return MoveFileEx(path, null, MoveFileDelayUntilReboot); }
        catch (Exception ex) when (IsExpected(ex) || ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    public bool ExportRegistryKey(string keyPath, string storedRegFile)
    {
        try
        {
            if (!TrySplit(keyPath, out _, out _)) return false;
            var parent = Path.GetDirectoryName(storedRegFile);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            return RunReg(["export", keyPath, storedRegFile, "/y"]) && File.Exists(storedRegFile);
        }
        catch (Exception ex) when (IsExpected(ex)) { return false; }
    }

    public bool DeleteRegistryKey(string keyPath)
    {
        try
        {
            if (!TrySplit(keyPath, out var hive, out var sub)) return false;
            hive.DeleteSubKeyTree(sub, throwOnMissingSubKey: true);
            return true;
        }
        catch (Exception ex) when (IsExpected(ex)) { return false; }
    }

    public bool ImportRegistryFile(string storedRegFile)
    {
        try
        {
            return File.Exists(storedRegFile) && RunReg(["import", storedRegFile]);
        }
        catch (Exception ex) when (IsExpected(ex)) { return false; }
    }

    private static bool TrySplit(string keyPath, out RegistryKey hive, out string sub)
    {
        hive = Registry.CurrentUser;
        sub = "";
        if (string.IsNullOrWhiteSpace(keyPath)) return false;
        var parts = keyPath.Replace('/', '\\').Split('\\', 2);
        if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1])) return false;
        var h = parts[0].ToUpperInvariant() switch
        {
            "HKCU" => Registry.CurrentUser,
            "HKLM" => Registry.LocalMachine,
            "HKCR" => Registry.ClassesRoot,
            _ => null,
        };
        if (h is null) return false;
        hive = h;
        sub = parts[1];
        return true;
    }

    private static bool RunReg(string[] args)
    {
        var psi = new ProcessStartInfo("reg.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return false;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(); } catch (Exception ex) when (IsExpected(ex)) { }
            return false;
        }
        return p.ExitCode == 0;
    }
}
