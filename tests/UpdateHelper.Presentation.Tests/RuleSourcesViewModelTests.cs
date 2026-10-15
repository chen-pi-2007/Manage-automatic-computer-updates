using UpdateHelper.Core.Rules.ThirdParty;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.Presentation.Tests;

public class RuleSourcesViewModelTests
{
    private static (RuleSourcesViewModel Vm, FakeBackend Backend, FakeConfirmService Confirm) Make(
        ThirdPartyConfig? config = null, bool confirmAnswer = true)
    {
        var backend = new FakeBackend { ThirdPartyConfig = config ?? ThirdPartyConfig.Default };
        var confirm = new FakeConfirmService(confirmAnswer);
        return (new RuleSourcesViewModel(backend, confirm), backend, confirm);
    }

    [Fact]
    public async Task Add_source_rejects_non_https()
    {
        var (vm, backend, _) = Make();
        vm.NewSourceUrl = "http://x/rules.yaml";   // 非 https
        await vm.AddSourceAsync();
        Assert.Empty(backend.ThirdPartyConfig.Sources);
        Assert.Contains("https", vm.AddError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Add_source_accepts_https_and_saves()
    {
        var (vm, backend, _) = Make();
        vm.NewSourceUrl = "https://x/rules.yaml";
        vm.NewSourceNote = "社区源";
        await vm.AddSourceAsync();
        var s = Assert.Single(backend.ThirdPartyConfig.Sources);
        Assert.Equal("https://x/rules.yaml", s.Url);
        Assert.True(s.Enabled);
        Assert.False(s.AllowOverrideOfficial);
        Assert.True(backend.SyncThirdPartyCalled);
    }

    private static ThirdPartyConfig OneSource(bool enabled = true, bool allowOverride = false) =>
        new(true, [new ThirdPartySource(SourceId.FromUrl("https://x/a.yaml"), "https://x/a.yaml", "A", enabled, allowOverride)]);

    [Fact]
    public async Task Disable_source_asks_confirmation_and_reverts_on_cancel()
    {
        var (vm, backend, confirm) = Make(OneSource(), confirmAnswer: false);
        await vm.RefreshAsync();
        await vm.SetEnabledAsync(vm.Sources[0], false);
        Assert.Single(confirm.ConfirmMessages);                     // 弹了确认
        Assert.True(backend.ThirdPartyConfig.Sources[0].Enabled);   // 取消 → 未改
    }

    [Fact]
    public async Task Enable_override_asks_confirmation_before_applying()
    {
        var (vm, backend, confirm) = Make(OneSource(), confirmAnswer: true);
        await vm.RefreshAsync();
        await vm.SetOverrideAsync(vm.Sources[0], true);
        Assert.Contains(confirm.ConfirmMessages, m => m.Contains("覆盖"));
        Assert.True(backend.ThirdPartyConfig.Sources[0].AllowOverrideOfficial);
    }

    [Fact]
    public async Task Delete_source_confirms_then_saves_without_it()
    {
        var (vm, backend, confirm) = Make(OneSource(), confirmAnswer: true);
        await vm.RefreshAsync();
        await vm.DeleteAsync(vm.Sources[0]);
        Assert.Single(confirm.ConfirmMessages);
        Assert.Empty(backend.ThirdPartyConfig.Sources);
    }

    [Fact]
    public async Task Turning_global_off_with_sources_asks_confirmation()
    {
        var (vm, backend, confirm) = Make(OneSource(), confirmAnswer: false);
        await vm.RefreshAsync();
        await vm.SetGlobalEnabledAsync(false);
        Assert.Single(confirm.ConfirmMessages);
        Assert.True(backend.ThirdPartyConfig.ThirdPartyEnabled);   // 取消 → 仍开
    }

    [Fact]
    public async Task Sync_is_guarded_against_reentry_and_resets_busy()
    {
        var (vm, backend, _) = Make();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.SyncGate = gate;

        var first = vm.SyncAsync();
        Assert.True(vm.IsBusy);
        await vm.SyncAsync();                       // 重入：应被直接挡掉
        Assert.Equal(1, backend.SyncThirdPartyCount);

        gate.SetResult();
        await first;
        Assert.False(vm.IsBusy);
    }
}
