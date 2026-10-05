namespace UpdateHelper.Core.Grouping;

public enum SoftwareCategory
{
    /// <summary>普通软件（默认展示）</summary>
    Application,
    /// <summary>开发工具（默认展示）</summary>
    DevTool,
    /// <summary>游戏：由 Steam 等平台负责更新</summary>
    Game,
    /// <summary>运行库：多个版本共存是正常的</summary>
    Runtime,
    /// <summary>硬件驱动</summary>
    Driver,
    /// <summary>无法归属到具体软件的系统/厂商组件合集</summary>
    SystemComponent,
}
