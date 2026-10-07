using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpdateHelper.Presentation.Settings;

/// <summary>设置文件读写。读取永不抛异常：文件缺失或损坏都用默认值。</summary>
public sealed class SettingsStore(string filePath)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UpdateHelper", "settings.json");

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(filePath)) return new AppSettings();
            return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(filePath), Options) ?? new AppSettings()).Normalized();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new AppSettings();
        }
    }

    /// <summary>先写临时文件再替换，写到一半断电也不会留下坏文件。</summary>
    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(filePath))!);
        var temp = filePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings.Normalized(), Options), new UTF8Encoding(false));
        File.Move(temp, filePath, overwrite: true);
    }
}
