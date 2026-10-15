namespace UpdateHelper.Presentation;

/// <summary>向用户确认 / 提示的抽象。App 用 WPF-UI 对话框实现，绝不触发阻塞式系统弹窗。</summary>
public interface IConfirmService
{
    /// <summary>显示带"确定/取消"的确认框；用户点确定返回 true。</summary>
    Task<bool> ConfirmAsync(string title, string message);

    /// <summary>显示只读提示框（单个"知道了"按钮）。</summary>
    Task InfoAsync(string title, string message);
}
