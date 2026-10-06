using System.Text;

namespace UpdateHelper.Core.Tests;

/// <summary>测试用的临时目录，用完自动删除。</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("uh-test-").FullName;

    /// <summary>写一个文件（自动建子目录），返回完整路径。</summary>
    public string Write(string relativePath, string content, bool withBom = false)
    {
        var full = System.IO.Path.Combine(Path, relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: withBom));
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
