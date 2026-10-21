using System.IO;
using UpdateHelper.Presentation;
using UpdateHelper.Presentation.Settings;
using UpdateHelper.Presentation.ViewModels;

namespace UpdateHelper.App;

/// <summary>组合根：创建底层、共享状态和各页面的 ViewModel。程序启动时调用一次 Initialize。</summary>
public static class AppHost
{
    public static AppState State { get; private set; } = null!;
    public static SettingsService Settings { get; private set; } = null!;
    public static HomeViewModel Home { get; private set; } = null!;
    public static UpdatesViewModel Updates { get; private set; } = null!;
    public static SoftwareViewModel Software { get; private set; } = null!;
    public static BackgroundViewModel Background { get; private set; } = null!;
    public static HistoryViewModel History { get; private set; } = null!;
    public static SecurityViewModel Security { get; private set; } = null!;
    public static RuleSubscriptionViewModel RuleSubscription { get; private set; } = null!;
    public static RuleSourcesViewModel RuleSources { get; private set; } = null!;
    internal static IConfirmService Confirm { get; private set; } = null!;
    internal static IAppBackend Backend { get; private set; } = null!;
    public static SettingsViewModel SettingsPage { get; private set; } = null!;
    public static AgentSettingsViewModel Agent { get; private set; } = null!;

    public static void Initialize()
    {
        var backend = new RealBackend(Path.Combine(AppContext.BaseDirectory, "rules"));
        Backend = backend;
        State = new AppState(backend, firstSeen: new FirstSeenStore(FirstSeenStore.DefaultPath));
        Settings = new SettingsService(new SettingsStore(SettingsStore.DefaultPath));
        Home = new HomeViewModel(State, Settings);
        Confirm = new WpfUiConfirmService();
        Updates = new UpdatesViewModel(State, Confirm);
        Software = new SoftwareViewModel(State);
        Background = new BackgroundViewModel(State);
        History = new HistoryViewModel(State, Confirm);
        Security = new SecurityViewModel(backend);
        RuleSubscription = new RuleSubscriptionViewModel(backend);
        RuleSources = new RuleSourcesViewModel(backend, Confirm);
        SettingsPage = new SettingsViewModel(Settings);
        Agent = new AgentSettingsViewModel(backend);
        _ = Agent.RefreshAsync();   // 后台查询，不挡启动
    }
}
