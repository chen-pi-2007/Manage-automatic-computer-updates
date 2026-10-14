using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateHelper.Core.Rules.ThirdParty;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>第三方源列表里的一行（只读展示 + 当前开关值）。</summary>
public sealed partial class ThirdPartySourceRow(ThirdPartySourceStatus status) : ObservableObject
{
    public string Id => status.Id;
    public string Url => status.Url;
    public string Note => string.IsNullOrWhiteSpace(status.Note) ? status.Url : status.Note;
    public bool Enabled => status.Enabled;
    public bool AllowOverrideOfficial => status.AllowOverrideOfficial;
    public string StateText => $"{status.RuleCount} 条规则 · {status.StatusText}";
}

/// <summary>「规则」页第三方源管理：增删、启用/覆盖/全局开关（消费性操作走确认框）。</summary>
public sealed partial class RuleSourcesViewModel(IAppBackend backend, IConfirmService confirm) : ObservableObject
{
    [ObservableProperty] private bool _globalEnabled = true;
    [ObservableProperty] private string _newSourceUrl = "";
    [ObservableProperty] private string _newSourceNote = "";
    [ObservableProperty] private string _addError = "";

    public ObservableCollection<ThirdPartySourceRow> Sources { get; } = [];

    public string RiskNotice => "第三方源未经审核，内容和更新包都由对方负责，风险自负。";

    public async Task RefreshAsync()
    {
        var config = await Task.Run(backend.GetThirdPartyConfig);
        GlobalEnabled = config.ThirdPartyEnabled;
        var statuses = await Task.Run(backend.GetThirdPartySourceStatuses);
        Sources.Clear();
        foreach (var s in statuses) Sources.Add(new ThirdPartySourceRow(s));
    }

    public async Task SyncAsync()
    {
        await backend.SyncThirdPartyAsync(CancellationToken.None);
        await RefreshAsync();
    }

    [RelayCommand]
    public async Task AddSourceAsync()
    {
        AddError = "";
        var url = (NewSourceUrl ?? "").Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
        {
            AddError = "请填写 https:// 开头的规则文件地址";
            return;
        }
        var config = backend.GetThirdPartyConfig();
        if (config.Sources.Any(s => string.Equals(s.Url, url, StringComparison.OrdinalIgnoreCase)))
        {
            AddError = "这个源已经添加过了";
            return;
        }
        var added = new ThirdPartySource(SourceId.FromUrl(url), url, (NewSourceNote ?? "").Trim(), true, false);
        backend.SaveThirdPartyConfig(config with { Sources = [.. config.Sources, added] });
        NewSourceUrl = "";
        NewSourceNote = "";
        await backend.SyncThirdPartyAsync(CancellationToken.None);
        await RefreshAsync();
    }

    public async Task SetGlobalEnabledAsync(bool value)
    {
        var config = backend.GetThirdPartyConfig();
        if (!value && config.Sources.Count > 0)   // 关闭且有源 → 确认
        {
            var ok = await confirm.ConfirmAsync("关闭第三方规则源",
                "关闭后，你添加的第三方规则将全部停用，相关软件可能变回认不出 / 不再提示更新。确定关闭吗？");
            if (!ok) { GlobalEnabled = true; return; }
        }
        backend.SaveThirdPartyConfig(config with { ThirdPartyEnabled = value });
        await RefreshAsync();
    }

    public async Task SetEnabledAsync(ThirdPartySourceRow row, bool value)
    {
        if (!value)
        {
            var ok = await confirm.ConfirmAsync("停用这个源",
                $"停用「{row.Note}」后，它的规则将不再生效，相关软件可能变回认不出 / 不再提示更新。确定停用吗？");
            if (!ok) { await RefreshAsync(); return; }
        }
        Mutate(row.Id, s => s with { Enabled = value });
        await RefreshAsync();
    }

    public async Task SetOverrideAsync(ThirdPartySourceRow row, bool value)
    {
        if (value)
        {
            var ok = await confirm.ConfirmAsync("允许覆盖官方规则",
                $"打开后，「{row.Note}」的规则会覆盖同名的官方规则（含安装包的发布者校验目标）。请确认你信任这个源。确定打开吗？");
            if (!ok) { await RefreshAsync(); return; }
        }
        Mutate(row.Id, s => s with { AllowOverrideOfficial = value });
        await RefreshAsync();
    }

    public async Task DeleteAsync(ThirdPartySourceRow row)
    {
        var ok = await confirm.ConfirmAsync("删除这个源",
            $"确定删除「{row.Note}」吗？它的本地缓存也会一起清理。");
        if (!ok) return;
        var config = backend.GetThirdPartyConfig();
        backend.SaveThirdPartyConfig(config with { Sources = config.Sources.Where(s => s.Id != row.Id).ToList() });
        await RefreshAsync();
    }

    private void Mutate(string id, Func<ThirdPartySource, ThirdPartySource> change)
    {
        var config = backend.GetThirdPartyConfig();
        backend.SaveThirdPartyConfig(config with
        {
            Sources = config.Sources.Select(s => s.Id == id ? change(s) : s).ToList(),
        });
    }
}
