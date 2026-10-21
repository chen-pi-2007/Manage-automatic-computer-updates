using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Presentation.ViewModels;

/// <summary>残留表格的一行。Warning: null 无警告 / "shared" 黄 / "personal" 红（界面决定颜色）。Checked 可由用户勾选切换。</summary>
public sealed class LeftoverRow(LeftoverItem item, string typeName, string categoryName, string sizeText, string? warning)
{
    public LeftoverItem Item { get; } = item;
    public string TypeName { get; } = typeName;
    public string Path => Item.Path;
    public string CategoryName { get; } = categoryName;
    public string SizeText { get; } = sizeText;
    public bool Checked { get; set; } = item.DefaultChecked;
    public string? Warning { get; } = warning;
    public string? Note => Item.Note;
}

/// <summary>"查看残留 / 卸载"对话框的界面逻辑：勾选要清理的残留，收集选中项交给底层。</summary>
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
        "扫描只能尽量找全，做不到 100%。删除前会先备份，可在“历史”页一键恢复。";

    /// <summary>当前被勾选的残留项（按表格顺序）。</summary>
    public IReadOnlyList<LeftoverItem> SelectedItems => Rows.Where(r => r.Checked).Select(r => r.Item).ToList();
    public int SelectedCount => Rows.Count(r => r.Checked);

    /// <summary>以当前勾选的项执行卸载；一项都没勾时默认不调用底层、直接提示（allowEmpty=true 表示只运行软件自带的卸载程序）。</summary>
    public Task<UninstallOutcome> UninstallAsync(IAppBackend backend, SoftwareGroup group, CancellationToken ct, bool allowEmpty = false)
    {
        var selected = SelectedItems;
        if (selected.Count == 0 && !allowEmpty)
            return Task.FromResult(new UninstallOutcome(false, new RemovalResult(0, 0, [], null), "没有勾选任何残留项。"));
        return backend.UninstallAsync(group, selected, ct);
    }

    private static LeftoverRow ToRow(LeftoverItem i) => new(
        i, TypeName(i.Type), CategoryName(i.Category), SizeText(i.SizeBytes),
        i.Category switch { LeftoverCategory.Shared => "shared", LeftoverCategory.Personal => "personal", _ => null });

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
