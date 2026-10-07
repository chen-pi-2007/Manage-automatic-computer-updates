using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>更新列表里的一行。</summary>
public sealed partial class UpdateRow : ObservableObject
{
    public UpdateRow(JudgedUpdate update)
    {
        Update = update;
        CanSelect = update.Tier is UpdateTier.Low or UpdateTier.Careful;
        IsSelected = update.Tier == UpdateTier.Low;    // 默认只勾低风险（用属性赋值，不直接写字段：MVVMTK0034）
    }

    public JudgedUpdate Update { get; }
    public string Name => Update.Group?.Name ?? Update.Candidate.Name;
    public string Versions => $"{Update.Candidate.InstalledVersion} → {Update.Candidate.AvailableVersion}";
    public string TierName => Display.TierName(Update.Tier);
    public string Reason => Update.Reason;

    /// <summary>"不自动"的更新不能勾选（spec 第 5 节）。</summary>
    public bool CanSelect { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _status = "";

    partial void OnIsSelectedChanged(bool value)
    {
        if (value && !CanSelect) IsSelected = false;
    }
}

/// <summary>更新页：列出可用更新，勾选后依次安装。</summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly AppState _state;

    public UpdatesViewModel(AppState state)
    {
        _state = state;
        _state.PropertyChanged += OnStateChanged;
        Rebuild();
    }

    public ObservableCollection<UpdateRow> Rows { get; } = [];
    public ObservableCollection<string> LastResults { get; } = [];

    public string Summary => $"{Rows.Count} 个更新，已选 {Rows.Count(r => r.IsSelected)} 个";

    private bool CanInstall() => !_state.IsBusy && Rows.Any(r => r.IsSelected);

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallSelectedAsync()
    {
        var selected = Rows.Where(r => r.IsSelected).ToList();
        var results = new List<string>();

        var ran = await _state.RunExclusiveAsync(async () =>
        {
            foreach (var row in selected)
            {
                row.Status = "正在更新……";
                string text;
                try
                {
                    var result = await _state.Backend.InstallAsync(row.Update, null, CancellationToken.None);
                    text = $"{Display.OutcomeName(result.Outcome)}：{result.Message}";
                }
                catch (Exception ex)
                {
                    text = $"失败：{ex.Message}";   // 一个出错，继续下一个
                }
                row.Status = text;
                results.Add($"{row.Name}：{text}");
            }
        });
        if (!ran) return;

        LastResults.Clear();
        foreach (var line in results) LastResults.Add(line);
        await _state.RefreshAsync();
    }

    [RelayCommand]
    private Task RefreshAsync() => _state.RefreshAsync();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppState.Updates)) Rebuild();
        if (e.PropertyName == nameof(AppState.IsBusy)) InstallSelectedCommand.NotifyCanExecuteChanged();
    }

    private void Rebuild()
    {
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
        Rows.Clear();
        foreach (var update in _state.Updates.Where(u => u.Tier != UpdateTier.Ignored))
        {
            var row = new UpdateRow(update);
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        SelectionChanged();
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateRow.IsSelected)) SelectionChanged();
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(Summary));
        InstallSelectedCommand.NotifyCanExecuteChanged();
    }
}
