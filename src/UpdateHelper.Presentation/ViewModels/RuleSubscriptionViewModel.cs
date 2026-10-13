using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Core.Rules.Subscription;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>规则订阅页：显示官方源状态、手动同步。查询/同步都在后台线程。</summary>
public sealed partial class RuleSubscriptionViewModel(IAppBackend backend) : ObservableObject
{
    private RuleSubscriptionInfo? _info;
    private bool _busy;
    private SyncStatus? _syncStatus;   // 本次会话手动同步的结果，优先于已落盘的状态

    public string SourceUrl => _info?.SourceUrl ?? "";
    public bool IsBusy { get => _busy; private set => SetProperty(ref _busy, value); }

    public string VersionText => _info is null ? ""
        : $"规则包版本 {_info.CurrentVersion?.ToString() ?? "（无）"}，共 {_info.RuleCount} 条规则";

    public string LastSyncText => _info?.LastSync is { } t
        ? $"上次同步：{t.LocalDateTime:yyyy-MM-dd HH:mm}"
        : "上次同步：尚未同步";

    public string StateText => (_syncStatus ?? _info?.LastStatus) switch
    {
        SyncStatus.Updated => "已更新到最新",
        SyncStatus.AlreadyLatest => "已是最新",
        SyncStatus.DowngradeRejected => "已是最新（忽略了更旧的包）",
        SyncStatus.VerificationFailed => "同步失败：签名校验未通过，已继续使用现有规则",
        SyncStatus.NetworkFailed => "同步失败：连不上官方源，已继续使用现有规则",
        SyncStatus.CacheWriteFailed => "同步失败：无法写入本地缓存，已继续使用现有规则",
        null => "尚未同步，正在使用内置规则",
        _ => "",
    };

    public string Detail => "程序自带一份官方规则；联网时会从官方源拉取更新的签名规则包，验签通过才使用。出问题始终用自带规则兜底。";

    public async Task RefreshAsync()
    {
        _info = await Task.Run(backend.GetRuleSubscriptionInfo);
        RaiseAll();
    }

    public async Task SyncAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var outcome = await backend.SyncRulesAsync(CancellationToken.None);
            _syncStatus = outcome.Status;
            _info = await Task.Run(backend.GetRuleSubscriptionInfo);
        }
        finally { IsBusy = false; }
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(SourceUrl));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(LastSyncText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(Detail));
    }
}
