using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation;

/// <summary>
/// 各页面共享的状态：最近一次扫描、更新列表、是否正在忙。
/// 同一时间只做一件耗时的事（扫描/检查或安装），忙时新的请求直接忽略（Review Focus 1）。
/// </summary>
public sealed partial class AppState(IAppBackend backend, TimeProvider? clock = null) : ObservableObject
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public IAppBackend Backend => backend;

    [ObservableProperty] private ScanSnapshot? _snapshot;
    [ObservableProperty] private IReadOnlyList<JudgedUpdate> _updates = [];
    [ObservableProperty] private DateTimeOffset? _lastChecked;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private string? _warning;

    /// <summary>重新扫描并检查更新。忙时直接返回；出错转成 Warning，不抛异常。</summary>
    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            StatusText = "正在扫描电脑上的软件……";
            var snapshot = await backend.ScanAsync(CancellationToken.None);
            Snapshot = snapshot;

            StatusText = "正在检查更新……";
            var report = await backend.CheckUpdatesAsync(snapshot, CancellationToken.None);
            Updates = report.Updates;

            var warnings = snapshot.Warnings.Concat(report.Warning is null ? [] : [report.Warning]).ToList();
            Warning = warnings.Count == 0 ? null : string.Join("；", warnings);
            LastChecked = _clock.GetLocalNow();
        }
        catch (Exception ex)
        {
            Warning = $"检查失败：{ex.Message}";
        }
        finally
        {
            StatusText = "";
            IsBusy = false;
        }
    }

    /// <summary>在"不忙"时独占执行一段工作（如安装）。忙时返回 false 且不执行。</summary>
    public async Task<bool> RunExclusiveAsync(Func<Task> work)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            await work();
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
