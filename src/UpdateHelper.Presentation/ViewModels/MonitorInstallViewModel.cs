using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateHelper.Core.Agent;
using UpdateHelper.Core.Monitor;
using UpdateHelper.Presentation.Settings;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>足迹列表里的一行（界面直接显示的中文）。</summary>
public sealed record FootprintRow(string Kind, string Path, string Confidence, string Change);

/// <summary>
/// 设置页"监控安装"：选一个安装包 → 在监控下运行 → 装完列出这次的足迹（只读）。
/// 选文件由组合根注入（界面里是文件对话框，测试里是假函数），所以这里不依赖 WPF，可以单测。
/// 只在监控模块已开启时可用；同一时间只跑一个。
/// </summary>
public sealed partial class MonitorInstallViewModel : ObservableObject
{
    private readonly IAppBackend _backend;
    private readonly SettingsService _settings;
    private readonly Func<string?> _pickInstaller;

    public MonitorInstallViewModel(IAppBackend backend, SettingsService settings, Func<string?> pickInstaller)
    {
        _backend = backend;
        _settings = settings;
        _pickInstaller = pickInstaller;
        // 开关变了，按钮是否可用随之变
        _settings.PropertyChanged += (_, _) => StartCommand.NotifyCanExecuteChanged();
    }

    public string Note =>
        "选一个安装程序（.exe 或 .msi），会像双击一样运行它，同时记录它新增和改动了哪些文件、注册表和计划任务。" +
        "安装程序退出后自动结束并列出结果。只记录，不删除任何东西；标「可能」的项需要你自己判断是否属于这个软件。";

    /// <summary>进行中的进度、失败原因等一行提示。</summary>
    [ObservableProperty] private string? _message;

    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(StartCommand))] private bool _isRunning;

    /// <summary>结果标题，如"setup.exe · 2026-11-16 16:28"；没有结果时为 null。</summary>
    [ObservableProperty] private string? _resultTitle;

    /// <summary>结果汇总，如"共 6 项：确定 5 项，可能 1 项（需你确认）"。</summary>
    [ObservableProperty] private string? _summary;

    /// <summary>完整性说明："记录完整"或"可能不完整：……"。</summary>
    [ObservableProperty] private string? _completenessText;

    public ObservableCollection<FootprintRow> Entries { get; } = [];

    private bool CanStart() => _settings.Current.MonitorModuleEnabled && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        var path = _pickInstaller();
        if (string.IsNullOrWhiteSpace(path)) return;   // 取消了选择

        IsRunning = true;
        Message = $"正在监控 {Path.GetFileName(path)} 的安装……装完（安装程序退出）后会自动结束。";
        var found = 0;
        var progress = new Progress<DiscoveryItem>(_ =>
        {
            var n = Interlocked.Increment(ref found);
            Message = $"正在监控 {Path.GetFileName(path)} 的安装：已发现 {n} 项改动……";
        });
        try
        {
            var footprint = await _backend.StartMonitoredInstallAsync(path, progress, CancellationToken.None);
            Show(footprint);
            Message = null;
        }
        catch (Exception ex)
        {
            Message = $"监控没有完成：{ex.Message}";   // 后端的消息都是中文；上一次的结果保留不动
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>显示最近一次保存的足迹（打开设置页时调用）；读不到就保持空。</summary>
    public void LoadLatest()
    {
        if (IsRunning) return;
        try
        {
            if (_backend.ReadFootprints().FirstOrDefault() is { } latest) Show(latest);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到历史足迹不影响使用
        }
    }

    private void Show(MonitorFootprint fp)
    {
        Entries.Clear();
        foreach (var e in fp.Entries
                     .OrderBy(e => e.Confidence)   // 确定在前，可能在后
                     .ThenBy(e => e.Type)
                     .ThenBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            Entries.Add(new FootprintRow(KindText(e.Type), e.Path,
                e.Confidence == FootprintConfidence.Certain ? "确定" : "可能",
                e.IsNew ? "新增" : "修改"));
        }

        ResultTitle = $"{Path.GetFileName(fp.RootExe)} · {fp.Start.ToLocalTime():yyyy-MM-dd HH:mm}";
        var possible = fp.Entries.Count(e => e.Confidence == FootprintConfidence.Possible);
        Summary = fp.Entries.Count == 0
            ? "没有记录到新增或改动的项"
            : $"共 {fp.Entries.Count} 项：确定 {fp.Entries.Count - possible} 项，可能 {possible} 项（需你确认）";
        CompletenessText = fp.Completeness.IsComplete
            ? "记录完整"
            : "可能不完整：" + string.Join("；", fp.Completeness.Reasons);
    }

    private static string KindText(FootprintEntryType t) => t switch
    {
        FootprintEntryType.File => "文件",
        FootprintEntryType.Directory => "文件夹",
        FootprintEntryType.RegistryKey => "注册表键",
        FootprintEntryType.RegistryValue => "注册表值",
        FootprintEntryType.Service => "服务",
        FootprintEntryType.Driver => "驱动",
        FootprintEntryType.ScheduledTask => "计划任务",
        _ => t.ToString(),
    };
}
