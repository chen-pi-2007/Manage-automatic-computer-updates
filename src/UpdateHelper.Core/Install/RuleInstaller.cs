using System.ComponentModel;
using UpdateHelper.Core.Rules;
using UpdateHelper.Core.Security;
using UpdateHelper.Core.Updates;

namespace UpdateHelper.Core.Install;

/// <summary>
/// 安装规则库（YAML 规则 update.latest）里的软件：下载 → 校验签名 → 静默运行安装包。
/// 签名校验不通过的文件当场删除、绝不运行。流程见计划 5 Task 5 的表格。
/// </summary>
public sealed class RuleInstaller(
    RuleSet rules,
    IDownloader downloader,
    ISignatureVerifier verifier,
    IProcessRunner runner,
    string downloadDirectory) : IPackageInstaller
{
    public static string DefaultDownloadDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHelper", "downloads");

    private const string NoElevation = "没有获得管理员授权（在确认框里点了“否”）";

    /// <summary>用完整路径：裸文件名会先查当前用户可写的 App Paths 和当前目录，可能被同名程序冒充。</summary>
    private static readonly string MsiExec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    public string Name => "规则库";

    public async Task<InstallerReport> UpgradeAsync(string packageId, string targetVersion, InstallScopeHint scope,
        IProgress<double>? progress, CancellationToken cancellationToken)
    {
        // 1～3：规则检查，全部在下载之前
        if (!packageId.StartsWith(RuleUpdateSource.PackagePrefix, StringComparison.Ordinal))
            return Fail($"不是规则库的包：{packageId}");
        var ruleId = packageId[RuleUpdateSource.PackagePrefix.Length..];
        var rule = rules.Rules.FirstOrDefault(r => r.Id == ruleId);
        if (rule is null) return Fail($"找不到规则：{ruleId}");
        if (rule.Update?.Latest is not { } latest) return Fail("规则没有写最新版本");
        if (!string.Equals(latest.Version, targetVersion, StringComparison.OrdinalIgnoreCase))
            return Fail($"可用版本已经变了（规则里现在是 {latest.Version}，检查时是 {targetVersion}），请重新检查更新");
        if (string.IsNullOrWhiteSpace(rule.Update.Signer)) return Fail("规则没有写签名者，不能安装");
        if (string.IsNullOrWhiteSpace(rule.Update.SilentArgs)) return Fail("规则没有写静默安装参数，不能自动安装");

        // MSI 的参数只放行白名单：TRANSFORMS=、PATCH= 等会让 msiexec 加载没经过签名校验的文件
        var isMsi = new Uri(latest.Url).AbsolutePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
        if (isMsi && MsiArguments.FindUnsafe(rule.Update.SilentArgs) is { } unsafeArg)
            return Fail($"规则里的 MSI 安装参数不安全（{unsafeArg}），不能安装");

        // 4～5：下载（取消向外抛，交给 UpdateExecutor 记为"已取消"）
        var file = Path.Combine(downloadDirectory, $"{ruleId}-{SafeName(latest.Version)}{(isMsi ? ".msi" : ".exe")}");
        var downloadProgress = progress is null ? null : new ScaledProgress(progress, 0.5);
        try
        {
            await downloader.DownloadAsync(latest.Url, file, downloadProgress, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"下载失败：{ex.Message}");
        }

        // 从签名校验开始一直到安装程序结束，锁住安装包：别的程序只能读，不能改写、替换或删除，
        // 避免"校验的是原版、运行的是被换掉的文件"
        FileStream fileLock;
        try
        {
            fileLock = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(file);
            return Fail($"无法锁定安装包（{ex.Message}），没有运行");
        }

        int exitCode;
        using (fileLock)
        {
            // 6：签名校验不通过 → 删除，绝不运行
            var check = verifier.Verify(file, rule.Update.Signer);
            if (!check.Trusted)
            {
                fileLock.Dispose();
                TryDelete(file);
                return Fail($"安装包没有通过签名校验：{check.Message}。文件已删除，没有运行");
            }

            cancellationToken.ThrowIfCancellationRequested();   // 运行前最后一次响应取消

            // 7～10：运行安装包（启动后不再响应取消）
            try
            {
                exitCode = isMsi
                    ? await runner.RunAsync(MsiExec, $"/i \"{file}\" {rule.Update.SilentArgs}", CancellationToken.None)
                    : await runner.RunAsync(file, rule.Update.SilentArgs, CancellationToken.None);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return Fail(NoElevation);
            }
        }
        progress?.Report(1.0);

        return exitCode switch
        {
            0 => new InstallerReport(true, false, null, null),
            3010 or 1641 => new InstallerReport(true, true, null, null),
            1223 or 1602 => Fail(NoElevation),
            _ => new InstallerReport(false, false, $"安装程序返回错误（退出码 {exitCode}）", (uint)exitCode),
        };
    }

    private static InstallerReport Fail(string message) => new(false, false, message, null);

    private static string SafeName(string version) =>
        string.Concat(version.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>把下载进度（0～1）压缩到总进度的前一部分。</summary>
    private sealed class ScaledProgress(IProgress<double> inner, double share) : IProgress<double>
    {
        public void Report(double value) => inner.Report(value * share);
    }
}
