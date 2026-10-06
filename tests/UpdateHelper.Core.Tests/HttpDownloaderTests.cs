using System.Net;
using System.Net.Sockets;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

/// <summary>在本机 localhost 起一个临时 HTTP 服务来测试下载，不访问外网。</summary>
public sealed class HttpDownloaderTests : IDisposable
{
    private readonly HttpListener _server = new();
    private readonly string _baseUrl;
    private readonly byte[] _payload = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();

    public HttpDownloaderTests()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        _baseUrl = $"http://localhost:{port}/";
        _server.Prefixes.Add(_baseUrl);   // localhost 前缀不需要管理员权限
        _server.Start();
        _ = Task.Run(Serve);
    }

    private async Task Serve()
    {
        while (_server.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _server.GetContextAsync(); }
            catch (Exception) { return; }   // 服务关闭

            if (ctx.Request.Url!.AbsolutePath == "/setup.exe")
            {
                ctx.Response.ContentLength64 = _payload.Length;
                await ctx.Response.OutputStream.WriteAsync(_payload);
            }
            else
            {
                ctx.Response.StatusCode = 404;
            }
            ctx.Response.Close();
        }
    }

    public void Dispose() => _server.Close();

    // 测试服务器在本机：不能走系统代理。开发机若开着 Clash 等代理，HttpClient 会把 localhost 请求也转给代理而卡住
    private static HttpDownloader Downloader() => new(new HttpClient(new HttpClientHandler { UseProxy = false }));

    private sealed class SyncProgress(List<double> seen) : IProgress<double>
    {
        public void Report(double value) => seen.Add(value);
    }

    [Fact]
    public async Task Downloads_complete_file_and_reports_progress()
    {
        using var dir = new TempDir();
        var dest = Path.Combine(dir.Path, "downloads", "setup.exe");   // 目录不存在也能下载
        var seen = new List<double>();

        await Downloader().DownloadAsync(_baseUrl + "setup.exe", dest, new SyncProgress(seen), CancellationToken.None);

        Assert.Equal(_payload, File.ReadAllBytes(dest));
        Assert.Equal(1.0, seen[^1]);
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Http_error_leaves_no_files()   // Review Focus 3
    {
        using var dir = new TempDir();
        var dest = Path.Combine(dir.Path, "missing.exe");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            Downloader().DownloadAsync(_baseUrl + "missing.exe", dest, null, CancellationToken.None));

        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Cancelled_download_leaves_no_files()   // Review Focus 3
    {
        using var dir = new TempDir();
        var dest = Path.Combine(dir.Path, "setup.exe");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Downloader().DownloadAsync(_baseUrl + "setup.exe", dest, null, cts.Token));

        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public async Task Existing_file_is_replaced()
    {
        using var dir = new TempDir();
        var dest = dir.Write("setup.exe", "旧内容");
        await Downloader().DownloadAsync(_baseUrl + "setup.exe", dest, null, CancellationToken.None);
        Assert.Equal(_payload, File.ReadAllBytes(dest));
    }
}
