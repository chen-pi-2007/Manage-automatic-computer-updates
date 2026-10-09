namespace UpdateHelper.Presentation;

/// <summary>开机自动启动在注册表 Run 键里应有的登记值。只算字符串，真正读写在 App 层。</summary>
public static class AutoStartPolicy
{
    /// <summary>Run 键下的值名。</summary>
    public const string ValueName = "UpdateHelper";

    /// <summary>开启时返回命令行（exe 路径加引号，带 --minimized --login）；关闭时返回 null，表示删除登记。</summary>
    public static string? DesiredCommand(bool enabled, string exePath) =>
        enabled ? $"\"{exePath}\" --minimized --login" : null;
}
