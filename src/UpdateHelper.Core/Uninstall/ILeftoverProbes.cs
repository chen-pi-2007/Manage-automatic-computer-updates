namespace UpdateHelper.Core.Uninstall;

/// <summary>只读文件系统探针。所有方法失败都返回"不存在/空/未知"，不抛异常。</summary>
public interface IFileProbe
{
    bool DirectoryExists(string path);
    IReadOnlyList<string> GetChildDirectories(string parent);
    long? DirectorySize(string path);
}

/// <summary>只读注册表探针。path 形如 HKCU\Software\Foo。</summary>
public interface IRegistryProbe
{
    bool KeyExists(string path);
}
