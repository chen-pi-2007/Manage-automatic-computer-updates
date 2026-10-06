namespace UpdateHelper.Core.Install;

public interface IDownloader
{
    /// <summary>把 url 下载到 destinationPath。失败或取消时抛异常，并且不留下任何文件。</summary>
    Task DownloadAsync(string url, string destinationPath, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>用 HttpClient 下载：先写 .part，完整后再改名，避免留下半截文件被当成完整安装包。</summary>
public sealed class HttpDownloader(HttpClient? client = null) : IDownloader
{
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(30) };
    private readonly HttpClient _client = client ?? SharedClient;

    public async Task DownloadAsync(string url, string destinationPath, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destinationPath))!);
        var partPath = destinationPath + ".part";
        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;

            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = File.Create(partPath))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    done += read;
                    if (total is > 0) progress?.Report((double)done / total.Value);
                }
            }

            File.Move(partPath, destinationPath, overwrite: true);
            progress?.Report(1.0);
        }
        catch
        {
            TryDelete(partPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
