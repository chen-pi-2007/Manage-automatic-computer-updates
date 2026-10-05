using UpdateHelper.Core.Scanning;

namespace UpdateHelper.Core.Tests;

public class CommandLineParserTests
{
    [Theory]
    // 带引号
    [InlineData("\"C:\\Program Files\\Tencent\\QQNT\\QQ.exe\" /background", @"C:\Program Files\Tencent\QQNT\QQ.exe")]
    // 不带引号、路径含空格（Review Focus 3）
    [InlineData(@"C:\Program Files\Docker\Docker\Docker Desktop.exe", @"C:\Program Files\Docker\Docker\Docker Desktop.exe")]
    [InlineData(@"C:\Program Files (x86)\pcsuite\pcsuite.exe --openAsHidden", @"C:\Program Files (x86)\pcsuite\pcsuite.exe")]
    // 不带引号、无空格
    [InlineData(@"C:\Windows\system32\SecurityHealthSystray.exe", @"C:\Windows\system32\SecurityHealthSystray.exe")]
    // 服务常见的 svchost
    [InlineData(@"C:\Windows\system32\svchost.exe -k netsvcs -p", @"C:\Windows\system32\svchost.exe")]
    // rundll32：真正的主人是后面的 dll
    [InlineData(@"rundll32.exe C:\Tools\helper.dll,Start", @"C:\Tools\helper.dll")]
    [InlineData("C:\\Windows\\System32\\rundll32.exe \"C:\\My Tools\\h.dll\",Run", @"C:\My Tools\h.dll")]
    public void Extracts_program_path(string command, string expected)
        => Assert.Equal(expected, CommandLineParser.ExtractExecutable(command), ignoreCase: true);

    [Fact]
    public void Expands_environment_variables()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles")!;
        Assert.Equal(Path.Combine(pf, @"App\a.exe"),
            CommandLineParser.ExtractExecutable(@"%ProgramFiles%\App\a.exe -x"), ignoreCase: true);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]          // 只有半个引号
    [InlineData("\"\"")]        // 空引号
    public void Returns_null_for_garbage(string? command)
        => Assert.Null(CommandLineParser.ExtractExecutable(command));

    [Fact]
    public void Falls_back_to_first_token_without_exe_suffix()
        => Assert.Equal(@"C:\tools\run.cmd", CommandLineParser.ExtractExecutable(@"C:\tools\run.cmd arg1"));
}
