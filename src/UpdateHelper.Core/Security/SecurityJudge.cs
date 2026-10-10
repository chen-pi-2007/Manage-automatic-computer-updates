namespace UpdateHelper.Core.Security;

/// <summary>把探针结果判成一个诚实的安全状态（spec 第 9 节）。纯逻辑。</summary>
public static class SecurityJudge
{
    private const string DefenderName = "Windows Defender";

    public static SecurityStatus Evaluate(IReadOnlyList<RegisteredAntivirus> registered, DefenderStatus? defender)
    {
        // 1. 第三方杀软优先（它在管时 Defender 会让位）
        var thirdParty = registered.FirstOrDefault(a =>
            !string.Equals(a.DisplayName, DefenderName, StringComparison.OrdinalIgnoreCase));
        if (thirdParty is not null)
            return new SecurityStatus(AntivirusState.Protected, thirdParty.DisplayName,
                $"由 {thirdParty.DisplayName} 保护", "检测到第三方杀毒软件正在保护这台电脑");

        var defenderRegistered = registered.Any(a =>
            string.Equals(a.DisplayName, DefenderName, StringComparison.OrdinalIgnoreCase));

        // 2. 只有 Defender（或没有注册但能读到 Defender 状态）
        if (defenderRegistered || defender is not null)
        {
            if (defender is null)
                return new SecurityStatus(AntivirusState.Unknown, DefenderName,
                    "无法读取 Windows Defender 状态", "可以在 Windows 安全中心查看");
            if (defender.RealTimeProtectionEnabled)
            {
                var detail = defender.SignatureLastUpdated is { } d
                    ? $"病毒库更新于 {d.LocalDateTime:yyyy-MM-dd}"
                    : "已开启实时保护";
                return new SecurityStatus(AntivirusState.Protected, DefenderName, "Windows Defender 正在保护", detail);
            }
            return new SecurityStatus(AntivirusState.RealTimeOff, DefenderName, "实时保护未开启",
                "Windows Defender 已注册，但实时保护是关闭的，建议在 Windows 安全中心开启");
        }

        // 3. 什么都没有
        return new SecurityStatus(AntivirusState.NotDetected, "",
            "没有检测到杀毒软件", "建议开启 Windows Defender 或安装一款杀毒软件");
    }
}
