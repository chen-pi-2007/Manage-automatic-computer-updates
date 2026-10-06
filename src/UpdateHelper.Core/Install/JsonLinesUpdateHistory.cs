using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateHelper.Core.Install;

/// <summary>更新历史：每条记录一行 JSON，追加写入。坏行在读取时跳过。</summary>
public sealed class JsonLinesUpdateHistory(string filePath) : IUpdateHistory
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHelper", "history.jsonl");

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 中文原样写入，文件可直接阅读
        Converters = { new JsonStringEnumConverter() },
    };

    public void Append(HistoryRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        File.AppendAllText(filePath, JsonSerializer.Serialize(record, Options) + "\n", new UTF8Encoding(false));
    }

    public IReadOnlyList<HistoryRecord> ReadAll()
    {
        if (!File.Exists(filePath)) return [];

        var result = new List<HistoryRecord>();
        foreach (var line in File.ReadLines(filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                if (JsonSerializer.Deserialize<HistoryRecord>(line, Options) is { } record) result.Add(record);
            }
            catch (JsonException)
            {
                // 坏行跳过（Review Focus 4）
            }
        }
        return result;
    }
}
