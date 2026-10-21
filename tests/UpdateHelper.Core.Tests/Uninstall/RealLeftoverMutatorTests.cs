using UpdateHelper.Core.Uninstall;

namespace UpdateHelper.Core.Tests.Uninstall;

public sealed class RealLeftoverMutatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "uh-mutator-" + Guid.NewGuid().ToString("N"));

    public RealLeftoverMutatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public void MoveToBackup_moves_directory_and_leaves_original_gone()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);
        File.WriteAllText(Path.Combine(src, "a.txt"), "hi");
        var stored = Path.Combine(_root, "backup", "files", "0");

        var ok = new RealLeftoverMutator().MoveToBackup(src, stored);

        Assert.True(ok);
        Assert.False(Directory.Exists(src));
        Assert.Equal("hi", File.ReadAllText(Path.Combine(stored, "a.txt")));
    }

    [Fact]
    public void MoveBack_restores_directory_to_original_location()
    {
        var stored = Path.Combine(_root, "backup", "0");
        Directory.CreateDirectory(stored);
        File.WriteAllText(Path.Combine(stored, "a.txt"), "hi");
        var original = Path.Combine(_root, "deep", "orig");

        var ok = new RealLeftoverMutator().MoveBack(stored, original);

        Assert.True(ok);
        Assert.False(Directory.Exists(stored));
        Assert.Equal("hi", File.ReadAllText(Path.Combine(original, "a.txt")));
    }

    [Fact]
    public void MoveToBackup_returns_false_when_source_missing()
    {
        var ok = new RealLeftoverMutator().MoveToBackup(Path.Combine(_root, "nope"), Path.Combine(_root, "b", "0"));

        Assert.False(ok);
    }
}
