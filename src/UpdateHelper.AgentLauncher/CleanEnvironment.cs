namespace UpdateHelper.AgentLauncher;

/// <summary>
/// 后台助手以管理员身份运行，它的环境变量不能来自用户：用户能改自己的环境变量，
/// 而 .NET 程序启动时会读取其中一部分。这里只保留白名单里的系统类变量，Path 只用系统级的值。
/// </summary>
public static class CleanEnvironment
{
    /// <summary>允许传给后台助手的变量（标准写法）。都是 Windows 自己设置的路径和系统信息，不含任何运行时配置。</summary>
    public static IReadOnlyList<string> AllowedNames { get; } =
    [
        "SystemRoot", "windir", "SystemDrive", "ComSpec", "PATHEXT", "OS",
        "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "PROCESSOR_LEVEL", "PROCESSOR_REVISION", "NUMBER_OF_PROCESSORS",
        "COMPUTERNAME", "USERNAME", "USERDOMAIN", "USERDOMAIN_ROAMINGPROFILE", "LOGONSERVER", "SESSIONNAME",
        "USERPROFILE", "HOMEDRIVE", "HOMEPATH", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP",
        "ProgramData", "ALLUSERSPROFILE", "PUBLIC",
        "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "CommonProgramFiles(x86)", "CommonProgramW6432",
        "Path",
    ];

    public static Dictionary<string, string> Build(IReadOnlyDictionary<string, string> current, string? machinePath, string systemRoot)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AllowedNames)
        {
            if (name == "Path") continue;
            var value = current.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
            if (value is not null) result[name] = value;
        }

        if (!result.ContainsKey("SystemRoot")) result["SystemRoot"] = systemRoot;
        result["Path"] = string.IsNullOrWhiteSpace(machinePath)
            ? $@"{systemRoot}\system32;{systemRoot};{systemRoot}\System32\Wbem"
            : machinePath;
        return result;
    }
}
