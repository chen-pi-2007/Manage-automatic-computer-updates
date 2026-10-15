using System.Text;
using System.Text.Json;
using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Rules.ThirdParty;

public enum ThirdPartySyncStatus { Updated, NetworkFailed, Empty }

public sealed record ThirdPartySourceState(DateTimeOffset When, ThirdPartySyncStatus Status, int RuleCount, string? Detail);

/// <summary>按源下载 YAML 到各自缓存目录（不验签），写每源状态；某源失败只跳过它并保留旧缓存。</summary>
public sealed class ThirdPartySyncService(IDownloader downloader, RuleSourceStore store, Func<DateTimeOffset>? clock = null)
{
    public const string RulesFileName = "rules.yaml";
    private const string StateFileName = "source-state.json";
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.Now);

    public async Task SyncAsync(IEnumerable<ThirdPartySource> sources, CancellationToken ct = default)
    {
        foreach (var source in sources)
        {
            ct.ThrowIfCancellationRequested();
            await SyncOneAsync(source, ct);
        }
    }

    private async Task SyncOneAsync(ThirdPartySource source, CancellationToken ct)
    {
        var dir = store.CacheDirFor(source.Id);
        var temp = Path.Combine(Path.GetTempPath(), "uh-tp-" + Guid.NewGuid().ToString("N") + ".yaml");
        try
        {
            await downloader.DownloadAsync(source.Url, temp, null, ct);
            var text = await File.ReadAllTextAsync(temp, ct);
            if (string.IsNullOrWhiteSpace(text))
            {
                SaveState(dir, new ThirdPartySourceState(_clock(), ThirdPartySyncStatus.Empty,
                    ReadStateInDir(dir)?.RuleCount ?? 0, "下载到的内容为空"));
                return;
            }
            var count = RuleParser.ParseAll(text, source.Url).Count(p => p.Ok);
            if (count == 0)   // 非空但一条规则都解析不出（如 HTML 错误页）：保留上次好缓存
            {
                SaveState(dir, new ThirdPartySourceState(_clock(), ThirdPartySyncStatus.Empty,
                    ReadStateInDir(dir)?.RuleCount ?? 0, "下载内容未解析出任何规则"));
                return;
            }
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, RulesFileName), text, new UTF8Encoding(false));   // 原样存
            SaveState(dir, new ThirdPartySourceState(_clock(), ThirdPartySyncStatus.Updated, count, null));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }   // 仅真正的取消向上抛；超时（ct 未取消）按失败隔离
        catch (Exception ex)   // 网络/IO 失败：保留旧缓存，只记状态
        {
            SaveState(dir, new ThirdPartySourceState(_clock(), ThirdPartySyncStatus.NetworkFailed,
                ReadStateInDir(dir)?.RuleCount ?? 0, ex.Message));
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static ThirdPartySourceState? ReadState(RuleSourceStore store, string id) =>
        ReadStateInDir(store.CacheDirFor(id));

    private static ThirdPartySourceState? ReadStateInDir(string dir)
    {
        try
        {
            var path = Path.Combine(dir, StateFileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ThirdPartySourceState>(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void SaveState(string dir, ThirdPartySourceState state)
    {
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, StateFileName), JsonSerializer.Serialize(state));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
