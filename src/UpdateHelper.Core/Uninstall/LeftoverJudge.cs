namespace UpdateHelper.Core.Uninstall;

/// <summary>按 spec 第 7 节的表格给一条残留分类、决定默认是否勾选、给出提示。纯逻辑。</summary>
public static class LeftoverJudge
{
    public static LeftoverItem Classify(LeftoverType type, string path, long? sizeBytes,
        bool ruleOwned, bool rulePersonal, bool ruleShared,
        bool isInstallLocation, bool sharedWithOthers, bool nameMatchOnly)
    {
        // 安全优先：个人数据 > 多软件共用 > 确定属于 > 可能属于
        if (rulePersonal)
            return Make(type, path, sizeBytes, LeftoverCategory.Personal, false,
                "个人数据（聊天记录/存档/文档等），删除可能造成不可恢复的损失");
        if (ruleShared || sharedWithOthers)
            return Make(type, path, sizeBytes, LeftoverCategory.Shared, false,
                "其他已安装的软件也在用这个位置，删除可能影响它们");
        if (ruleOwned || isInstallLocation
            || type is LeftoverType.Service or LeftoverType.ScheduledTask or LeftoverType.StartupEntry)
            return Make(type, path, sizeBytes, LeftoverCategory.Owned, true, null);
        return Make(type, path, sizeBytes, LeftoverCategory.Possible, false,
            "按名称/发布者推测，可能属于这个软件");
    }

    private static LeftoverItem Make(LeftoverType type, string path, long? size,
        LeftoverCategory category, bool defaultChecked, string? note) =>
        new(type, path, category, size, defaultChecked, note);
}
