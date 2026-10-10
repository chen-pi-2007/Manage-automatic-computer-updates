using System.Globalization;
using System.Management;

namespace UpdateHelper.Core.Security;

/// <summary>
/// 真实只读安全探针：查 SecurityCenter2 的注册杀软、Defender 状态类。
/// 任何失败（命名空间不存在、被策略禁用、WMI 出错）都返回空/null，不抛异常。
/// </summary>
public sealed class RealSecurityProbe : ISecurityProbe
{
    public IReadOnlyList<RegisteredAntivirus> QueryRegistered()
    {
        var result = new List<RegisteredAntivirus>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\SecurityCenter2", "SELECT displayName, productState, pathToSignedProductExe FROM AntiVirusProduct");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var name = mo["displayName"] as string;
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    var state = mo["productState"] is { } s ? Convert.ToUInt32(s, CultureInfo.InvariantCulture) : 0u;
                    result.Add(new RegisteredAntivirus(name, state, mo["pathToSignedProductExe"] as string));
                }
            }
        }
        catch (Exception)
        {
            // 只读探针：任何 WMI 失败都当作"查不到"
        }
        return result;
    }

    public DefenderStatus? QueryDefender()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\Microsoft\Windows\Defender", "SELECT * FROM MSFT_MpComputerStatus");
            foreach (ManagementObject mo in searcher.Get())
            {
                using (mo)
                {
                    var mode = mo["AMRunningMode"] as string ?? "Unknown";
                    var rtp = mo["RealTimeProtectionEnabled"] as bool? ?? false;
                    var av = mo["AntivirusEnabled"] as bool? ?? false;
                    var sig = mo["AntivirusSignatureVersion"] as string;
                    return new DefenderStatus(mode, rtp, av, string.IsNullOrWhiteSpace(sig) ? null : sig,
                        ToDate(mo["AntivirusSignatureLastUpdated"]));
                }
            }
        }
        catch (Exception)
        {
            // 同上
        }
        return null;
    }

    // WMI 可能给 DateTime，也可能给 CIM-DATETIME 字符串；两种都兜，转换失败为 null。
    private static DateTimeOffset? ToDate(object? raw)
    {
        try
        {
            var dt = raw switch
            {
                DateTime d => d,
                string s when !string.IsNullOrWhiteSpace(s) => ManagementDateTimeConverter.ToDateTime(s),
                _ => (DateTime?)null,
            };
            return dt is { } v ? new DateTimeOffset(v.ToUniversalTime(), TimeSpan.Zero) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
