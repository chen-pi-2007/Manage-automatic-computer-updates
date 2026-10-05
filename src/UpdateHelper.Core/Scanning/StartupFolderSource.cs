namespace UpdateHelper.Core.Scanning;

/// <summary>读取"启动"文件夹（当前用户 + 所有用户）里的快捷方式和程序。只读。</summary>
public sealed class StartupFolderSource : IBackgroundSource
{
    public IReadOnlyList<BackgroundItem> Read()
    {
        var result = new List<BackgroundItem>();
        foreach (var folder in new[] { Environment.SpecialFolder.Startup, Environment.SpecialFolder.CommonStartup })
        {
            var dir = Environment.GetFolderPath(folder);
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                if (Path.GetFileName(file).Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
                var target = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ResolveShortcut(file) : file;
                result.Add(new BackgroundItem(BackgroundKind.StartupFolder,
                    Path.GetFileNameWithoutExtension(file), null, target ?? file, target, dir));
            }
        }
        return result;
    }

    /// <summary>用 WScript.Shell 读取快捷方式的目标；失败返回 null。</summary>
    private static string? ResolveShortcut(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            var target = (string)shell.CreateShortcut(lnkPath).TargetPath;
            return string.IsNullOrWhiteSpace(target) ? null : target;
        }
        catch (Exception)
        {
            return null;   // COM 不可用或快捷方式损坏
        }
    }
}
