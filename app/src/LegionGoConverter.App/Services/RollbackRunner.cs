using System.IO;
using LegionGoConverter.Core;

namespace LegionGoConverter.App;

/// <summary>Rolls back the latest session: finds its <c>manifest.json</c>, loads it and runs Core's <see cref="RollbackService"/>.
/// Needs only the sessions folder, a driver installer (system pnputil), the trust store and (for the AMD Software the session
/// installed) the system msiexec; no bundled tools. Progress goes to that session's <c>session.log</c>.
/// <paramref name="secureDataRoot"/> (see <see cref="PipelineServices.SecureDataRoot"/>) runs before the sessions folder is
/// read: when the data folder cannot be trusted there is no previous session and rollback refuses with the reason.</summary>
public sealed class RollbackRunner(string sessionsRoot, Func<ISessionLog, IDriverInstaller> installerFactory, ITrustStore trust,
    Func<ISessionLog, IAmdSoftwareUninstaller>? softwareFactory = null, Func<string>? secureDataRoot = null,
    IDeviceProbe? deviceProbe = null) : IRollbackRunner
{
    public string? LatestSessionDir
    {
        get
        {
            if (secureDataRoot is not null)
            {
                try { secureDataRoot(); }
                catch (DataRootSecurityException) { return null; }
            }
            return SessionLocator.FindLatestSessionDir(sessionsRoot);
        }
    }

    public bool HasPreviousSession => LatestSessionDir is not null;

    public async Task<RollbackResult> RunAsync(CancellationToken ct)
    {
        if (secureDataRoot is not null)
        {
            try { secureDataRoot(); }
            catch (DataRootSecurityException e) { throw new RollbackException("Rollback not started, nothing was changed. " + e.Message, e); }
        }
        var dir = SessionLocator.FindLatestSessionDir(sessionsRoot)
                  ?? throw new RollbackException("There is no earlier conversion to roll back (no session manifest was found).");
        SessionManifest manifest;
        try { manifest = SessionManifest.Load(Path.Combine(dir, ConversionPipeline.ManifestFileName)); }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        { throw new RollbackException($"Could not read the session manifest in {dir}: {e.Message}", e); }

        using var log = new FileSessionLog(dir);
        var svc = new RollbackService(installerFactory(log), trust, log, softwareFactory?.Invoke(log));
        var result = await svc.RunAsync(manifest, ct).ConfigureAwait(false);
        if (deviceProbe is null) return result;
        // Re-check what the GPU actually runs (never assumed), with the manual fallback when it is not back on Lenovo.
        var (text, onLenovo) = RollbackReport.Describe(manifest, deviceProbe);
        log.Info("Rollback outcome: " + text);
        return result with { GpuState = text, GpuOnLenovo = onLenovo };
    }
}
