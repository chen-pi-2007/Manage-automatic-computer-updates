namespace UpdateHelper.Core.Monitor;

/// <summary>
/// 把 ETW 给出的 NT 设备路径（\Device\HarddiskVolume3\...）翻成 Win32 盘符路径。
/// 设备映射由 Agent 侧用 QueryDosDevice 建好传入；Core 不碰 Win32 API，便于单测。
/// </summary>
public static class PathNormalizer
{
    /// <param name="deviceMap">键为设备名（如 \Device\HarddiskVolume3），值为盘符（如 C:）。</param>
    public static string ToWin32(string ntOrWin32Path, IReadOnlyDictionary<string, string> deviceMap)
    {
        // 不是以 \Device\ 开头的（已是 Win32 路径等）原样返回
        if (!ntOrWin32Path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            return ntOrWin32Path;

        foreach (var (device, drive) in deviceMap)
        {
            if (!ntOrWin32Path.StartsWith(device, StringComparison.OrdinalIgnoreCase)) continue;
            // 必须落在路径分隔边界上，否则 HarddiskVolume1 会误吞 HarddiskVolume12
            if (ntOrWin32Path.Length != device.Length && ntOrWin32Path[device.Length] != '\\') continue;
            return drive + ntOrWin32Path[device.Length..];
        }
        return ntOrWin32Path; // 无匹配：宁可原样保留，也不瞎猜盘符
    }
}
