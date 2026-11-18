using System.Security.Cryptography.X509Certificates;

namespace LegionGoConverter.Core;

public class RollbackException : Exception
{
    public RollbackException(string message, Exception? inner = null) : base(message, inner) { }
}

public static class RollbackPlanner
{
    /// <summary>The store packages that came from our converted package: original INF name (case-insensitive) and
    /// driver version (<see cref="System.Version"/> equality, so <c>26.10.0.0001</c> equals <c>26.10.0.1</c>) match a
    /// <see cref="PackageRef"/>. When the converted display package of that version is in the store and signed by our
    /// certificate (<see cref="OurDisplayPresent"/>), every match is ours, also when it was already in the store before
    /// this run (a re-run, or the spike install): that AMD release can only have come from a conversion. Otherwise a match
    /// counts only when it was not in the store before the install or is signed by our certificate. Published oemNN numbers
    /// are never trusted on their own. Display packages (class Display or <see cref="PackageRef.IsDisplay"/>) come first,
    /// then manifest order.</summary>
    public static IReadOnlyList<StoredDriver> Select(IEnumerable<StoredDriver> store, SessionManifest m)
    {
        ArgumentNullException.ThrowIfNull(store);
        var list = store.ToList();
        return Select(list, m, OurDisplayPresent(list, m));
    }

    /// <summary>As <see cref="Select(IEnumerable{StoredDriver}, SessionManifest)"/>, with the "every match is ours" decision
    /// made by the caller (rollback decides it once, on the store before anything was removed).</summary>
    internal static IReadOnlyList<StoredDriver> Select(IEnumerable<StoredDriver> store, SessionManifest m, bool allMatchesAreOurs)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(m);
        var wanted = Wanted(m);
        var pre = new HashSet<string>(m.PreExistingPublishedNames, StringComparer.OrdinalIgnoreCase);

