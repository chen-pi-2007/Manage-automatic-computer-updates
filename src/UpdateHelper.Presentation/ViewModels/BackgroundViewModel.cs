using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Presentation.ViewModels;

public sealed record BackgroundRow(string Kind, string Name, string Owner, string Explanation, string Path);

/// <summary>后台项目：服务、开机自启、计划任务，附归属软件和一句话说明。第一期只看不改。</summary>
public sealed class BackgroundViewModel : ObservableObject
{
    private readonly AppState _state;

    public BackgroundViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.Snapshot)) Rebuild(); };
        Rebuild();
    }

    public ObservableCollection<BackgroundRow> Rows { get; } = [];
    public string CountText => $"共 {Rows.Count} 个后台项目";

    private void Rebuild()
    {
        Rows.Clear();
        var snapshot = _state.Snapshot;
        if (snapshot is not null)
        {
            var items = snapshot.Result.Groups.SelectMany(g => g.Background.Select(b => (Item: b, Owner: g.Name)))
                .Concat(snapshot.Result.UnassignedBackground.Select(b => (Item: b, Owner: "（未归属）")))
                .OrderBy(x => x.Owner == "（未归属）")
                .ThenBy(x => x.Owner, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.Item.Kind);
            foreach (var (item, owner) in items)
            {
                Rows.Add(new BackgroundRow(Display.KindName(item.Kind), item.Name, owner,
                    snapshot.Explanations.TryGetValue(item, out var text) ? text : "未知用途",
                    item.ExecutablePath ?? item.Command ?? ""));
            }
        }
        OnPropertyChanged(nameof(CountText));
    }
}
