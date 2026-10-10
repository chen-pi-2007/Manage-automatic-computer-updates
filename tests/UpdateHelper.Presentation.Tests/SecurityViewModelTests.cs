using UpdateHelper.Core.Security;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

public sealed class SecurityViewModelTests
{
    private static SecurityViewModel Vm(SecurityStatus s)
    {
        var backend = new FakeBackend { OnGetSecurityStatus = () => s };
        return new SecurityViewModel(backend);
    }

    [Fact]
    public async Task Protected_shows_green()
    {
        var vm = Vm(new SecurityStatus(AntivirusState.Protected, "Windows Defender", "Windows Defender 正在保护", "病毒库更新于 2026-10-09"));
        await vm.RefreshAsync();
        Assert.Equal("Windows Defender 正在保护", vm.Headline);
        Assert.Equal("已保护", vm.StateText);
        Assert.Equal("#3FA34D", vm.StateColor);
    }

    [Fact]
    public async Task Realtime_off_shows_red()
    {
        var vm = Vm(new SecurityStatus(AntivirusState.RealTimeOff, "Windows Defender", "实时保护未开启", "……"));
        await vm.RefreshAsync();
        Assert.Equal("未开启", vm.StateText);
        Assert.Equal("#D9534F", vm.StateColor);
    }

    [Fact]
    public async Task Not_detected_shows_red()
    {
        var vm = Vm(new SecurityStatus(AntivirusState.NotDetected, "", "没有检测到杀毒软件", "……"));
        await vm.RefreshAsync();
        Assert.Equal("未检测到", vm.StateText);
    }

    [Fact]
    public async Task Unknown_shows_gray()
    {
        var vm = Vm(new SecurityStatus(AntivirusState.Unknown, "Windows Defender", "无法读取 Windows Defender 状态", "……"));
        await vm.RefreshAsync();
        Assert.Equal("未知", vm.StateText);
        Assert.Equal("#808080", vm.StateColor);
    }

    [Fact]
    public void Before_refresh_shows_querying()
    {
        var vm = Vm(new SecurityStatus(AntivirusState.Protected, "x", "x", "x"));
        Assert.Contains("正在", vm.Headline);
    }
}
