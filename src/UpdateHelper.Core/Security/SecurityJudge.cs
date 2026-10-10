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
        // 第三方的真实开关状态各家编码不可靠（productState 不能当真），注册 ≠ 正在保护。
        // 按诚实原则：只说"检测到"，不替它妄称开/关，让用户在它自己的界面确认。
        if (thirdParty is not null)
            return new SecurityStatus(AntivirusState.ThirdParty, thirdParty.DisplayName,
                $"检测到 {thirdParty.DisplayName}",
                "由它负责保护这台电脑；本助手不判断第三方杀毒软件的开关状态，可在它自己的界面查看");

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
