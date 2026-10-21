namespace UpdateHelper.Core.Uninstall;

/// <summary>可直接运行的卸载命令。</summary>
/// <param name="ExecutablePath">卸载程序路径（环境变量已展开）。</param>
/// <param name="Arguments">命令行参数。</param>
/// <param name="IsMsi">是否 MSI 卸载（msiexec）。</param>
/// <param name="Silent">是否静默卸载（不弹界面）。</param>
public sealed record UninstallCommand(string ExecutablePath, string Arguments, bool IsMsi, bool Silent);
