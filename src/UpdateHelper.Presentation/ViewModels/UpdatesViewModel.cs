using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>更新列表里的一行。</summary>
public sealed partial class UpdateRow : ObservableObject
{
    public UpdateRow(JudgedUpdate update, Rule? rule)
    {
        Update = update;
        IsThirdParty = rule?.Trust == RuleTrust.ThirdParty;
        SourceUrl = rule?.SourceUrl ?? "";
        Signer = rule?.Update?.Signer;

        // 第三方规则缺 signer / silentArgs → 能识别、能显示"有新版"，但不能一键安装
        if (IsThirdParty && (string.IsNullOrWhiteSpace(rule!.Update?.Signer) || string.IsNullOrWhiteSpace(rule.Update?.SilentArgs)))
            BlockReason = "该规则未声明签名者，无法一键安装";

        Installable = BlockReason is null;
        CanSelect = Installable && update.Tier is UpdateTier.Low or UpdateTier.Careful;
        IsSelected = CanSelect && update.Tier == UpdateTier.Low;    // 默认只勾可装的低风险（用属性赋值，不直接写字段：MVVMTK0034）
    }

    public JudgedUpdate Update { get; }
    public bool IsThirdParty { get; }
    public string SourceUrl { get; }
    public string? Signer { get; }
    public bool Installable { get; }
    public string? BlockReason { get; }
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
    private readonly IConfirmService _confirm;

    public UpdatesViewModel(AppState state, IConfirmService confirm)
    {
        _state = state;
        _confirm = confirm;
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
                if (row.IsThirdParty)
                {
                    var ok = await _confirm.ConfirmAsync("第三方规则更新",
                        $"此更新来自未经审核的第三方规则源，内容与安装包都由对方负责，风险自负。\n\n" +
                        $"来源：{row.SourceUrl}\n发布者（签名校验目标）：{row.Signer ?? "（未声明）"}\n\n确定要安装 {row.Name} 吗？");
                    if (!ok) { row.Status = "已取消"; results.Add($"{row.Name}：已取消"); continue; }
                }
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
            var row = new UpdateRow(update, FindRule(update));
            row.PropertyChanged += OnRowChanged;
            Rows.Add(row);
        }
        SelectionChanged();
    }

    private Rule? FindRule(JudgedUpdate update)
    {
        var id = update.Candidate.PackageId;
        if (!id.StartsWith(RuleUpdateSource.PackagePrefix, StringComparison.Ordinal))
            return null;
        var ruleId = id[RuleUpdateSource.PackagePrefix.Length..];
        return _state.Snapshot?.Rules.Rules.FirstOrDefault(r => r.Id == ruleId);
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
