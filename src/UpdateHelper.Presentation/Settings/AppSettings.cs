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

/// <summary>用户设置。默认值来自 spec 第 6、18 节。</summary>
public sealed record AppSettings
{
    public const int MaxObservationDays = 30;
    public const int MinCheckIntervalHours = 1;
    public const int MaxCheckIntervalHours = 168;

    public UpdateMode Mode { get; init; } = UpdateMode.NotifyOnly;
    public int ObservationDays { get; init; } = 3;
    public int CheckIntervalHours { get; init; } = 24;
    public bool TrayEnabled { get; init; } = true;

    /// <summary>把越界的数值限制到范围内，未定义的模式换成默认。</summary>
    public AppSettings Normalized() => this with
    {
        Mode = Enum.IsDefined(Mode) ? Mode : UpdateMode.NotifyOnly,
        ObservationDays = Math.Clamp(ObservationDays, 0, MaxObservationDays),
        CheckIntervalHours = Math.Clamp(CheckIntervalHours, MinCheckIntervalHours, MaxCheckIntervalHours),
    };
}
