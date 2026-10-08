using UpdateHelper.Core.Agent;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

public sealed class AgentSettingsTests
{
    [Theory]
    [InlineData(AgentStatus.NotEnabled, "未启用", "启用免确认更新", false)]
    [InlineData(AgentStatus.Ready, "已启用", "", true)]
    [InlineData(AgentStatus.Outdated, "版本较旧", "更新后台助手", true)]
    [InlineData(AgentStatus.Broken, "不完整", "重新启用", true)]
    public async Task Status_text_and_buttons(AgentStatus status, string statusPart, string action, bool canDisable)
    {
        var vm = new AgentSettingsViewModel(new FakeBackend { AgentStatus = status });
        await vm.RefreshAsync();

        Assert.Contains(statusPart, vm.StatusText);
        Assert.Equal(action, vm.ActionText);
        Assert.Equal(action.Length > 0, vm.CanEnable);
        Assert.Equal(canDisable, vm.CanDisable);
    }

    [Fact]
    public async Task Enable_success_refreshes_status()
    {
        var backend = new FakeBackend { AgentStatus = AgentStatus.NotEnabled };
        backend.OnEnableAgent = () => { backend.AgentStatus = AgentStatus.Ready; return null; };
        var vm = new AgentSettingsViewModel(backend);
        await vm.RefreshAsync();

        await vm.EnableCommand.ExecuteAsync(null);

        Assert.Contains("已启用", vm.StatusText);
        Assert.Contains("已启用", vm.Message);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Enable_failure_shows_reason_and_keeps_status()
    {
        var backend = new FakeBackend { AgentStatus = AgentStatus.NotEnabled, OnEnableAgent = () => "已取消（在确认框里点了“否”）" };
        var vm = new AgentSettingsViewModel(backend);
        await vm.RefreshAsync();

        await vm.EnableCommand.ExecuteAsync(null);

        Assert.Contains("未启用", vm.StatusText);
        Assert.Equal("已取消（在确认框里点了“否”）", vm.Message);
    }

    [Fact]
    public async Task Disable_returns_to_not_enabled()
    {
        var backend = new FakeBackend { AgentStatus = AgentStatus.Ready };
        backend.OnDisableAgent = () => { backend.AgentStatus = AgentStatus.NotEnabled; return null; };
        var vm = new AgentSettingsViewModel(backend);
        await vm.RefreshAsync();

        await vm.DisableCommand.ExecuteAsync(null);

        Assert.Contains("未启用", vm.StatusText);
    }

    [Fact]
    public async Task Status_query_failure_is_shown_not_thrown()
    {
        var vm = new AgentSettingsViewModel(new FakeBackend { OnGetAgentStatus = () => throw new InvalidOperationException("schtasks 坏了") });
        await vm.RefreshAsync();
        Assert.Contains("schtasks 坏了", vm.StatusText);
    }

    [Fact]
    public void Before_first_refresh_shows_querying_and_no_buttons()
    {
        var backend = new FakeBackend();
        var vm = new AgentSettingsViewModel(backend);

        Assert.Contains("正在查询", vm.StatusText);
        Assert.False(vm.CanEnable);
        Assert.False(vm.CanDisable);
        Assert.Equal(0, backend.AgentStatusCalls);
    }
}
