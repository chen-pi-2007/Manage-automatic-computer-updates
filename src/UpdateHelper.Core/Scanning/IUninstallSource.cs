namespace UpdateHelper.Core.Scanning;

/// <summary>提供卸载登记的来源。真实实现读注册表，测试用假实现。</summary>
public interface IUninstallSource
{
    IReadOnlyList<UninstallEntry> Read();
}
