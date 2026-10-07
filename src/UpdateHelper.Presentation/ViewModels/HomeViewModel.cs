using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateHelper.Core.Updates;
using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>首页：更新概况、上次检查时间、更新模式、立即检查。</summary>
public sealed partial class HomeViewModel : ObservableObject
{
    private readonly AppState _state;
    private readonly SettingsService _settings;

    public HomeViewModel(AppState state, SettingsService settings)
    {
        _state = state;
        _settings = settings;
        _state.PropertyChanged += OnStateChanged;
        _settings.PropertyChanged += (_, _) => OnPropertyChanged(nameof(ModeIndex));
    }

    private IEnumerable<JudgedUpdate> Visible => _state.Updates.Where(u => u.Tier != UpdateTier.Ignored);

    public string SummaryTitle
    {
        get
        {
            if (_state.IsBusy && _state.StatusText.Length > 0) return _state.StatusText;
            if (_state.LastChecked is null) return "还没有检查更新";
            var count = Visible.Count();
            return count == 0 ? "所有软件都是最新的" : $"有 {count} 个更新可用";
        }
    }

    public string SummaryDetail
    {
        get
        {
            var list = Visible.ToList();
            if (list.Count == 0) return "";
            int Count(UpdateTier t) => list.Count(u => u.Tier == t);
            return $"其中 {Count(UpdateTier.Low)} 个低风险、{Count(UpdateTier.Careful)} 个需确认、{Count(UpdateTier.NeverAuto)} 个不自动";
        }
    }

    public string LastCheckedText =>
        _state.LastChecked is { } t ? $"上次检查：{t:yyyy-MM-dd HH:mm}" : "还没有检查";

    public string SoftwareCountText =>
        $"已识别 {_state.Snapshot?.Result.Groups.Count(g => g.Primary is not null) ?? 0} 个软件";

    public string? Warning => _state.Warning;

    // —— 图表 ——

    /// <summary>更新按风险分布的环形图；"不管"档不画。</summary>
    public IReadOnlyList<ChartSlice> TierSlices => Charts.Donut(
    [
        (Display.TierName(UpdateTier.Low), Visible.Count(u => u.Tier == UpdateTier.Low), "#3FA34D"),
        (Display.TierName(UpdateTier.Careful), Visible.Count(u => u.Tier == UpdateTier.Careful), "#E8A317"),
        (Display.TierName(UpdateTier.NeverAuto), Visible.Count(u => u.Tier == UpdateTier.NeverAuto), "#D9534F"),
    ]);

    public int TierTotal => Visible.Count();

    /// <summary>软件按分类的条形图，从多到少；组件合集不算。</summary>
    public IReadOnlyList<ChartBar> CategoryBars => Charts.Bars(
        (_state.Snapshot?.Result.Groups ?? [])
            .Where(g => g.Primary is not null)
            .GroupBy(g => g.Category)
            .Select(g => (Display.CategoryName(g.Key), g.Count()))
            .OrderByDescending(x => x.Item2));

    public int ModeIndex
    {
        get => (int)_settings.Current.Mode;
        set => _settings.Update(s => s with { Mode = (UpdateMode)value });
    }

    private bool CanCheck() => !_state.IsBusy;

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private Task CheckNowAsync() => _state.RefreshAsync();

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(SummaryTitle));
        OnPropertyChanged(nameof(SummaryDetail));
        OnPropertyChanged(nameof(LastCheckedText));
        OnPropertyChanged(nameof(SoftwareCountText));
        OnPropertyChanged(nameof(Warning));
        OnPropertyChanged(nameof(TierSlices));
        OnPropertyChanged(nameof(TierTotal));
        OnPropertyChanged(nameof(CategoryBars));
        if (e.PropertyName == nameof(AppState.IsBusy)) CheckNowCommand.NotifyCanExecuteChanged();
    }
}
