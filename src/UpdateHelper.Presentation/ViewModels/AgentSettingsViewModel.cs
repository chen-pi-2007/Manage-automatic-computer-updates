using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UpdateHelper.Core.Agent;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>设置页"免确认更新"卡片：显示后台助手状态，启用 / 更新 / 关闭。</summary>
public sealed partial class AgentSettingsViewModel : ObservableObject
{
    private readonly IAppBackend _backend;
    private AgentStatus? _status;
    private string? _statusError;
    private bool _querying = true;

    /// <summary>构造时不查询（查询要调 schtasks，可能很慢）；由调用方在合适的时候 RefreshAsync。</summary>
    public AgentSettingsViewModel(IAppBackend backend) => _backend = backend;

    [ObservableProperty] private string? _message;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(EnableCommand), nameof(DisableCommand))] private bool _isBusy;

    public string StatusText => _querying ? "正在查询后台助手状态……" : _statusError is not null
        ? $"无法查询后台助手状态：{_statusError}"
        : _status switch
        {
            AgentStatus.Ready => "已启用：通过 winget 的更新不再弹出管理员确认框",
            AgentStatus.Outdated => "后台助手版本较旧，需要更新（会弹一次确认框）",
            AgentStatus.Broken => "后台助手不完整（程序或计划任务被删除），请重新启用",
            _ => "未启用：每次更新都会弹出管理员确认框",
        };

    public string ActionText => _querying || _status is null ? "" : _status switch
    {
        AgentStatus.Ready => "",
        AgentStatus.Outdated => "更新后台助手",
        AgentStatus.Broken => "重新启用",
        _ => "启用免确认更新",
    };

    public bool CanEnable => ActionText.Length > 0;
    public bool CanDisable => !_querying && _status is AgentStatus.Ready or AgentStatus.Outdated or AgentStatus.Broken;

    public void ClearMessage() => Message = null;

    /// <summary>在后台线程查询状态（不会卡界面），完成后回到调用线程更新显示。从不抛异常。</summary>
    public async Task RefreshAsync()
    {
        _querying = true;
        Notify();
        try
        {
            _status = await Task.Run(_backend.GetAgentStatus);
            _statusError = null;
        }
        catch (Exception ex)
        {
            _status = null;
            _statusError = ex.Message;
        }
        _querying = false;
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CanEnable));
        OnPropertyChanged(nameof(CanDisable));
    }

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task EnableAsync() => RunAsync(_backend.EnableAgentAsync, "已启用免确认更新");

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private Task DisableAsync() => RunAsync(_backend.DisableAgentAsync, "已关闭免确认更新");

    private async Task RunAsync(Func<Task<string?>> action, string success)
    {
        IsBusy = true;
        Message = "请在弹出的确认框里点“是”……";
        try
        {
            Message = await action() ?? success;
        }
        catch (Exception ex)
        {
            Message = $"操作失败：{ex.Message}";
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }
}
