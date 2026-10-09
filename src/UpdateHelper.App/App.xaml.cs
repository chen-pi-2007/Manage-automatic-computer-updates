using System.ComponentModel;
using System.Windows;
using UpdateHelper.Presentation;
using Wpf.Ui.Appearance;

namespace UpdateHelper.App;

public partial class App : Application
{
    private const string MutexName = @"Local\UpdateHelper.SingleInstance";
    private const string ActivateEventName = @"Local\UpdateHelper.Activate";

    /// <summary>--page 参数可用的页面名。</summary>
    internal static readonly Dictionary<string, Type> PageTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["home"] = typeof(Pages.HomePage),
        ["updates"] = typeof(Pages.UpdatesPage),
        ["software"] = typeof(Pages.SoftwarePage),
        ["background"] = typeof(Pages.BackgroundPage),
        ["history"] = typeof(Pages.HistoryPage),
        ["settings"] = typeof(Pages.SettingsPage),
    };

    private Mutex? _singleInstance;
    private EventWaitHandle? _activate;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _exitRequested;
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;
    private bool _startedAtLogin;
    private System.Windows.Threading.DispatcherTimer? _checkTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：已经在运行就通知它把窗口调到前面，自己退出
        _singleInstance = new Mutex(true, MutexName, out var isFirst);
        if (!isFirst)
        {
            try { EventWaitHandle.OpenExisting(ActivateEventName).Set(); } catch (WaitHandleCannotBeOpenedException) { }
            Shutdown();
            return;
        }
        _activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        new Thread(() =>
        {
            while (_activate.WaitOne())
                Dispatcher.Invoke(() => ShowWindow(null));
        }) { IsBackground = true }.Start();

        _startedAtLogin = e.Args.Contains("--login");
        Notifier.Activated += page => Dispatcher.Invoke(() => ShowWindow(page == "updates" ? typeof(Pages.UpdatesPage) : null));

        AppHost.Initialize();

        // 每次启动都按当前程序路径对齐一次登记（程序被挪动后会改写到新位置）
        AutoStart.Apply(AppHost.Settings.Current.StartWithWindows);
        AppHost.Settings.PropertyChanged += (_, _) => AutoStart.Apply(AppHost.Settings.Current.StartWithWindows);

        _tray = new TrayIcon { Visible = AppHost.Settings.Current.TrayEnabled };
        _tray.OpenRequested += () => ShowWindow(null);
        _tray.OpenUpdatesRequested += () => ShowWindow(typeof(Pages.UpdatesPage));
        _tray.CheckRequested += () => _ = AppHost.State.RefreshAsync();
        _tray.ExitRequested += ExitApp;
        AppHost.Settings.PropertyChanged += (_, _) => _tray.Visible = AppHost.Settings.Current.TrayEnabled;

        // 每次检查结束：窗口没显示时弹通知
        AppHost.State.PropertyChanged += OnStateChanged;

        _window = new MainWindow();
        _window.Closing += OnWindowClosing;
        // 外观：现在先应用一次；窗口加载后再应用一次（跟随系统主题的监听需要窗口句柄）；设置改了随时应用
        Appearance.Apply(AppHost.Settings.Current, _window);
        _window.Loaded += (_, _) => Appearance.Apply(AppHost.Settings.Current, _window);
        AppHost.Settings.PropertyChanged += (_, _) => Appearance.Apply(AppHost.Settings.Current, _window);

        var pageIndex = Array.IndexOf(e.Args, "--page");
        Type? startPage = pageIndex >= 0 && pageIndex + 1 < e.Args.Length && PageTypes.TryGetValue(e.Args[pageIndex + 1], out var p)
            ? p : null;

        // --minimized：只在托盘里，不显示窗口（不要先 Show 再关，FluentWindow 在 Loaded 里关闭会崩溃）
        if (!e.Args.Contains("--minimized")) ShowWindow(startPage);

        _ = AppHost.State.RefreshAsync();   // 启动时扫描并检查一次

        // 每分钟问一次 CheckSchedule 该不该检查
        _checkTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _checkTimer.Tick += (_, _) =>
        {
            if (CheckSchedule.ShouldCheck(DateTimeOffset.Now, _startedAt, _startedAtLogin,
                    AppHost.State.LastChecked, AppHost.Settings.Current, AppHost.State.IsBusy))
                _ = AppHost.State.RefreshAsync();
        };
        _checkTimer.Start();
    }

    private void ShowWindow(Type? page)
    {
        if (_window is null) return;
        // ShowPage 自己处理"窗口还没加载"的情况（记下来，加载时只导航一次），所以在 Show 之前调用
        if (page is not null) _window.ShowPage(page);
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (TrayPolicy.HideInsteadOfClose(AppHost.Settings.Current.TrayEnabled, _exitRequested))
        {
            e.Cancel = true;
            _window!.Hide();
            return;
        }
        if (_exitRequested) return;   // 托盘"退出"触发的关闭，ExitApp 会接着 Shutdown
        // 窗口已经在关闭中：不能再调 Close()（WPF 会抛 InvalidOperationException），直接退出
        _exitRequested = true;
        Notifier.Uninstall();
        _tray?.Dispose();
        Shutdown();
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppState.LastChecked) || _tray is not { Visible: true }) return;
        var text = TrayPolicy.NotificationText(AppHost.State.Updates, _window is { IsVisible: true, WindowState: not WindowState.Minimized });
        if (text is not null) Notifier.Show("更新管理小助手", text);
    }

    private void ExitApp()
    {
        if (_exitRequested) return;
        _exitRequested = true;
        Notifier.Uninstall();
        _tray?.Dispose();
        _window?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _checkTimer?.Stop();
        _tray?.Dispose();
        _activate?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
