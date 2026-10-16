using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>残留表格的一行。Warning: null 无警告 / "shared" 黄 / "personal" 红（界面决定颜色）。</summary>
public sealed record LeftoverRow(string TypeName, string Path, string CategoryName, string SizeText, bool Checked, string? Warning, string? Note);

/// <summary>"查看残留"对话框的界面逻辑。只读预览，不涉及删除。</summary>
public sealed class LeftoverViewModel
{
    public LeftoverViewModel(IReadOnlyList<LeftoverItem> items)
    {
        Rows = items.Select(ToRow).ToList();
        var checkedCount = items.Count(i => i.DefaultChecked);
        Summary = items.Count == 0
            ? "没有找到可清理的残留"
            : $"共 {items.Count} 项，默认选中 {checkedCount} 项（共用和个人数据默认不选）";
    }

    public IReadOnlyList<LeftoverRow> Rows { get; }
    public string Summary { get; }
    public string DisclaimerText =>
        "扫描只能尽量找全，做不到 100%。删除和备份功能将在后台助手完成后提供。";

    private static LeftoverRow ToRow(LeftoverItem i) => new(
        TypeName(i.Type), i.Path, CategoryName(i.Category), SizeText(i.SizeBytes), i.DefaultChecked,
        i.Category switch { LeftoverCategory.Shared => "shared", LeftoverCategory.Personal => "personal", _ => null },
        i.Note);

    private static string TypeName(LeftoverType t) => t switch
    {
        LeftoverType.InstallDir => "安装目录",
        LeftoverType.DataDir => "数据文件夹",
        LeftoverType.File => "文件",
        LeftoverType.RegistryKey => "注册表项",
        LeftoverType.Service => "服务",
        LeftoverType.ScheduledTask => "计划任务",
        _ => "开机自启",
    };

    private static string CategoryName(LeftoverCategory c) => c switch
    {
        LeftoverCategory.Owned => "确定属于",
        LeftoverCategory.Possible => "可能属于",
        LeftoverCategory.Shared => "多软件共用",
        _ => "个人数据",
    };

    public static string SizeText(long? bytes)
    {
        if (bytes is null) return "—";
        double b = bytes.Value;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var u = 0;
        while (b >= 1024 && u < units.Length - 1) { b /= 1024; u++; }
        return u == 0 ? $"{bytes} B" : $"{b:0.#} {units[u]}";
    }
}
