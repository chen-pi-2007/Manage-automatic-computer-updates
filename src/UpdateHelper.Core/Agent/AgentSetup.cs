using System.Security;
using System.Security.Principal;

namespace UpdateHelper.Core.Agent;

/// <summary>后台助手的状态（App 设置页显示）。</summary>
public enum AgentStatus
{
    /// <summary>没有启用：更新时每次弹管理员确认框</summary>
    NotEnabled,
    /// <summary>已启用，版本一致</summary>
    Ready,
    /// <summary>已启用，但安装目录里的版本和当前程序不一致</summary>
    Outdated,
    /// <summary>程序文件或计划任务只剩一样（被删除或被杀毒软件拦截）</summary>
    Broken,
}

/// <summary>启用后台助手要用到的纯逻辑：任务 XML、复制部署、状态判断。真正的注册在 Agent 的 --install 里。</summary>
public static class AgentSetup
{
    public static string BuildTaskXml(string userSid, string agentExe)
    {
        if (!IsValidUserSid(userSid)) throw new ArgumentException("不是有效的账户 SID", nameof(userSid));
        var exe = SecurityElement.Escape(agentExe);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>更新管理小助手的后台助手：在你点更新时以管理员身份安装，免去每次的确认框。只运行 Program Files 里的程序，不会开机自启。</Description>
                <SecurityDescriptor>D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;{userSid})</SecurityDescriptor>
              </RegistrationInfo>
              <Triggers />
              <Principals>
                <Principal id="Author">
                  <UserId>{userSid}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Hidden>true</Hidden>
                <ExecutionTimeLimit>PT4H</ExecutionTimeLimit>
                <Priority>7</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{exe}</Command>
                  <Arguments>--serve</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    /// <summary>只接受本机或域账户的 SID（S-1-5-21-…），拒绝 SYSTEM、内置组和任何带别的字符的输入。</summary>
    public static bool IsValidUserSid(string sid)
    {
        if (string.IsNullOrEmpty(sid) || sid.Any(c => !(char.IsAsciiDigit(c) || c is 'S' or '-'))) return false;
        try
        {
            var parsed = new SecurityIdentifier(sid);
            return parsed.IsAccountSid() && parsed.Value == sid;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static void Deploy(string sourceDirectory, string targetDirectory, string version)
    {
        var source = Path.GetFullPath(sourceDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar);
        Directory.CreateDirectory(target);

        if (!string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
        {
            // 以管理员身份复制时不跟随联接/符号链接，避免把源目录外的文件复制进 Program Files
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                AttributesToSkip = FileAttributes.ReparsePoint,
                IgnoreInaccessible = false,
            };
            foreach (var file in Directory.EnumerateFiles(source, "*", options))
            {
                var relative = Path.GetRelativePath(source, file);
                var destination = Path.Combine(target, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(file, destination, overwrite: true);
            }
        }

        File.WriteAllText(Path.Combine(target, AgentPaths.VersionFileName), version);
    }

    public static AgentStatus GetStatus(string installDirectory, string expectedVersion, bool taskExists)
    {
        var exeExists = File.Exists(Path.Combine(installDirectory, AgentPaths.AgentExeName));
        if (!exeExists && !taskExists) return AgentStatus.NotEnabled;
        if (!exeExists || !taskExists) return AgentStatus.Broken;

        string? installed;
        try
        {
            installed = File.ReadAllText(Path.Combine(installDirectory, AgentPaths.VersionFileName)).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            installed = null;
        }
        return installed == expectedVersion ? AgentStatus.Ready : AgentStatus.Outdated;
    }
}
