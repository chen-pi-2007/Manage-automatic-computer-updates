using System.Drawing;
using System.Windows.Forms;

namespace UpdateHelper.App;

/// <summary>右下角托盘图标：右键菜单（打开 / 检查更新 / 退出），双击打开，点击通知打开更新页。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开更新管理小助手", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("立即检查更新", null, (_, _) => CheckRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "更新管理小助手",
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.BalloonTipClicked += (_, _) => OpenUpdatesRequested?.Invoke();
    }

    /// <summary>从程序资源里取托盘大小的图标（按当前缩放选 16/20/24… 中最合适的一张）。</summary>
    private static Icon LoadIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        if (info is null) return SystemIcons.Application;
        using var stream = info.Stream;
        return new Icon(stream, SystemInformation.SmallIconSize);
    }

    public event Action? OpenRequested;
    public event Action? OpenUpdatesRequested;
    public event Action? CheckRequested;
    public event Action? ExitRequested;

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    /// <summary>弹一条系统通知（Win10/11 上显示在右下角的通知区）。</summary>
    public void Notify(string title, string text) => _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
