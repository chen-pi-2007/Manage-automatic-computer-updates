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
    public static SettingsViewModel SettingsPage { get; private set; } = null!;
    public static AgentSettingsViewModel Agent { get; private set; } = null!;

    public static void Initialize()
    {
        var backend = new RealBackend(Path.Combine(AppContext.BaseDirectory, "rules"));
        State = new AppState(backend);
        Settings = new SettingsService(new SettingsStore(SettingsStore.DefaultPath));
        Home = new HomeViewModel(State, Settings);
        Updates = new UpdatesViewModel(State);
        Software = new SoftwareViewModel(State);
        Background = new BackgroundViewModel(State);
        History = new HistoryViewModel(State);
        SettingsPage = new SettingsViewModel(Settings);
        Agent = new AgentSettingsViewModel(backend);
    }
}
