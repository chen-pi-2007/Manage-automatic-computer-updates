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

    public AgentSettingsViewModel(IAppBackend backend)
    {
        _backend = backend;
        Refresh();
    }

    [ObservableProperty] private string? _message;
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(EnableCommand), nameof(DisableCommand))] private bool _isBusy;

    public string StatusText => _statusError is not null
        ? $"无法查询后台助手状态：{_statusError}"
        : _status switch
        {
            AgentStatus.Ready => "已启用：通过 winget 的更新不再弹出管理员确认框",
            AgentStatus.Outdated => "后台助手版本较旧，需要更新（会弹一次确认框）",
            AgentStatus.Broken => "后台助手不完整（程序或计划任务被删除），请重新启用",
            _ => "未启用：每次更新都会弹出管理员确认框",
        };

    public string ActionText => _status switch
    {
        AgentStatus.Ready => "",
        AgentStatus.Outdated => "更新后台助手",
        AgentStatus.Broken => "重新启用",
        _ => "启用免确认更新",
    };

    public bool CanEnable => ActionText.Length > 0;
    public bool CanDisable => _status is AgentStatus.Ready or AgentStatus.Outdated or AgentStatus.Broken;

    public void Refresh()
    {
        try
        {
            _status = _backend.GetAgentStatus();
            _statusError = null;
        }
        catch (Exception ex)
        {
            _status = null;
            _statusError = ex.Message;
        }
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
            Refresh();
        }
    }
}
