using UpdateHelper.Core.Grouping;
using UpdateHelper.Core.Updates;
using static UpdateHelper.Core.Tests.Fakes;

namespace UpdateHelper.Core.Tests;

public class UpdateMatcherTests
{
    private static UpdateCandidate Candidate(params string[] codes)
        => new("Some.Id", "Some", null, "1.0", "2.0", codes);

    private static readonly IReadOnlyList<SoftwareGroup> Groups = SoftwareGrouper.Group(
    [
        Entry("QQ", "腾讯科技(深圳)有限公司", key: "QQ"),
        Entry("Git", "The Git Development Community", key: "Git_is1"),
        Entry("Python 3.10.2 (64-bit)", "Python Software Foundation", key: "{21b42743-c8f9-49d7-b8b6-b5855317c7ed}"),
        Entry("Python 3.10.2 Core Interpreter (64-bit)", "Python Software Foundation", hidden: true,
              key: "{C60FD5AC-367D-4E3A-A975-F157502AC30A}"),
    ], []).Groups;

    [Theory]
    [InlineData("qq", "QQ")]            // winget 给的是小写
    [InlineData("git_is1", "Git")]
    public void Matches_primary_key_ignoring_case(string code, string expected)
        => Assert.Equal(expected, UpdateMatcher.FindGroup(Candidate(code), Groups)?.Name);

    [Fact]
    public void Matches_component_key()   // Python 的 winget 包对应的是隐藏的核心组件
        => Assert.Equal("Python 3.10.2 (64-bit)",
            UpdateMatcher.FindGroup(Candidate("{c60fd5ac-367d-4e3a-a975-f157502ac30a}"), Groups)?.Name);

    [Fact]
    public void Primary_match_wins_over_component_match()
        => Assert.Equal("QQ", UpdateMatcher.FindGroup(Candidate("{c60fd5ac-367d-4e3a-a975-f157502ac30a}", "qq"), Groups)?.Name);

    [Fact]
    public void No_product_codes_returns_null()
        => Assert.Null(UpdateMatcher.FindGroup(Candidate(), Groups));

    [Fact]
    public void Unknown_product_code_returns_null()   // Review Focus 5
        => Assert.Null(UpdateMatcher.FindGroup(Candidate("not-installed"), Groups));
}
