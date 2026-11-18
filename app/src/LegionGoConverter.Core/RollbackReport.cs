namespace LegionGoConverter.Core;

/// <summary>Re-checks the GPU after an in-app rollback (never assumed) and says what it runs, with the manual fallback
/// (pnputil from the retained Lenovo copy, or System Restore) when it is not back on Lenovo's driver with problem code 0.
/// Lenovo's version is read from the retained INF's DriverVer; when it cannot be read, "back on Lenovo" means problem code 0
/// and no longer the converted AMD version.</summary>
public static class RollbackReport
{
    public static (string Text, bool OnLenovo) Describe(SessionManifest m, IDeviceProbe probe)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(probe);
        Version? lenovo = null;
        try
        {
            if (File.Exists(m.LenovoInfPath))
            {
                var hit = AmdPackageLocator.DriverVer.Match(AmdPackageLocator.ReadInf(m.LenovoInfPath));
                if (hit.Success && Version.TryParse(hit.Groups[1].Value, out var v)) lenovo = DriverStoreParser.Normalize(v);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PackageLayoutException) { }
        Version? amd = Version.TryParse(m.AmdVersion, out var av) ? DriverStoreParser.Normalize(av) : null;

        GpuDevice? dev = null;
        string? probeError = null;
        try { dev = probe.GetAmdDisplayDevices().FirstOrDefault(); }
        catch (Exception e) when (e is not OperationCanceledException) { probeError = e.Message; }

        Version? running = dev?.DriverVersion is { } s && Version.TryParse(s.Trim(), out var rv) ? DriverStoreParser.Normalize(rv) : null;
        bool onLenovo = dev is { ProblemCode: 0 } && running != null && (lenovo != null ? running == lenovo : running != amd);
        var lenovoText = lenovo?.ToString() ?? "(version unknown)";
        if (onLenovo)
            return ($"The GPU now runs Lenovo's driver {dev!.DriverVersion} ({dev.DriverInf ?? "?"}) with problem code 0.", true);
        var what = dev != null
            ? $"the GPU reports driver {dev.DriverVersion ?? "unknown"} ({dev.DriverInf ?? "?"}) with problem code {dev.ProblemCode}"
            : probeError != null ? $"the GPU could not be checked ({probeError})" : "Windows does not report the GPU";
        return ($"However, {what}, not Lenovo's driver {lenovoText} with problem code 0. Restart Windows; if it is still wrong afterwards, " +
                $"re-add Lenovo's driver from the copy kept at {m.LenovoInfPath} (from an administrator prompt: pnputil /add-driver \"{m.LenovoInfPath}\" /install) " +
                "or use System Restore.", false);
    }
}
