namespace UpdateHelper.Presentation.Tests;

public sealed class FirstSeenStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("uh-seen-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string FilePath => Path.Combine(_dir, "first-seen.json");

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }

    [Fact]
    public void Records_first_time_and_keeps_it()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
        var store = new FirstSeenStore(FilePath, clock);

        store.Record([("Tencent.QQ", "9.9.21")]);
        clock.Now = clock.Now.AddDays(2);
        store.Record([("Tencent.QQ", "9.9.21"), ("Git.Git", "2.51.0")]);

        Assert.Equal(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero), store.Get("Tencent.QQ", "9.9.21"));
        Assert.Equal(new DateTimeOffset(2026, 10, 10, 9, 0, 0, TimeSpan.Zero), store.Get("Git.Git", "2.51.0"));
        Assert.Null(store.Get("Tencent.QQ", "9.9.22"));
    }

    [Fact]
    public void Survives_restart()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 8, 9, 0, 0, TimeSpan.Zero));
        new FirstSeenStore(FilePath, clock).Record([("Tencent.QQ", "9.9.21")]);

        Assert.Equal(clock.Now, new FirstSeenStore(FilePath).Get("Tencent.QQ", "9.9.21"));
    }

    [Fact]
    public void Package_id_is_case_insensitive()
    {
        var store = new FirstSeenStore(FilePath);
        store.Record([("Tencent.QQ", "9.9.21")]);
        Assert.NotNull(store.Get("tencent.qq", "9.9.21"));
    }

    [Fact]
    public void Corrupt_file_reads_as_empty()
    {
        File.WriteAllText(FilePath, "{ not json");
        var store = new FirstSeenStore(FilePath);

        Assert.Null(store.Get("Tencent.QQ", "9.9.21"));
        store.Record([("Tencent.QQ", "9.9.21")]);          // 不抛异常，并且覆盖成好文件
        Assert.NotNull(new FirstSeenStore(FilePath).Get("Tencent.QQ", "9.9.21"));
    }

    [Fact]
    public void Missing_directory_is_created()
    {
        var store = new FirstSeenStore(Path.Combine(_dir, "sub", "first-seen.json"));
        store.Record([("Tencent.QQ", "9.9.21")]);
        Assert.NotNull(store.Get("Tencent.QQ", "9.9.21"));
    }
}
