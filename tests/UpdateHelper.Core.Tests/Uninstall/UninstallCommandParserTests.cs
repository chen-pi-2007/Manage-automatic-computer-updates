using UpdateHelper.Core.Scanning;
using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests.Uninstall;

public sealed class UninstallCommandParserTests
{
    private sealed class FakeFiles(params string[] existing) : IFileProbe
    {
        private readonly HashSet<string> _files = new(existing, StringComparer.OrdinalIgnoreCase);
        public bool FileExists(string path) => _files.Contains(path);
        public bool DirectoryExists(string path) => false;
        public IReadOnlyList<string> GetChildDirectories(string parent) => [];
        public long? DirectorySize(string path) => null;
    }

    private static UninstallEntry Entry(string? uninstall, string? quiet = null) => new(
        "k", UninstallHive.LocalMachine64, "Demo", "1.0", "Pub", null,
        uninstall, quiet, false, null, null, null);

    [Fact]
    public void Parse_prefers_quiet_uninstall_string_when_available()
    {
        var files = new FakeFiles(@"C:\P\unins000.exe");
        var cmd = UninstallCommandParser.Parse(
            Entry("\"C:\\P\\unins000.exe\"", "\"C:\\P\\unins000.exe\" /VERYSILENT"), files, preferSilent: true);

        Assert.NotNull(cmd);
        Assert.Equal(@"C:\P\unins000.exe", cmd.ExecutablePath);
        Assert.Equal("/VERYSILENT", cmd.Arguments);
        Assert.True(cmd.Silent);
        Assert.False(cmd.IsMsi);
    }

    [Fact]
    public void Parse_splits_quoted_exe_and_args()
    {
        var files = new FakeFiles(@"C:\P\unins000.exe");
        var cmd = UninstallCommandParser.Parse(Entry("\"C:\\P\\unins000.exe\" /x"), files, preferSilent: true);

        Assert.NotNull(cmd);
        Assert.Equal(@"C:\P\unins000.exe", cmd.ExecutablePath);
        Assert.Equal("/x", cmd.Arguments);
        Assert.False(cmd.Silent);   // 没有静默串，不猜 /S
    }

    [Fact]
    public void Parse_handles_unquoted_path_with_spaces()
    {
        var files = new FakeFiles(@"C:\Program Files\App\unins.exe");
        var cmd = UninstallCommandParser.Parse(Entry(@"C:\Program Files\App\unins.exe /S"), files, preferSilent: false);

        Assert.NotNull(cmd);
        Assert.Equal(@"C:\Program Files\App\unins.exe", cmd.ExecutablePath);
        Assert.Equal("/S", cmd.Arguments);
    }

    [Fact]
    public void Parse_expands_environment_variables()
    {
        var pf = Environment.GetEnvironmentVariable("ProgramFiles")!;
        var exe = Path.Combine(pf, "App", "u.exe");
        var files = new FakeFiles(exe);
        var cmd = UninstallCommandParser.Parse(Entry("\"%ProgramFiles%\\App\\u.exe\" /x"), files, preferSilent: false);

        Assert.NotNull(cmd);
        Assert.Equal(exe, cmd.ExecutablePath);
    }

    [Fact]
    public void Parse_detects_msi()
    {
        var files = new FakeFiles();
        var guid = "{12345678-1234-1234-1234-123456789ABC}";
        var cmd = UninstallCommandParser.Parse(Entry($"MsiExec.exe /I{guid}"), files, preferSilent: true);

        Assert.NotNull(cmd);
        Assert.True(cmd.IsMsi);
        Assert.True(cmd.Silent);
        Assert.Equal($"/X{guid} /qn", cmd.Arguments);
    }

    [Fact]
    public void Parse_msi_without_silent_keeps_ui()
    {
        var guid = "{12345678-1234-1234-1234-123456789ABC}";
        var cmd = UninstallCommandParser.Parse(Entry($"msiexec /i {guid}"), new FakeFiles(), preferSilent: false);

        Assert.NotNull(cmd);
        Assert.True(cmd.IsMsi);
        Assert.False(cmd.Silent);
        Assert.Equal($"/X {guid}", cmd.Arguments);
    }

    [Fact]
    public void Parse_returns_null_when_executable_missing()
    {
        var cmd = UninstallCommandParser.Parse(Entry("\"C:\\P\\unins000.exe\" /x"), new FakeFiles(), preferSilent: false);
        Assert.Null(cmd);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"")]
    public void Parse_returns_null_for_empty_or_bad_input(string? s)
    {
        Assert.Null(UninstallCommandParser.Parse(Entry(s), new FakeFiles(), preferSilent: true));
    }
}
