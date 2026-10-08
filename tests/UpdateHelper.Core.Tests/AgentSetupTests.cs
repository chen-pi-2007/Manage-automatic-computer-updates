using System.Xml.Linq;
using UpdateHelper.Core.Agent;

namespace UpdateHelper.Core.Tests;

public sealed class AgentSetupTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-agent-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private const string Sid = "S-1-5-21-4081559805-2983792979-1103478602-1001";

    [Fact]
    public void Task_xml_runs_installed_agent_as_this_user_with_highest_privileges()
    {
        var xml = XDocument.Parse(AgentSetup.BuildTaskXml(Sid, @"C:\Program Files\UpdateHelper\UpdateHelper.Agent.exe"));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        string Value(string path) => xml.Descendants(ns + path).Single().Value;

        Assert.Equal(Sid, Value("UserId"));
        Assert.Equal("InteractiveToken", Value("LogonType"));      // 登录用户身份，不是 SYSTEM
        Assert.Equal("HighestAvailable", Value("RunLevel"));
        Assert.Equal(@"C:\Program Files\UpdateHelper\UpdateHelper.Agent.exe", Value("Command"));
        Assert.Equal("--serve", Value("Arguments"));
        Assert.Equal("IgnoreNew", Value("MultipleInstancesPolicy"));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        // 只有这个用户能运行（读 + 执行），管理员和 SYSTEM 完全控制
        Assert.Equal($"D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;{Sid})", Value("SecurityDescriptor"));
        Assert.Empty(xml.Descendants(ns + "Triggers").SelectMany(t => t.Elements()));   // 没有触发器：只在 App 需要时启动
    }

    [Fact]
    public void Task_xml_escapes_paths()
    {
        var text = AgentSetup.BuildTaskXml(Sid, @"C:\A&B\<x>\UpdateHelper.Agent.exe");
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        Assert.Equal(@"C:\A&B\<x>\UpdateHelper.Agent.exe", XDocument.Parse(text).Descendants(ns + "Command").Single().Value);
    }

    [Theory]
    [InlineData(Sid, true)]
    [InlineData("S-1-5-18", false)]                 // SYSTEM 不是账户
    [InlineData("S-1-5-32-544", false)]             // Administrators 组
    [InlineData("not-a-sid", false)]
    [InlineData("", false)]
    [InlineData("S-1-5-21-1-2-3-1001)(A;;FA;;;WD", false)]   // 想往安全描述符里注入
    public void Only_account_sids_are_accepted(string sid, bool valid)
    {
        Assert.Equal(valid, AgentSetup.IsValidUserSid(sid));
    }

    [Fact]
    public void Deploy_copies_recursively_overwrites_and_writes_version()
    {
        var source = Path.Combine(_dir, "src");
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(Path.Combine(source, "rules"));
        File.WriteAllText(Path.Combine(source, "UpdateHelper.Agent.exe"), "new");
        File.WriteAllText(Path.Combine(source, "rules", "a.yaml"), "id: a");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "UpdateHelper.Agent.exe"), "old");

        AgentSetup.Deploy(source, target, "0.2.0");

        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "UpdateHelper.Agent.exe")));
        Assert.Equal("id: a", File.ReadAllText(Path.Combine(target, "rules", "a.yaml")));
        Assert.Equal("0.2.0", File.ReadAllText(Path.Combine(target, AgentPaths.VersionFileName)));
    }

    [Fact]
    public void Deploy_to_itself_only_writes_version()
    {
        File.WriteAllText(Path.Combine(_dir, "UpdateHelper.Agent.exe"), "x");
        AgentSetup.Deploy(_dir, _dir + Path.DirectorySeparatorChar, "0.2.0");
        Assert.Equal("x", File.ReadAllText(Path.Combine(_dir, "UpdateHelper.Agent.exe")));
        Assert.Equal("0.2.0", File.ReadAllText(Path.Combine(_dir, AgentPaths.VersionFileName)));
    }

    [Fact]
    public void Deploy_does_not_follow_directory_junctions()
    {
        var source = Path.Combine(_dir, "src");
        var outside = Path.Combine(_dir, "outside");
        var target = Path.Combine(_dir, "target");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "secret");
        File.WriteAllText(Path.Combine(source, "UpdateHelper.Agent.exe"), "new");

        // mklink /J 不需要特殊权限；失败就直接让测试失败，不能悄悄通过
        var link = Path.Combine(source, "link");
        using (var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{outside}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("无法启动 cmd.exe"))
        {
            var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0 || !Directory.Exists(link))
                Assert.Fail($"无法创建目录联接，测试无效: {output}");
        }

        try
        {
            AgentSetup.Deploy(source, target, "0.2.0");

            Assert.True(File.Exists(Path.Combine(target, "UpdateHelper.Agent.exe")));
            Assert.False(File.Exists(Path.Combine(target, "link", "secret.txt")));
        }
        finally
        {
            // 先非递归删除联接本身（只删链接，不动 outside 里的内容），否则清理时递归删除会失败
            Directory.Delete(link);
        }
    }

    [Fact]
    public void Status_is_outdated_when_version_file_missing()
    {
        File.WriteAllText(Path.Combine(_dir, AgentPaths.AgentExeName), "x");
        Assert.Equal(AgentStatus.Outdated, AgentSetup.GetStatus(_dir, "0.2.0", taskExists: true));
    }

    [Fact]
    public void Status_reflects_files_task_and_version()
    {
        Assert.Equal(AgentStatus.NotEnabled, AgentSetup.GetStatus(_dir, "0.2.0", taskExists: false));

        File.WriteAllText(Path.Combine(_dir, AgentPaths.AgentExeName), "x");
        File.WriteAllText(Path.Combine(_dir, AgentPaths.VersionFileName), "0.2.0");
        Assert.Equal(AgentStatus.Ready, AgentSetup.GetStatus(_dir, "0.2.0", taskExists: true));
        Assert.Equal(AgentStatus.Outdated, AgentSetup.GetStatus(_dir, "0.3.0", taskExists: true));
        Assert.Equal(AgentStatus.Broken, AgentSetup.GetStatus(_dir, "0.2.0", taskExists: false));   // 任务被删了

        File.Delete(Path.Combine(_dir, AgentPaths.AgentExeName));
        Assert.Equal(AgentStatus.Broken, AgentSetup.GetStatus(_dir, "0.2.0", taskExists: true));     // 程序被删了
    }
}
