using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public class JsonLinesUpdateHistoryTests
{
    private static HistoryRecord Record(string name, ExecuteOutcome outcome = ExecuteOutcome.Succeeded)
        => new(new DateTimeOffset(2026, 10, 6, 20, 0, 0, TimeSpan.FromHours(8)),
               "Pkg." + name, name, "1.0", "1.1", outcome, "已从 1.0 更新到 1.1", false);

    [Fact]
    public void Appended_records_are_read_back_in_order()
    {
        using var dir = new TempDir();
        var history = new JsonLinesUpdateHistory(Path.Combine(dir.Path, "sub", "history.jsonl"));   // 目录不存在也能写

        history.Append(Record("微信"));
        history.Append(Record("QQ", ExecuteOutcome.Failed));

        var all = history.ReadAll();
        Assert.Equal(new[] { "微信", "QQ" }, all.Select(r => r.Name));
        Assert.Equal(Record("微信"), all[0]);
        Assert.Equal(ExecuteOutcome.Failed, all[1].Outcome);
    }

    [Fact]
    public void Missing_file_reads_as_empty()
    {
        using var dir = new TempDir();
        Assert.Empty(new JsonLinesUpdateHistory(Path.Combine(dir.Path, "none.jsonl")).ReadAll());
    }

    [Fact]
    public void Corrupted_lines_are_skipped_and_appending_still_works()   // Review Focus 4
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "history.jsonl");
        var history = new JsonLinesUpdateHistory(path);
        history.Append(Record("A"));
        File.AppendAllText(path, "这一行被手工改坏了\n{\"PackageId\":\n\n");
        history.Append(Record("B"));

        Assert.Equal(new[] { "A", "B" }, history.ReadAll().Select(r => r.Name));
    }

    [Fact]
    public void File_is_readable_text_with_chinese_and_enum_names()
    {
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, "history.jsonl");
        new JsonLinesUpdateHistory(path).Append(Record("微信", ExecuteOutcome.NeedsReboot));

        var text = File.ReadAllText(path);
        Assert.Contains("微信", text);
        Assert.Contains("\"NeedsReboot\"", text);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }
}
