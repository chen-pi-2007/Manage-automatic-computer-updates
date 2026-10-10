using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Core.Security;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>安全页：显示杀毒软件保护状态。查询在后台线程，不卡界面。</summary>
public sealed partial class SecurityViewModel(IAppBackend backend) : ObservableObject
{
    private SecurityStatus? _status;

    public string Headline => _status?.Headline ?? "正在检查安全状态……";
    public string Detail => _status?.Detail ?? "";
    public string ProviderName => _status?.ProviderName ?? "";

    public string StateText => _status?.State switch
    {
        AntivirusState.Protected => "已保护",
        AntivirusState.RealTimeOff => "未开启",
        AntivirusState.NotDetected => "未检测到",
        AntivirusState.Unknown => "未知",
        _ => "",
    };

    public string StateColor => _status?.State switch
    {
        AntivirusState.Protected => "#3FA34D",
        AntivirusState.RealTimeOff => "#D9534F",
        AntivirusState.NotDetected => "#D9534F",
        _ => "#808080",
    };

    public async Task RefreshAsync()
    {
        var s = await Task.Run(backend.GetSecurityStatus);
        _status = s;
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Detail));
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateColor));
    }
}
