using System.Text.Json;

namespace LegionGoConverter.Core;

/// <summary>A driver package from the converted AMD package: its original INF file name and DriverVer version.
/// <see cref="IsDisplay"/> marks the display INF (a failed removal of it stops rollback).</summary>
public sealed record PackageRef(string InfName, string Version, bool IsDisplay = false);

/// <summary>Everything rollback needs, saved in the session folder before the first mutation and rewritten after
/// AMD Software is installed. <see cref="PreExistingPublishedNames"/> are the driver-store published names
/// (<c>oemNN.inf</c>) present before the install; <see cref="CertSubject"/> is the signing certificate's subject
/// so packages signed by it are always recognized as ours.</summary>
public sealed record SessionManifest(
    string AmdVersion,
    string CertThumbprint,
    string CertSubject,
    IReadOnlyList<PackageRef> Packages,
    string LenovoInfPath,
    IReadOnlyList<string> PreExistingPublishedNames,
    IReadOnlyList<string> AmdSoftwareProductCodes)
{
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Writes the manifest atomically: the temp file is written through to the disk (and flushed) before it replaces
    /// the old one, so a crash or power loss never leaves a torn or empty file in its place.</summary>
    public void Save(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var tmp = path + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Loads and validates a manifest. Throws <see cref="InvalidDataException"/> when it is malformed or incomplete,
    /// including when it names no Lenovo driver (an export-only manifest can never drive a rollback).</summary>
    public static SessionManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        SessionManifest? m;
        try { m = JsonSerializer.Deserialize<SessionManifest>(File.ReadAllText(path), Options); }
        catch (JsonException e) { throw new InvalidDataException($"Session manifest {path} is not valid JSON: {e.Message}", e); }
        if (m is null
            || string.IsNullOrWhiteSpace(m.AmdVersion)
            || string.IsNullOrWhiteSpace(m.CertThumbprint)
            || m.CertSubject is null
            || m.Packages is null || m.Packages.Any(p => p is null || string.IsNullOrWhiteSpace(p.InfName) || string.IsNullOrWhiteSpace(p.Version))
            || string.IsNullOrWhiteSpace(m.LenovoInfPath)
            || m.PreExistingPublishedNames is null || m.PreExistingPublishedNames.Any(n => n is null)
            || m.AmdSoftwareProductCodes is null || m.AmdSoftwareProductCodes.Any(c => c is null))
            throw new InvalidDataException($"Session manifest {path} is incomplete.");
        return m;
    }
}
