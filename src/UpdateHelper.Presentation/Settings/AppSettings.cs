namespace UpdateHelper.Presentation.Settings;

/// <summary>四种更新模式（spec 第 6 节，用户自选）。</summary>
public enum UpdateMode
{
    /// <summary>只提醒：发现更新时通知，点击后自动装好（默认）</summary>
    NotifyOnly,
    /// <summary>分级自动：低风险的在空闲时自动装，其余提醒</summary>
    Tiered,
    /// <summary>全部自动：除锁定的软件外全部自动装</summary>
    FullAuto,
    /// <summary>打开才检查：没有后台检查</summary>
    OnOpenOnly,
}

/// <summary>主题：跟随系统 / 浅色 / 深色。</summary>
public enum ThemeChoice { System, Light, Dark }

/// <summary>窗口背景材质：云母 / 亚克力（半透明）/ 纯色。</summary>
public enum BackdropChoice { Mica, Acrylic, Solid }

/// <summary>文字大小。</summary>
public enum TextSize { Small, Standard, Large }

/// <summary>用户设置。默认值来自 spec 第 6、18 节；外观默认跟随系统。</summary>
public sealed record AppSettings
{
    public const int MaxObservationDays = 30;
    public const int MinCheckIntervalHours = 1;
    public const int MaxCheckIntervalHours = 168;

    public UpdateMode Mode { get; init; } = UpdateMode.NotifyOnly;
    public int ObservationDays { get; init; } = 3;
    public int CheckIntervalHours { get; init; } = 24;
    public bool TrayEnabled { get; init; } = true;

    public ThemeChoice Theme { get; init; } = ThemeChoice.System;
    /// <summary>强调色，形如 #RRGGBB；null 表示跟随系统。</summary>
    public string? AccentColor { get; init; }
    public BackdropChoice Backdrop { get; init; } = BackdropChoice.Mica;
    public TextSize TextSize { get; init; } = TextSize.Standard;
    /// <summary>紧凑模式：表格行更矮，一屏能看到更多软件。</summary>
    public bool Compact { get; init; }

    /// <summary>把越界的数值限制到范围内，未定义的选项和格式不对的颜色换成默认。</summary>
    public AppSettings Normalized() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : UpdateMode.NotifyOnly,
        ObservationDays = Math.Clamp(ObservationDays, 0, MaxObservationDays),
        CheckIntervalHours = Math.Clamp(CheckIntervalHours, MinCheckIntervalHours, MaxCheckIntervalHours),
        Theme = Enum.IsDefined(Theme) ? Theme : ThemeChoice.System,
        AccentColor = IsHexColor(AccentColor) ? AccentColor!.ToUpperInvariant() : null,
        Backdrop = Enum.IsDefined(Backdrop) ? Backdrop : BackdropChoice.Mica,
        TextSize = Enum.IsDefined(TextSize) ? TextSize : TextSize.Standard,
    };

    private static bool IsHexColor(string? s) =>
        s is { Length: 7 } && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);
}
