using UpdateHelper.Core.Rules;

namespace UpdateHelper.Core.Tests;

public class RuleParserMultiTests
{
    [Fact]
    public void Single_document_yields_one_rule()
    {
        var yaml = "id: a\nname: A\nmatch:\n  displayName: \"A\"\n";
        var results = RuleParser.ParseAll(yaml, "a.yaml");
        var r = Assert.Single(results);
        Assert.True(r.Ok);
        Assert.Equal("a", r.Rule!.Id);
    }

    [Fact]
    public void Multiple_documents_split_on_separator()
    {
        var yaml = """
            id: a
            name: A
            match:
              displayName: "A"
            ---
            id: b
            name: B
            match:
              displayName: "B"
            """;
        var results = RuleParser.ParseAll(yaml, "multi.yaml");
        Assert.Equal(new[] { "a", "b" }, results.Where(x => x.Ok).Select(x => x.Rule!.Id));
    }

    [Fact]
    public void One_invalid_document_is_skipped_others_load()
    {
        var yaml = """
            id: a
            name: A
            match:
              displayName: "A"
            ---
            name: 缺了id
            match:
              displayName: "B"
            ---
            id: c
            name: C
            match:
              displayName: "C"
            """;
        var results = RuleParser.ParseAll(yaml, "multi.yaml");
        Assert.Equal(new[] { "a", "c" }, results.Where(x => x.Ok).Select(x => x.Rule!.Id));
        Assert.Contains(results, x => !x.Ok && x.Errors.Any(e => e.Contains("缺少 id")));
    }

    [Fact]
    public void Multi_doc_does_not_split_on_dashes_inside_string()   // Review Focus 1
    {
        // explain 值里含有 "---"，绝不能被当成文档分隔符切开
        var yaml = """
            id: a
            name: A
            match:
              displayName: "A"
            background:
              - service: "svc"
                explain: "分隔线 --- 不是文档边界"
            """;
        var results = RuleParser.ParseAll(yaml, "a.yaml");
        var r = Assert.Single(results);
        Assert.True(r.Ok);
        Assert.Contains("---", r.Rule!.Background[0].Explain);
    }

    [Fact]
    public void Empty_text_reports_empty_error()
    {
        var results = RuleParser.ParseAll("   \n", "empty.yaml");
        Assert.All(results, x => Assert.False(x.Ok));
        Assert.Contains(results, x => x.Errors.Any(e => e.Contains("空")));
    }
}