        var hits = new List<(StoredDriver Driver, int Index)>();
        foreach (var d in store)
        {
            int idx = Match(wanted, d);
            if (idx < 0) continue;
            if (!allMatchesAreOurs && pre.Contains(d.Published) && !SignedBy(d.Signer, m.CertSubject)) continue;
            hits.Add((d, wanted[idx].Index));
        }
        return hits
            .OrderBy(h => IsDisplay(h.Driver, m) ? 0 : 1)
            .ThenBy(h => h.Index)
            .ThenBy(h => h.Driver.Published, StringComparer.OrdinalIgnoreCase)
            .Select(h => h.Driver)
            .ToList();
    }

    /// <summary>True when a display package matching the manifest by name and version is in the store and signed by the
    /// manifest's certificate subject.</summary>
    public static bool OurDisplayPresent(IEnumerable<StoredDriver> store, SessionManifest m)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(m);
        var wanted = Wanted(m);
        return store.Any(d => Match(wanted, d) >= 0 && IsDisplay(d, m) && SignedBy(d.Signer, m.CertSubject));
    }

    static List<(string InfName, Version Version, int Index)> Wanted(SessionManifest m) =>
        m.Packages.Select((p, i) =>
        {
            if (!Version.TryParse(p.Version, out var v))
                throw new InvalidDataException($"Session manifest package {p.InfName} has an unparsable version '{p.Version}'.");
            return (p.InfName, Version: DriverStoreParser.Normalize(v), Index: i);
        }).ToList();

    static int Match(List<(string InfName, Version Version, int Index)> wanted, StoredDriver d)
    {
        var dv = DriverStoreParser.Normalize(d.Version);
        return wanted.FindIndex(w => string.Equals(w.InfName, d.Original, StringComparison.OrdinalIgnoreCase) && w.Version == dv);
    }

    /// <summary>Display if pnputil reported class Display, or the matching manifest package is marked <see cref="PackageRef.IsDisplay"/>.</summary>
    internal static bool IsDisplay(StoredDriver d, SessionManifest m) =>
        string.Equals(d.ClassName, "Display", StringComparison.OrdinalIgnoreCase)
        || m.Packages.Any(p => p.IsDisplay
                               && string.Equals(p.InfName, d.Original, StringComparison.OrdinalIgnoreCase)
                               && Version.TryParse(p.Version, out var v)
                               && DriverStoreParser.Normalize(v) == DriverStoreParser.Normalize(d.Version));

    /// <summary>pnputil shows the signer's simple name (the CN), the manifest keeps the full subject; accept either.</summary>
    internal static bool SignedBy(string signer, string certSubject)
    {
        if (string.IsNullOrWhiteSpace(signer) || string.IsNullOrWhiteSpace(certSubject)) return false;
        if (string.Equals(signer.Trim(), certSubject.Trim(), StringComparison.OrdinalIgnoreCase)) return true;
        string? cn = null;
        try
        {
            foreach (var rdn in new X500DistinguishedName(certSubject).EnumerateRelativeDistinguishedNames())
                if (rdn.GetSingleElementType().Value == "2.5.4.3") { cn = rdn.GetSingleElementValue(); break; }
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or InvalidOperationException) { }
        return cn != null && string.Equals(signer.Trim(), cn.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>What a completed rollback did. <see cref="RebootRequired"/> is true when any pnputil call returned 3010.
/// <see cref="SoftwareRemoved"/> lists the AMD Software product codes that were uninstalled; <see cref="SoftwareNote"/>
/// says what happened to AMD Software (null when the session installed none). <see cref="GpuState"/> and
/// <see cref="GpuOnLenovo"/> are filled in by callers that re-check the GPU afterwards.</summary>
public sealed record RollbackResult(bool RebootRequired, IReadOnlyList<string> Removed, bool CertificateKept = false)
{
    public IReadOnlyList<string> SoftwareRemoved { get; init; } = Array.Empty<string>();
    public string? SoftwareNote { get; init; }
    public string? GpuState { get; init; }
    public bool? GpuOnLenovo { get; init; }
}

/// <summary>Removes what the session installed and puts the GPU back on Lenovo's driver, never leaving it without one:
/// 0. pre-flight (nothing changed yet): if Lenovo's display INF is not in the store, its package must exist on disk,
///    otherwise rollback refuses to start;
/// 1. if Lenovo's display INF is not in the store, stages it (store only, active driver unchanged) and verifies it is
///    listed; on any problem it aborts with nothing deleted;
/// 2. deletes the selected store packages with /uninstall (display first; Windows rebinds the GPU to the staged Lenovo
///    driver). If the display package cannot be deleted, stops there so the GPU keeps a consistent, working driver;
/// 3. re-lists the store and untrusts the certificate only when none of our packages remain and no other package signed by
///    it (e.g. an earlier conversion) is left; otherwise the result reports <see cref="RollbackResult.CertificateKept"/>.
///    The trust store removes it only when its subject is this app's (<see cref="SigningCertificate.DefaultSubject"/>);
///    a manifest naming any other subject is refused before anything changes;
/// 4. only after the driver rollback succeeded: uninstalls the AMD Software products the session installed
///    (<see cref="SessionManifest.AmdSoftwareProductCodes"/>). A failure there does not fail or undo the driver rollback;
///    it is reported in <see cref="RollbackResult.SoftwareNote"/> with the product codes.
/// Failures are reported together in one <see cref="RollbackException"/> that says what was removed so far;
/// cancellation is checked only before staging and before each delete. A second run after success changes nothing.</summary>
public sealed class RollbackService
{
    readonly IDriverInstaller _installer;
    readonly ITrustStore _trust;
    readonly ISessionLog _log;
    readonly IAmdSoftwareUninstaller? _software;

    public RollbackService(IDriverInstaller installer, ITrustStore trust, ISessionLog log, IAmdSoftwareUninstaller? software = null)
    {
        _installer = installer ?? throw new ArgumentNullException(nameof(installer));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _software = software;
    }

    public async Task<RollbackResult> RunAsync(SessionManifest m, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(m);

        // ---- pre-flight: nothing has been changed yet, exceptions propagate as they are ----
        if (!string.Equals(m.CertSubject, SigningCertificate.DefaultSubject, StringComparison.Ordinal))
        {
            var msg = $"Rollback not started, nothing was changed: the session manifest names the certificate subject '{m.CertSubject}', " +
                      $"but this app only ever signs with '{SigningCertificate.DefaultSubject}'. The manifest was not written by this app.";
            _log.Error(msg);
            throw new RollbackException(msg);
        }
        var store0 = await _installer.ListAsync(ct).ConfigureAwait(false);
        // Decided once, before anything is removed (deleting the display package would change the answer).
        bool allOurs = RollbackPlanner.OurDisplayPresent(store0, m);
        var targets = RollbackPlanner.Select(store0, m, allOurs);
        var lenovoInf = m.LenovoInfPath ?? "";
        var lenovoName = Path.GetFileName(lenovoInf);
        bool lenovoPresent = lenovoName.Length > 0 && store0.Any(d => IsLenovo(d, lenovoName));
        if (!lenovoPresent && (lenovoName.Length == 0
                               || !string.Equals(Path.GetExtension(lenovoName), ".inf", StringComparison.OrdinalIgnoreCase)
                               || !File.Exists(lenovoInf)))
        {
            var msg = $"Rollback not started, nothing was changed: Lenovo's display driver '{lenovoName}' is not in the driver store " +
                      $"and the Lenovo package is not available at '{m.LenovoInfPath}'. Removing the converted driver now would leave " +
                      "the GPU on the Microsoft Basic Display Adapter. Restore the Lenovo package to that path and run rollback again.";
            _log.Error(msg);
            throw new RollbackException(msg);
        }
        _log.Info(targets.Count == 0
            ? "Rollback: no driver packages from this session are in the driver store."
            : "Rollback: will remove " + string.Join("; ", targets.Select(Describe)));

        var removed = new List<string>();
        var failures = new List<string>();
        var failedDeletes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool reboot = false;
        bool certKept = false;
        try
        {
            // ---- stage Lenovo's driver before anything of ours is removed ----
            if (!lenovoPresent)
            {
                ct.ThrowIfCancellationRequested();
                _log.Warn($"Rollback: Lenovo's display driver {lenovoName} is not in the driver store; staging it from {m.LenovoInfPath}.");
                try { reboot |= (await _installer.StageAsync(lenovoInf, ct).ConfigureAwait(false)).RebootRequired; }
                catch (DriverStoreException e)
                {
                    _log.Error("Rollback: staging Lenovo's display driver failed", e);
                    throw new RollbackException($"Rollback stopped, nothing was removed: staging Lenovo's display driver {lenovoName} failed: {e.Message}", e);
                }
                var staged = await _installer.ListAsync(CancellationToken.None).ConfigureAwait(false);
                if (!staged.Any(d => IsLenovo(d, lenovoName)))
                {
                    var msg = $"Rollback stopped, nothing was removed: Lenovo's display driver {lenovoName} was staged but is not listed in the driver store.";
                    _log.Error(msg);
                    throw new RollbackException(msg);
                }
                _log.Info($"Rollback: Lenovo's display driver {lenovoName} is staged in the driver store.");
            }

            // ---- delete ours ----
            foreach (var t in targets)
            {
                ct.ThrowIfCancellationRequested();
                _log.Info("Rollback: removing " + Describe(t));
                try
                {
                    reboot |= (await _installer.DeleteAsync(t.Published, ct).ConfigureAwait(false)).RebootRequired;
                    removed.Add(Describe(t));
                }
                catch (DriverStoreException e)
                {
                    failures.Add($"Could not remove {Describe(t)}: {e.Message}");
                    failedDeletes.Add(t.Published);
                    _log.Error("Rollback: could not remove " + Describe(t), e);
                    if (RollbackPlanner.IsDisplay(t, m))
                    {
                        _log.Warn("Rollback: the display driver could not be removed; stopping so the GPU keeps a working driver. " +
                                  "The certificate stays trusted. Fix the cause and run rollback again.");
                        throw Fail(removed, failures);
                    }
                }
            }

            // ---- verify, then untrust ----
            var store = await _installer.ListAsync(CancellationToken.None).ConfigureAwait(false);
            var remaining = RollbackPlanner.Select(store, m, allOurs);
            // Other packages signed by our certificate (e.g. an earlier conversion of another AMD version) may be what Windows
            // binds the GPU to; untrusting the certificate would break them (Code 52), so it is kept while any remain.
            var stillSigned = store.Where(d => RollbackPlanner.SignedBy(d.Signer, m.CertSubject)).ToList();
            if (remaining.Count == 0 && stillSigned.Count > 0)
            {
                certKept = true;
                _log.Warn($"Rollback: certificate {m.CertThumbprint} stays trusted because other driver packages signed by it remain in the store: " +
                          string.Join("; ", stillSigned.Select(Describe)) + ".");
            }
            else if (remaining.Count == 0)
            {
                try
                {
                    _trust.Untrust(m.CertThumbprint, m.CertSubject);
                    _log.Info($"Rollback: certificate {m.CertThumbprint} is no longer trusted.");
                }
                catch (TrustStoreException e)
                {
                    failures.Add($"Could not remove certificate {m.CertThumbprint} from the trusted stores: {e.Message}");
                    _log.Error("Rollback: could not untrust the certificate", e);
                }
            }
            else
            {
                foreach (var r in remaining.Where(r => !failedDeletes.Contains(r.Published)))
                    failures.Add($"{Describe(r)} is still in the driver store after deletion.");
                _log.Warn("Rollback: still in the store: " + string.Join("; ", remaining.Select(Describe)) + ". The certificate stays trusted.");
            }
            if (!store.Any(d => IsLenovo(d, lenovoName)))
                failures.Add($"Lenovo's display driver {lenovoName} is no longer listed in the driver store.");
        }
        catch (Exception e) when (e is not OperationCanceledException and not RollbackException)
        {
            failures.Add("Unexpected error: " + e.Message);
            _log.Error("Rollback: unexpected error", e);
            throw Fail(removed, failures, e);
        }

        if (failures.Count > 0) throw Fail(removed, failures);
        _log.Info(reboot ? "Rollback: driver part done. Restart Windows to finish." : "Rollback: driver part done. No restart was reported as required.");
        var (swRemoved, swNote) = await UninstallSoftwareAsync(m).ConfigureAwait(false);
        return new RollbackResult(reboot, removed, certKept) { SoftwareRemoved = swRemoved, SoftwareNote = swNote };
    }

    const string StoreAppNote =
        " Lenovo's Microsoft Store app \"AMD Radeon Software\" (removed when AMD Software was installed) is not restored: reinstall it from the Microsoft Store if you want it.";

    /// <summary>After a successful driver rollback: removes the AMD Software this session installed. Never throws.</summary>
    async Task<(IReadOnlyList<string> Removed, string? Note)> UninstallSoftwareAsync(SessionManifest m)
    {
        var codes = m.AmdSoftwareProductCodes;
        if (codes.Count == 0) return (Array.Empty<string>(), null);
        var list = string.Join(", ", codes);
        if (_software is null)
        {
            _log.Warn($"Rollback: AMD Software installed by that run ({list}) was not removed (no uninstaller available).");
            return (Array.Empty<string>(), $"AMD Software installed by that run ({list}) was not removed: uninstall \"AMD Software: Adrenalin Edition\" " +
                                           "in Settings > Apps > Installed apps." + StoreAppNote);
        }
        try
        {
            _log.Info($"Rollback: uninstalling the AMD Software this session installed: {list}.");
            await _software.UninstallAsync(codes, CancellationToken.None).ConfigureAwait(false);
            _log.Info("Rollback: AMD Software uninstalled.");
            return (codes.ToList(), $"AMD Software installed by that run was uninstalled ({list})." + StoreAppNote);
        }
        catch (Exception e)
        {
            _log.Error("Rollback: uninstalling AMD Software failed (the driver rollback itself succeeded)", e);
            return (Array.Empty<string>(), $"The driver was rolled back, but uninstalling AMD Software failed: {e.Message} Product codes: {list}. " +
                                           "Uninstall \"AMD Software: Adrenalin Edition\" in Settings > Apps > Installed apps, or run the app's Rollback again." + StoreAppNote);
        }
    }

    static bool IsLenovo(StoredDriver d, string lenovoName) => string.Equals(d.Original, lenovoName, StringComparison.OrdinalIgnoreCase);

    static string Describe(StoredDriver d) => $"{d.Published} ({d.Original} {d.Version})";

    static RollbackException Fail(List<string> removed, List<string> failures, Exception? inner = null) =>
        new("Rollback did not complete.\nRemoved so far: " + (removed.Count == 0 ? "nothing" : string.Join("; ", removed)) +
            "\nProblems:\n- " + string.Join("\n- ", failures), inner);
}
