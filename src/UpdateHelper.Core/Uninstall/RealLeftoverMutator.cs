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

    public bool CopyToBackup(string originalPath, string storedPath)
    {
        try
        {
            var isDir = Directory.Exists(originalPath);
            if (!isDir && !File.Exists(originalPath)) return false;
            if (Directory.Exists(storedPath) || File.Exists(storedPath)) return false;
            var parent = Path.GetDirectoryName(storedPath);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            try
            {
                if (isDir) CopyDirectory(originalPath, storedPath);
                else CopyFileShared(originalPath, storedPath);
                return true;
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                // 复制一半失败：清掉半成品，避免恢复时误用
                try
                {
                    if (Directory.Exists(storedPath)) Directory.Delete(storedPath, true);
                    else if (File.Exists(storedPath)) File.Delete(storedPath);
                }
                catch (Exception ex2) when (IsExpected(ex2)) { }
                return false;
            }
        }
        catch (Exception ex) when (IsExpected(ex)) { return false; }
    }

    /// <summary>以共享读写方式打开源文件复制，被其它进程占用但允许共享读取的文件也能复制。</summary>
    private static void CopyFileShared(string from, string to)
    {
        using var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var dst = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    public bool MoveBack(string storedPath, string originalPath) => Move(storedPath, originalPath);

    private static bool Move(string from, string to)
    {
        try
        {
            var isDir = Directory.Exists(from);
            if (!isDir && !File.Exists(from)) return false;
            if (Directory.Exists(to) || File.Exists(to)) return false;
            // 源存在且目标不存在后才建父目录，避免失败时留下空目录
            var parent = Path.GetDirectoryName(to);
            if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
            if (!isDir)
            {
                File.Move(from, to); // 文件 Move 自带跨盘支持
                return true;
            }
            try
            {
                Directory.Move(from, to);
                return true;
            }
            catch (IOException)
            {
                // 跨盘时 Directory.Move 会抛 IOException：改为递归复制 + 删除源（沙箱已验证跨盘走此路径）
                return CopyThenDelete(from, to);
            }
        }
        catch (Exception ex) when (IsExpected(ex)) { return false; }
    }

    /// <summary>递归复制目录后删除源；复制失败则清理半成品目标并返回 false，源保持不动。</summary>
    internal static bool CopyThenDelete(string from, string to)
    {
        try
        {
            CopyDirectory(from, to);
            Directory.Delete(from, true);
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // 复制或删源失败：删掉目标副本，避免源与目标并存造成歧义
            try { if (Directory.Exists(to)) Directory.Delete(to, true); }
            catch (Exception ex2) when (IsExpected(ex2)) { }
            return false;
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from))
            CopyFileShared(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.GetDirectories(from))
            CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
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
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi);
        if (p is null) return false;
        // 不重定向输出（用不到），避免 ReadToEnd 阻塞导致超时失效或死锁
        if (!p.WaitForExit(30_000))
        {
            try { p.Kill(); } catch (Exception ex) when (IsExpected(ex)) { }
            return false;
        }
        return p.ExitCode == 0;
    }
}
