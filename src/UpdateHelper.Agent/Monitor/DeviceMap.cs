using System.Runtime.InteropServices;
using System.Text;
using UpdateHelper.Core.Monitor;

namespace UpdateHelper.Agent.Monitor;

/// <summary>
/// 设备路径适配器：用 QueryDosDevice 建 \Device\HarddiskVolumeN → 盘符 表，
/// 去掉 \??\ 之类的 NT 前缀，并为非系统卷取卷序列号（Ruling-1：U 盘盘符变了也能靠序列号找回）。
/// ETW 回调是单线程的，但 Refresh 可能被别处调用，所以内部加锁。
/// </summary>
public sealed class DeviceMap
{
    private readonly object _gate = new();
    private Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _serialByDrive = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _systemDrive =
        Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))?.TrimEnd('\\') ?? "C:";
    private long _lastRefreshTick;

    public DeviceMap() => Refresh();

    /// <summary>重建设备表（会话期间插拔 U 盘后，旧表会找不到新设备）。</summary>
    public void Refresh()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var buf = new StringBuilder(1024);
        for (var c = 'A'; c <= 'Z'; c++)
        {
            var drive = c + ":";
            buf.Clear();
            if (QueryDosDevice(drive, buf, buf.Capacity) == 0) continue;
            // 结果是多字符串，取第一条；subst/网络映射也一并收，原样当设备名
            var target = buf.ToString().Split('\0')[0];
            if (target.Length > 0) map[target] = drive;
        }
        lock (_gate)
        {
            _map = map;
            _serialByDrive.Clear();   // 盘符可能换了卷，序列号缓存作废
            _lastRefreshTick = Environment.TickCount64;
        }
    }

    /// <summary>
    /// 把 ETW/NT 路径变成 Win32 盘符路径，并给出所在卷的序列号（系统盘为 null）。
    /// 路径翻不出盘符时原样返回、序列号为 null（宁可保留原始路径也不瞎猜）。
    /// </summary>
    public (string Path, string? VolumeSerial) Resolve(string rawPath)
    {
        var p = StripNtPrefix(rawPath);
        Dictionary<string, string> map;
        long last;
        lock (_gate) { map = _map; last = _lastRefreshTick; }
        var win32 = PathNormalizer.ToWin32(p, map);

        // 没翻出来且像是新插入的设备：限频重建一次再试（最多每 2 秒一次，避免噪声路径把 CPU 吃光）
        if (win32.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase)
            && Environment.TickCount64 - last > 2000)
        {
            Refresh();
            lock (_gate) map = _map;
            win32 = PathNormalizer.ToWin32(p, map);
        }

        if (win32.Length >= 2 && win32[1] == ':')
            return (win32, SerialFor(win32[..2]));
        return (win32, null);
    }

    /// <summary>去掉 \??\、\\?\ 前缀（PathNormalizer 只认 \Device\ 与已是 Win32 的路径）。</summary>
    public static string StripNtPrefix(string path)
    {
        if (path.StartsWith(@"\??\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            path = path[4..];
        // \??\UNC\server\share → \\server\share
        if (path.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[4..];
        return path;
    }

    private string? SerialFor(string drive)
    {
        // 系统盘不需要序列号：它的盘符不会变
        if (string.Equals(drive, _systemDrive, StringComparison.OrdinalIgnoreCase)) return null;
        lock (_gate)
        {
            if (_serialByDrive.TryGetValue(drive, out var cached)) return cached;
            string? serial = null;
            // 在 ETW 回调线程里同步查，对断开/慢速卷可能略卡；结果按盘符缓存，每个盘符只查一次
            if (GetVolumeInformation(drive + "\\", null, 0, out var sn, out _, out _, null, 0))
                serial = sn.ToString("X8");
            _serialByDrive[drive] = serial;
            return serial;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(string lpDeviceName, StringBuilder lpTargetPath, int ucchMax);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeInformation(
        string lpRootPathName, StringBuilder? lpVolumeNameBuffer, int nVolumeNameSize,
        out uint lpVolumeSerialNumber, out uint lpMaximumComponentLength, out uint lpFileSystemFlags,
        StringBuilder? lpFileSystemNameBuffer, int nFileSystemNameSize);
}
