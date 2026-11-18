using System.IO;
using LegionGoConverter.Core;

namespace LegionGoConverter.App;

public static class SessionLocator
{
    /// <summary>The session folder with the newest <c>manifest.json</c> that loads and validates
    /// (<see cref="SessionManifest.Load"/>), or null when there is none. A newer manifest that does not load (torn by a power
    /// loss, or one without a Lenovo driver path) is skipped, so it can never hide the last install's manifest.</summary>
    public static string? FindLatestSessionDir(string sessionsRoot)
    {
        try
        {
            if (!Directory.Exists(sessionsRoot)) return null;
            return Directory.EnumerateDirectories(sessionsRoot)
                .Select(d => (Dir: d, Manifest: Path.Combine(d, ConversionPipeline.ManifestFileName)))
                .Where(x => File.Exists(x.Manifest))
                .OrderByDescending(x => File.GetLastWriteTimeUtc(x.Manifest))
                .Where(x => Loads(x.Manifest))
                .Select(x => x.Dir)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    static bool Loads(string manifest)
    {
        try { SessionManifest.Load(manifest); return true; }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        { return false; }
    }
}
