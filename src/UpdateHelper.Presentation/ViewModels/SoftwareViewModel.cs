using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Presentation.ViewModels;

public sealed record SoftwareRow(string Name, string Publisher, string Version, string Category, string Components, string Location);

/// <summary>我的软件：已归组的软件列表，默认只显示普通软件和开发工具，可搜索。</summary>
public sealed partial class SoftwareViewModel : ObservableObject
{
    private readonly AppState _state;

    public SoftwareViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(AppState.Snapshot)) Rebuild(); };
        Rebuild();
    }

    public ObservableCollection<SoftwareRow> Rows { get; } = [];

    [ObservableProperty] private bool _showAll;
    [ObservableProperty] private string _searchText = "";

    public string CountText => $"共 {Rows.Count} 个";

    partial void OnShowAllChanged(bool value) => Rebuild();
    partial void OnSearchTextChanged(string value) => Rebuild();

    private void Rebuild()
    {
        Rows.Clear();
        var groups = _state.Snapshot?.Result.Groups ?? [];
        foreach (var g in groups)
        {
            if (!ShowAll && g.Category is not (SoftwareCategory.Application or SoftwareCategory.DevTool)) continue;
            if (SearchText.Length > 0 && !g.Name.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            Rows.Add(new SoftwareRow(g.Name, g.Publisher ?? "", g.Version ?? "", Display.CategoryName(g.Category),
                g.Components.Count > 0 ? $"含 {g.Components.Count} 个组件" : "",
                g.InstallLocations.FirstOrDefault() ?? ""));
        }
        OnPropertyChanged(nameof(CountText));
    }
}
