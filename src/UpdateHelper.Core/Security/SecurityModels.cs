namespace UpdateHelper.Core.Security;

/// <summary>SecurityCenter2 里注册的一个杀毒软件。</summary>
public sealed record RegisteredAntivirus(string DisplayName, uint ProductState, string? ExePath);

/// <summary>Windows Defender 的真实运行状态（来自 MSFT_MpComputerStatus）。</summary>
public sealed record DefenderStatus(
    string AmRunningMode, bool RealTimeProtectionEnabled, bool AntivirusEnabled,
    string? SignatureVersion, DateTimeOffset? SignatureLastUpdated);

/// <summary>安全状态四态。</summary>
public enum AntivirusState { Protected, RealTimeOff, NotDetected, Unknown }

/// <summary>给用户看的安全状态。</summary>
public sealed record SecurityStatus(AntivirusState State, string ProviderName, string Headline, string Detail);
