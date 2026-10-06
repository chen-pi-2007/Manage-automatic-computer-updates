using UpdateHelper.Core.Install;

namespace UpdateHelper.Core.Tests;

public class RunningProcessProbeTests
{
    [Fact]
    public void Finds_the_current_test_process()
    {
        var exe = Environment.ProcessPath!;
        var found = new RunningProcessProbe().FindRunningUnder([Path.GetDirectoryName(exe)!]);
        Assert.Contains(Path.GetFileName(exe), found, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_directories_means_nothing_found()
        => Assert.Empty(new RunningProcessProbe().FindRunningUnder([]));

    [Fact]
    public void Unrelated_directory_finds_nothing()
    {
        using var dir = new TempDir();
        Assert.Empty(new RunningProcessProbe().FindRunningUnder([dir.Path]));
    }

    [Fact]
    public void Results_are_distinct()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        var found = new RunningProcessProbe().FindRunningUnder([dir, dir]);
        Assert.Equal(found.Count, found.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
