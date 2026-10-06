using System.Diagnostics;
using System.Runtime.InteropServices;
using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Core.Install;

/// <summary>
/// 找出程序文件位于给定目录内、正在运行的进程。只读，不会结束任何进程。
/// 用 QueryFullProcessImageName 取路径：只需要"有限查询"权限，普通权限下也能读到以管理员身份运行的进程，
/// 而且没有 32/64 位的限制（Process.MainModule 两样都做不到）。
/// </summary>
public sealed class RunningProcessProbe : IRunningProcessProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;

    public IReadOnlyList<string> FindRunningUnder(IReadOnlyList<string> directories)
    {
        var dirs = directories.Select(PathUtil.NormalizeDir).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dirs.Count == 0) return [];

        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                var path = ImagePath(process.Id);
                if (path is not null && dirs.Any(d => PathUtil.IsUnder(path, d)))
                    found.Add(Path.GetFileName(path));
            }
        }
        return found.ToList();
    }

    /// <summary>进程的程序完整路径；系统进程等读不到时返回 null。</summary>
    private static string? ImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[1024];
            var size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] exeName, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);
}
