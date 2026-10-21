using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UpdateHelper.Presentation.ViewModels;

public sealed record HistoryRow(string Time, string Name, string Versions, string Outcome, string Message, string Trigger);

/// <summary>一次卸载前的备份：可一键恢复。</summary>
public sealed record BackupRow(string Id, string Time, string SoftwareName, int ItemCount);

/// <summary>更新历史，最新的在前；另列出卸载残留的备份供一键恢复。每次检查结束（包括安装后的重新检查）自动重新读取。</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly IConfirmService? _confirm;

    public HistoryViewModel(AppState state, IConfirmService? confirm = null)
    {
        _state = state;
        _confirm = confirm;
        _state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.LastChecked)) Reload(); };
        Reload();
    }

    public ObservableCollection<HistoryRow> Rows { get; } = [];
    public ObservableCollection<BackupRow> Backups { get; } = [];

    /// <summary>一键恢复某次卸载前的备份：先确认，再恢复，最后告知结果（失败也只提示、不抛出）。</summary>
    public async Task RestoreAsync(BackupRow row)
    {
        if (_confirm is null) return;
        if (!await _confirm.ConfirmAsync("恢复备份",
                $"把“{row.SoftwareName}”卸载时备份的 {row.ItemCount} 项内容恢复到原位置？")) return;
        try
        {
            var n = await _state.Backend.RestoreBackupAsync(row.Id, CancellationToken.None);
            await _confirm.InfoAsync("恢复备份", n == row.ItemCount
                ? $"已恢复 {n} 项（共 {row.ItemCount} 项）。"
                : $"恢复了 {n} 项（共 {row.ItemCount} 项），其余项的备份已丢失或原位置被占用。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            await _confirm.InfoAsync("恢复备份", $"恢复失败：{ex.Message}");
        }
    }

    [RelayCommand]
    private void Reload()
    {
        Rows.Clear();
        foreach (var r in _state.Backend.ReadHistory().OrderByDescending(r => r.Time))
        {
            Rows.Add(new HistoryRow($"{r.Time:yyyy-MM-dd HH:mm}", r.Name, $"{r.FromVersion} → {r.ToVersion}",
                Display.OutcomeName(r.Outcome), r.Message, r.Automatic ? "自动" : "手动"));
        }

        Backups.Clear();
        try
        {
            foreach (var m in _state.Backend.ReadBackups())
                Backups.Add(new BackupRow(m.Id, $"{m.Time:yyyy-MM-dd HH:mm}", m.SoftwareName, m.Entries.Count));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 备份清单读不了就当没有
        }
    }
}
