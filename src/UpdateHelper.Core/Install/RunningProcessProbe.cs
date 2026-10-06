using System.ComponentModel;
using System.Diagnostics;
using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Core.Install;

/// <summary>找出程序文件位于给定目录内、正在运行的进程。只读，不会结束任何进程。</summary>
public sealed class RunningProcessProbe : IRunningProcessProbe
{
    public IReadOnlyList<string> FindRunningUnder(IReadOnlyList<string> directories)
    {
        var dirs = directories.Select(PathUtil.NormalizeDir).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (dirs.Count == 0) return [];

        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                string? path;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    continue;   // 系统进程、更高权限的进程读不到路径，跳过
                }
                if (path is not null && dirs.Any(d => PathUtil.IsUnder(path, d)))
                    found.Add(Path.GetFileName(path));
            }
        }
        return found.ToList();
    }
}
