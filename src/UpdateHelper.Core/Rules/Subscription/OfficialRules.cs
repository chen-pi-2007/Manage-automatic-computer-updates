namespace UpdateHelper.Core.Rules.Subscription;

/// <summary>读某目录的 manifest、在内置与缓存之间按版本挑官方源目录。只读。</summary>
public static class OfficialRules
{
    public const string ManifestFileName = "manifest.json";

    public static BundleManifest? TryReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestFileName);
        try
        {
            if (!File.Exists(path)) return null;
            var (ok, m, _) = ManifestParser.Parse(File.ReadAllText(path));
            return ok ? m : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>内置与缓存谁的 bundleVersion 高用谁；读不到记作 -1；都读不到用内置。</summary>
    public static string SelectDirectory(string builtinDir, string cacheDir)
    {
        var builtin = TryReadManifest(builtinDir)?.BundleVersion ?? -1;
        var cache = TryReadManifest(cacheDir)?.BundleVersion ?? -1;
        return cache > builtin ? cacheDir : builtinDir;
    }
}
