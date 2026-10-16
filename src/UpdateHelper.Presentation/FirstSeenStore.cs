using System.Text;
using System.Text.Json;

namespace UpdateHelper.Presentation;

/// <summary>
/// 记录每个"包 id + 版本"第一次被发现的时间，供观察期使用（spec 第 5 节）。
/// 读取永不抛异常（文件缺失或损坏当作空）；写入失败也不抛异常，下次检查时再记。
/// </summary>
public sealed class FirstSeenStore(string filePath, TimeProvider? clock = null)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHelper", "first-seen.json");

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public DateTimeOffset? Get(string packageId, string version) =>
        Load().TryGetValue(Key(packageId, version), out var t) ? t : null;

    public void Record(IEnumerable<(string PackageId, string Version)> seen)
    {
        var data = Load();
        var now = _clock.GetUtcNow();
        var changed = false;
        foreach (var (id, version) in seen)
            changed |= data.TryAdd(Key(id, version), now);
        if (!changed) return;

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
            var temp = filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(data), new UTF8Encoding(false));
            File.Move(temp, filePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Key(string packageId, string version) => $"{packageId.ToLowerInvariant()}|{version}";

    private Dictionary<string, DateTimeOffset> Load()
    {
        try
        {
            if (!File.Exists(filePath)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, DateTimeOffset>>(File.ReadAllText(filePath)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }
    }
}
