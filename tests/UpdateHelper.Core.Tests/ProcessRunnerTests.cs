using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

/// <summary>只运行 Windows 自带的 cmd.exe /c exit N，不安装任何东西。</summary>
public class ProcessRunnerTests
{
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Theory]
    [InlineData(0)]
    [InlineData(1603)]   // MSI 常见的"安装出错"
    [InlineData(3010)]   // 需要重启
    public async Task Returns_exit_code(int code)
        => Assert.Equal(code, await new ProcessRunner().RunAsync(Cmd, $"/c exit {code}", CancellationToken.None));

    [Fact]
    public async Task Cancelled_before_start_does_not_start()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "ran.txt");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProcessRunner().RunAsync(Cmd, $"/c echo x > \"{marker}\"", cts.Token));

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task Cancellation_after_start_waits_for_the_process()
    {
        using var dir = new TempDir();
        var marker = Path.Combine(dir.Path, "done.txt");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        // 大约 2 秒后写文件再退出；200 毫秒时取消也必须等它跑完
        var code = await new ProcessRunner().RunAsync(Cmd, $"/c ping -n 3 127.0.0.1 >nul & echo x > \"{marker}\"", cts.Token);

        Assert.Equal(0, code);
        Assert.True(File.Exists(marker));
    }
}
