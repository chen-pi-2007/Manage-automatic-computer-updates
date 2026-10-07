using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace UpdateHelper.Presentation.ViewModels;

public sealed record HistoryRow(string Time, string Name, string Versions, string Outcome, string Message, string Trigger);

/// <summary>更新历史，最新的在前。每次检查结束（包括安装后的重新检查）自动重新读取。</summary>
public sealed partial class HistoryViewModel : ObservableObject
{
    private readonly AppState _state;

    public HistoryViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.LastChecked)) Reload(); };
        Reload();
    }

    public ObservableCollection<HistoryRow> Rows { get; } = [];

    [RelayCommand]
    private void Reload()
    {
        Rows.Clear();
        foreach (var r in _state.Backend.ReadHistory().OrderByDescending(r => r.Time))
        {
            Rows.Add(new HistoryRow($"{r.Time:yyyy-MM-dd HH:mm}", r.Name, $"{r.FromVersion} → {r.ToVersion}",
                Display.OutcomeName(r.Outcome), r.Message, r.Automatic ? "自动" : "手动"));
        }
    }
}
