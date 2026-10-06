using System.ComponentModel;
using UpdateHelper.Core.Install;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Security;

namespace UpdateHelper.Core.Tests;

public sealed class RuleInstallerTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();

    private static readonly string SignedFile =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");

    // —— 假实现 ——
    private sealed class FakeDownloader(Func<string, byte[]>? content = null, Exception? error = null) : IDownloader
    {
        public List<string> Urls { get; } = [];
        public Task DownloadAsync(string url, string destinationPath, IProgress<double>? progress, CancellationToken ct)
        {
            Urls.Add(url);
            ct.ThrowIfCancellationRequested();
            if (error is not null) throw error;
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllBytes(destinationPath, (content ?? (_ => [1, 2, 3]))(url));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeVerifier(bool trusted) : ISignatureVerifier
    {
        public List<(string Path, string Signer)> Calls { get; } = [];
        public SignatureCheck Verify(string filePath, string expectedSigner)
        {
            Calls.Add((filePath, expectedSigner));
            return trusted ? new SignatureCheck(true, expectedSigner, "签名有效") : new SignatureCheck(false, "Evil Corp", "签名者不对");
        }
    }

    private sealed class FakeRunner(Func<int>? behave = null) : IProcessRunner
    {
        public List<(string File, string Args)> Calls { get; } = [];
        public Task<int> RunAsync(string fileName, string arguments, CancellationToken ct)
        {
            Calls.Add((fileName, arguments));
            return Task.FromResult((behave ?? (() => 0))());
        }
    }

    private static RuleSet Rules(string version = "12.1.0.24000", string url = "https://example.com/wps_setup.exe",
        string? signer = "Zhuhai Kingsoft Office Software Co., Ltd", string? silentArgs = "/S")
        => new([new Rule("kingsoft.wps", "WPS Office", new RuleMatch("WPS Office*", null), null,
            new UpdateRule(null, new UpdateLatest(version, url), signer, silentArgs, RiskLevel.Normal), [], [])], [], []);

    private RuleInstaller Make(RuleSet rules, FakeDownloader downloader, ISignatureVerifier verifier, FakeRunner runner)
        => new(rules, downloader, verifier, runner, _dir.Path);

    private static Task<InstallerReport> Upgrade(RuleInstaller installer, string id = "rule:kingsoft.wps",
        string target = "12.1.0.24000", CancellationToken ct = default)
        => installer.UpgradeAsync(id, target, InstallScopeHint.Machine, null, ct);

    private string Expected(string ext = ".exe") => Path.Combine(_dir.Path, "kingsoft.wps-12.1.0.24000" + ext);

    [Fact]
    public async Task Happy_path_downloads_verifies_and_runs_silently()
    {
        var downloader = new FakeDownloader();
        var verifier = new FakeVerifier(true);
        var runner = new FakeRunner();

        var report = await Upgrade(Make(Rules(), downloader, verifier, runner));

        Assert.Equal(new InstallerReport(true, false, null, null), report);
        Assert.Equal(new[] { "https://example.com/wps_setup.exe" }, downloader.Urls);
        Assert.Equal((Expected(), "Zhuhai Kingsoft Office Software Co., Ltd"), Assert.Single(verifier.Calls));
        Assert.Equal((Expected(), "/S"), Assert.Single(runner.Calls));
        Assert.True(File.Exists(Expected()));   // 成功后保留，留作以后回退
    }

    [Fact]
    public async Task Msi_is_run_through_msiexec()
    {
        var runner = new FakeRunner();
        await Upgrade(Make(Rules(url: "https://example.com/setup.msi", silentArgs: "/qn /norestart"),
            new FakeDownloader(), new FakeVerifier(true), runner));

        var call = Assert.Single(runner.Calls);
        Assert.Equal("msiexec.exe", call.File);
        Assert.Equal($"/i \"{Expected(".msi")}\" /qn /norestart", call.Args);
    }

    [Fact]
    public async Task Failed_signature_deletes_file_and_never_runs()   // Review Focus 2
    {
        var runner = new FakeRunner();
        var report = await Upgrade(Make(Rules(), new FakeDownloader(), new FakeVerifier(false), runner));

        Assert.False(report.Success);
        Assert.Equal("安装包没有通过签名校验：签名者不对。文件已删除，没有运行", report.ErrorMessage);
        Assert.Empty(runner.Calls);
        Assert.False(File.Exists(Expected()));
    }

    [Fact]
    public async Task Tampered_installer_is_caught_by_the_real_verifier()   // Review Focus 1（真实签名校验）
    {
        var tampered = File.ReadAllBytes(SignedFile);
        tampered[tampered.Length / 2] ^= 0xFF;
        var runner = new FakeRunner();

        var report = await Upgrade(Make(Rules(signer: "Microsoft Corporation"),
            new FakeDownloader(_ => tampered), new AuthenticodeVerifier(), runner));

        Assert.False(report.Success);
        Assert.Contains("文件可能被篡改过", report.ErrorMessage);
        Assert.Empty(runner.Calls);
        Assert.False(File.Exists(Expected()));
    }

    [Fact]
    public async Task Genuine_installer_passes_the_real_verifier()
    {
        var runner = new FakeRunner();
        var report = await Upgrade(Make(Rules(signer: "Microsoft Corporation"),
            new FakeDownloader(_ => File.ReadAllBytes(SignedFile)), new AuthenticodeVerifier(), runner));

        Assert.True(report.Success);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task Version_mismatch_is_refused_before_downloading()
    {
        var downloader = new FakeDownloader();
        var report = await Upgrade(Make(Rules(version: "12.1.0.25000"), downloader, new FakeVerifier(true), new FakeRunner()));

        Assert.False(report.Success);
        Assert.Equal("可用版本已经变了（规则里现在是 12.1.0.25000，检查时是 12.1.0.24000），请重新检查更新", report.ErrorMessage);
        Assert.Empty(downloader.Urls);
    }

    [Theory]
    [InlineData(null, "/S", "规则没有写签名者，不能安装")]
    [InlineData("Kingsoft", null, "规则没有写静默安装参数，不能自动安装")]
    public async Task Incomplete_rule_is_refused_before_downloading(string? signer, string? silentArgs, string message)
    {
        var downloader = new FakeDownloader();
        var report = await Upgrade(Make(Rules(signer: signer, silentArgs: silentArgs), downloader, new FakeVerifier(true), new FakeRunner()));

        Assert.Equal(message, report.ErrorMessage);
        Assert.Empty(downloader.Urls);
    }

    [Theory]
    [InlineData("Tencent.QQ.NT", "不是规则库的包：Tencent.QQ.NT")]
    [InlineData("rule:no.such", "找不到规则：no.such")]
    public async Task Unknown_package_is_refused(string id, string message)
        => Assert.Equal(message, (await Upgrade(Make(Rules(), new FakeDownloader(), new FakeVerifier(true), new FakeRunner()), id)).ErrorMessage);

    [Fact]
    public async Task Download_error_is_a_failure_and_nothing_runs()
    {
        var runner = new FakeRunner();
        var report = await Upgrade(Make(Rules(), new FakeDownloader(error: new HttpRequestException("404 Not Found")),
            new FakeVerifier(true), runner));

        Assert.Equal("下载失败：404 Not Found", report.ErrorMessage);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Cancelled_download_propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Upgrade(Make(Rules(), new FakeDownloader(), new FakeVerifier(true), new FakeRunner()), ct: cts.Token));
    }

    [Theory]
    [InlineData(3010, true, true, null)]
    [InlineData(1641, true, true, null)]
    [InlineData(1602, false, false, "没有获得管理员授权（在确认框里点了“否”）")]
    [InlineData(1223, false, false, "没有获得管理员授权（在确认框里点了“否”）")]   // Review Focus 4
    [InlineData(1603, false, false, "安装程序返回错误（退出码 1603）")]
    public async Task Exit_codes_are_interpreted(int code, bool success, bool reboot, string? message)
    {
        var report = await Upgrade(Make(Rules(), new FakeDownloader(), new FakeVerifier(true), new FakeRunner(() => code)));
        Assert.Equal(success, report.Success);
        Assert.Equal(reboot, report.RebootRequired);
        Assert.Equal(message, report.ErrorMessage);
    }

    [Fact]
    public async Task Declined_elevation_at_start_is_reported()   // Review Focus 4
    {
        var report = await Upgrade(Make(Rules(), new FakeDownloader(), new FakeVerifier(true),
            new FakeRunner(() => throw new Win32Exception(1223))));
        Assert.Equal("没有获得管理员授权（在确认框里点了“否”）", report.ErrorMessage);
    }
}
