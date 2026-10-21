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
    public void CopyThenDelete_moves_nested_directory_and_removes_source()
    {
        var src = Path.Combine(_root, "csrc");
        Directory.CreateDirectory(Path.Combine(src, "sub", "deep"));
        File.WriteAllText(Path.Combine(src, "a.txt"), "A");
        File.WriteAllText(Path.Combine(src, "sub", "b.txt"), "B");
        File.WriteAllText(Path.Combine(src, "sub", "deep", "c.txt"), "C");
        Directory.CreateDirectory(Path.Combine(src, "emptydir"));
        var dst = Path.Combine(_root, "cdst");

        var ok = RealLeftoverMutator.CopyThenDelete(src, dst);

        Assert.True(ok);
        Assert.False(Directory.Exists(src));
        Assert.Equal("A", File.ReadAllText(Path.Combine(dst, "a.txt")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(dst, "sub", "b.txt")));
        Assert.Equal("C", File.ReadAllText(Path.Combine(dst, "sub", "deep", "c.txt")));
        Assert.True(Directory.Exists(Path.Combine(dst, "emptydir")));
    }

    [Fact]
    public void MoveToBackup_failed_move_leaves_no_empty_parent_dirs()
    {
        var parent = Path.Combine(_root, "np");
        var ok = new RealLeftoverMutator().MoveToBackup(Path.Combine(_root, "none"), Path.Combine(parent, "x", "0"));

        Assert.False(ok);
        Assert.False(Directory.Exists(parent));
    }

    [Fact]
    public void MoveToBackup_returns_false_when_source_missing()
    {
        var ok = new RealLeftoverMutator().MoveToBackup(Path.Combine(_root, "nope"), Path.Combine(_root, "b", "0"));

        Assert.False(ok);
    }
}
