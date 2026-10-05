using UpdateHelper.Core.Grouping;

namespace UpdateHelper.Core.Scanning;

/// <summary>一次扫描的结果 + 扫描过程中的警告（某个来源读取失败等）。</summary>
public sealed record ScanReport(ScanResult Result, IReadOnlyList<string> Warnings);

/// <summary>把所有来源串起来做一次完整扫描。只读。</summary>
public sealed class SystemScanner(
    IUninstallSource uninstall,
    IReadOnlyList<IBackgroundSource> background,
    string windowsDirectory)
{
    public static SystemScanner CreateDefault() => new(
        new RegistryUninstallSource(),
        [new ServiceSource(), new RunKeySource(), new StartupFolderSource(), new ScheduledTaskSource()],
        Environment.GetFolderPath(Environment.SpecialFolder.Windows));

    public ScanReport Scan()
    {
        var warnings = new List<string>();

        IReadOnlyList<UninstallEntry> entries = [];
        try { entries = uninstall.Read(); }
        catch (Exception ex) { warnings.Add($"读取已装软件失败（{uninstall.GetType().Name}）：{ex.Message}"); }

        var items = new List<BackgroundItem>();
        foreach (var source in background)
        {
            try
            {
                items.AddRange(source.Read().Where(i => !PathUtil.IsUnder(i.ExecutablePath, windowsDirectory)));
            }
            catch (Exception ex)
            {
                warnings.Add($"读取后台项目失败（{source.GetType().Name}）：{ex.Message}");
            }
        }

        return new ScanReport(SoftwareGrouper.Group(entries, items), warnings);
    }
}
