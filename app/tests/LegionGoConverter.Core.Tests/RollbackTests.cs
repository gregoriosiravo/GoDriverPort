using System.Security.Cryptography.X509Certificates;

namespace LegionGoConverter.Core.Tests;

public class RollbackTests : IDisposable
{
    const string Thumb = "777C7D479879BCB167901701A72CABF29DD9BEB9";
    const string OurSigner = "Legion Go Driver Converter (local self-signed)";
    const string Whcp = "Microsoft Windows Hardware Compatibility Publisher";

    readonly string _tmp = Path.Combine(Path.GetTempPath(), "lgc-rb-" + Guid.NewGuid().ToString("N"));
    readonly string _lenovoInf;

    public RollbackTests()
    {
        Directory.CreateDirectory(Path.Combine(_tmp, "lenovo", "Display"));
        _lenovoInf = Path.Combine(_tmp, "lenovo", "Display", "u0202094.inf");
        File.WriteAllText(_lenovoInf, "; Lenovo display INF stand-in");
    }

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    static StoredDriver D(string pub, string orig, string ver, string signer, string? cls = null) =>
        new(pub, orig, Version.Parse(ver), signer) { ClassName = cls };

    SessionManifest Manifest(string[]? preExisting = null, string? lenovoInf = null) => new(
        "26.10.2", Thumb, SigningCertificate.DefaultSubject,
        new[]
        {
            new PackageRef("amdxe.inf", "26.10.0.0001"),
            new PackageRef("u0205499.inf", "32.0.32015.12012"),
            new PackageRef("amdwin-u0205499.inf", "32.0.32015.12012"),
        },
        lenovoInf ?? _lenovoInf,
        preExisting ?? Array.Empty<string>(),
        Array.Empty<string>());

    // ---------------- planner ----------------

    [Fact]
    public void Select_MatchesByNameAndVersion_NotByOemNumber()
    {
        var store = new[]
        {
            D("oem3.inf", "acpivpc.inf", "15.11.30.14", Whcp, "System"),
            D("oem77.inf", "u0205499.inf", "32.0.32015.12012", OurSigner, "Display"), // renumbered (was oem10)
            D("oem12.inf", "AMDXE.INF", "26.10.0.1", Whcp, "System"),                  // 26.10.0.1 == 26.10.0.0001
            D("oem55.inf", "u0202094.inf", "32.0.23044.19", Whcp, "Display"),         // Lenovo display
            D("oem54.inf", "amduw23e.inf", "32.0.23044.19", Whcp, "Extension"),       // Lenovo extension
            D("oem60.inf", "amdxe.inf", "26.5.0.0", Whcp, "System"),                  // Lenovo's amdxe, other version
            D("oem61.inf", "u0205499.inf", "31.0.21001.1000", Whcp, "Display"),       // same name, other version
        };
        var sel = RollbackPlanner.Select(store, Manifest());
        Assert.Equal(new[] { "oem77.inf", "oem12.inf" }, sel.Select(d => d.Published));
    }

    [Fact]
    public void Select_DisplayInfFirst()
    {
        var store = new[]
        {
            D("oem12.inf", "amdxe.inf", "26.10.0.1", Whcp, "System"),
            D("oem64.inf", "amdwin-u0205499.inf", "32.0.32015.12012", Whcp, "SoftwareComponent"),
            D("oem10.inf", "u0205499.inf", "32.0.32015.12012", OurSigner, "Display"),
        };
        var sel = RollbackPlanner.Select(store, Manifest());
        Assert.Equal("oem10.inf", sel[0].Published);
        Assert.Equal(3, sel.Count);
    }

    [Fact]
    public void Select_OurSignedDisplayPresent_PreExistingComponentsOfThatVersionAreOursToo()
    {
        // Final review finding 2 (this test encoded the old rule, which kept oem27): when the converted display package of
        // this exact version is in the store and signed by our certificate, every package matching the manifest by name
        // and version came from a conversion of that AMD release, even if it was already there before this run.
        var store = new[]
        {
            D("oem10.inf", "u0205499.inf", "32.0.32015.12012", OurSigner, "Display"),                   // pre-existing but ours
            D("oem27.inf", "amdxe.inf", "26.10.0.1", Whcp, "System"),                                   // pre-existing, same release
            D("oem64.inf", "amdwin-u0205499.inf", "32.0.32015.12012", Whcp, "SoftwareComponent"),       // added by us
        };
        var sel = RollbackPlanner.Select(store, Manifest(preExisting: new[] { "OEM10.INF", "oem27.inf" }));
        Assert.Equal(new[] { "oem10.inf", "oem27.inf", "oem64.inf" }, sel.Select(d => d.Published));
    }

    [Fact]
    public void Select_NoOurSignedDisplayInStore_KeepsTheSnapshotExclusion()
    {
        var store = new[]
        {
            D("oem27.inf", "amdxe.inf", "26.10.0.1", Whcp, "System"),                                   // pre-existing, not ours
            D("oem64.inf", "amdwin-u0205499.inf", "32.0.32015.12012", Whcp, "SoftwareComponent"),       // added by us
            D("oem61.inf", "u0205499.inf", "32.0.32015.12012", Whcp, "Display"),                        // same name+version, NOT ours
        };
        var sel = RollbackPlanner.Select(store, Manifest(preExisting: new[] { "oem27.inf", "oem61.inf" }));
        Assert.Equal(new[] { "oem64.inf" }, sel.Select(d => d.Published));
    }

    /// <summary>Scenario 1 of finding 2: run A installed; run B (same files) recorded A's packages as pre-existing.
    /// Rollback with B's manifest must remove A's AMD-signed components too (they have no Lenovo counterpart).</summary>
    [Fact]
    public void Select_ReRunManifest_RemovesTheFirstRunsComponents()
    {
        var store = new[]
        {
            D("oem70.inf", "u0205499.inf", "32.0.32015.12012", OurSigner, "Display"),
            D("oem71.inf", "amdxe.inf", "26.10.0.1", "Advanced Micro Devices, Inc.", "System"),
            D("oem72.inf", "amdafd.inf", "26.10.0.3", "Advanced Micro Devices, Inc.", "System"),
            D("oem73.inf", "amdpcibridgeextension.inf", "1.0.0.6", "Advanced Micro Devices, Inc.", "Extension"),
            D("oem54.inf", "amduw23e.inf", "32.0.23044.19", Whcp, "Extension"),
        };
        var m = SpikeManifest(preExisting: store.Select(d => d.Published).ToArray());
        var sel = RollbackPlanner.Select(store, m).Select(d => d.Published).ToList();
        Assert.Equal("oem70.inf", sel[0]); // display first, then manifest order
        Assert.Equal(new[] { "oem70.inf", "oem71.inf", "oem72.inf", "oem73.inf" }, sel.OrderBy(s => s, StringComparer.Ordinal));
    }

    /// <summary>Scenario 2 of finding 2: the device's current state. The spike-converted 26.10.2 (signed by the same
    /// certificate subject) is installed; Lenovo's display package is not in the store; Lenovo's extension (oem54) and
    /// Lenovo-era AMD components of OTHER versions are. The first real run records all of them as pre-existing.</summary>
    [Fact]
    public void Select_SpikeState_FirstRealRun_SelectsTheWholeSpikeSet_NeverLenovoOrOtherVersions()
    {
        var store = SpikeStore();
        var m = SpikeManifest(preExisting: store.Select(d => d.Published).ToArray());
        var sel = RollbackPlanner.Select(store, m).Select(d => d.Published).ToList();
        Assert.Equal("oem10.inf", sel[0]); // display first
        Assert.Equal(new[] { "oem10.inf", "oem62.inf", "oem63.inf", "oem64.inf", "oem65.inf" }, sel.OrderBy(s => s, StringComparer.Ordinal));
        foreach (var never in new[] { "oem3.inf", "oem54.inf", "oem56.inf", "oem57.inf", "oem58.inf", "oem59.inf" })
            Assert.DoesNotContain(never, sel);
    }

    static StoredDriver[] SpikeStore() => new[]
    {
        D("oem3.inf", "acpivpc.inf", "15.11.30.14", Whcp, "System"),
        D("oem10.inf", "u0205499.inf", "32.0.32015.12012", OurSigner, "Display"),                      // spike display
        D("oem62.inf", "amdxe.inf", "26.10.0.1", Whcp, "System"),                                      // spike components (AMD 26.10.2)
        D("oem63.inf", "amdafd.inf", "26.10.0.3", Whcp, "System"),
        D("oem64.inf", "amdpcibridgeextension.inf", "1.0.0.6", Whcp, "Extension"),
        D("oem65.inf", "amdwin-u0205499.inf", "32.0.32015.12012", Whcp, "SoftwareComponent"),
        D("oem54.inf", "amduw23e.inf", "32.0.23044.19", Whcp, "Extension"),                           // Lenovo extension
        D("oem56.inf", "amdxe.inf", "26.5.0.0", Whcp, "System"),                                       // Lenovo-era, other versions
        D("oem57.inf", "amdafd.inf", "25.30.0.2", Whcp, "System"),
        D("oem58.inf", "amdpcibridgeextension.inf", "1.0.0.4", Whcp, "Extension"),
        D("oem59.inf", "amdwin-u0202094.inf", "32.0.23044.19", Whcp, "SoftwareComponent"),
    };

    SessionManifest SpikeManifest(string[] preExisting) => new(
        "32.0.32015.12012", Thumb, SigningCertificate.DefaultSubject,
        new[]
        {
            new PackageRef("amdafd.inf", "26.10.0.0003"),
            new PackageRef("amdpcibridgeextension.inf", "1.0.0.6"),
            new PackageRef("amdwin-u0205499.inf", "32.0.32015.12012"),
            new PackageRef("amdxe.inf", "26.10.0.0001"),
            new PackageRef("u0205499.inf", "32.0.32015.12012", IsDisplay: true),
        },
        _lenovoInf, preExisting, Array.Empty<string>());

    [Fact]
    public async Task Rollback_SpikeState_RemovesWholeSpikeSet_StagesLenovo_AndUntrusts()
    {
        var inst = new FakeInstaller();
        inst.Store.AddRange(SpikeStore());
        inst.OnStage = _ => new[] { D("oem99.inf", "u0202094.inf", "32.0.23044.19", Whcp, "Display") };
        var trust = new FakeTrust();
        var res = await new RollbackService(inst, trust, new RecordingLog())
            .RunAsync(SpikeManifest(inst.Store.Select(d => d.Published).ToArray()), CancellationToken.None);
        Assert.Equal(Stage, inst.Mutations.First());
        Assert.Equal("delete oem10.inf", inst.Mutations.Skip(1).First());
        Assert.Equal(new[] { "oem10.inf", "oem62.inf", "oem63.inf", "oem64.inf", "oem65.inf" },
            inst.Mutations.Skip(1).Select(o => o["delete ".Length..]).OrderBy(s => s, StringComparer.Ordinal));
        foreach (var kept in new[] { "oem3.inf", "oem54.inf", "oem56.inf", "oem57.inf", "oem58.inf", "oem59.inf", "oem99.inf" })
            Assert.Contains(inst.Store, d => d.Published == kept);
        Assert.False(trust.IsTrusted(Thumb));
        Assert.Equal(5, res.Removed.Count);
    }

    [Fact]
    public void Select_UnparsableManifestVersion_Throws()
    {
        var m = Manifest() with { Packages = new[] { new PackageRef("u0205499.inf", "not-a-version") } };
        Assert.Throws<InvalidDataException>(() => RollbackPlanner.Select(Array.Empty<StoredDriver>(), m));
    }

    // ---------------- service ----------------

    sealed class FakeInstaller : IDriverInstaller
    {
        public List<StoredDriver> Store { get; } = new();
        public List<string> Ops { get; } = new();
        public HashSet<string> FailDelete { get; } = new(StringComparer.OrdinalIgnoreCase);
        /// <summary>Delete "succeeds" but the package stays in the store.</summary>
        public HashSet<string> SilentlyKeep { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> RebootOnDelete { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool FailStage { get; set; }
        public Func<string, IEnumerable<StoredDriver>> OnStage { get; set; } = _ => Array.Empty<StoredDriver>();
        public Action? AfterStage { get; set; }
        /// <summary>When set, the list call with this 1-based number throws.</summary>
        public int? ThrowOnListCall { get; set; }
        int _lists;

        public Task<DriverOpResult> AddAndInstallAsync(string infGlobRoot, CancellationToken ct)
        {
            Ops.Add("add " + infGlobRoot);
            return Task.FromResult(new DriverOpResult(false, ""));
        }

        public Task<DriverOpResult> StageAsync(string infPath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Ops.Add("stage " + infPath);
            if (FailStage) throw new DriverStoreException("pnputil add-driver (stage) failed (exit 2): The system cannot find the file specified.");
            Store.AddRange(OnStage(infPath));
            AfterStage?.Invoke();
            return Task.FromResult(new DriverOpResult(false, "Driver package added successfully."));
        }

        public Task<DriverOpResult> DeleteAsync(string publishedName, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Ops.Add("delete " + publishedName);
            if (FailDelete.Contains(publishedName)) throw new DriverStoreException("pnputil /delete-driver failed (exit 5): Access is denied.");
            if (!SilentlyKeep.Contains(publishedName)) Store.RemoveAll(d => d.Published == publishedName);
            return Task.FromResult(new DriverOpResult(RebootOnDelete.Contains(publishedName), "Driver package deleted successfully."));
        }

        public Task<IReadOnlyList<StoredDriver>> ListAsync(CancellationToken ct)
        {
            if (++_lists == ThrowOnListCall) throw new DriverStoreException("pnputil enum-drivers failed (exit 1): boom");
            return Task.FromResult<IReadOnlyList<StoredDriver>>(Store.ToList());
        }

        public IEnumerable<string> Mutations => Ops;
    }

    sealed class FakeTrust : ITrustStore
    {
        public HashSet<string> Trusted { get; } = new(StringComparer.OrdinalIgnoreCase) { Thumb };
        public List<(string Thumbprint, string Subject)> Untrusted { get; } = new();
        public bool RefuseSubject;
        public bool IsTrusted(string t) => Trusted.Contains(t);
        public void Trust(X509Certificate2 c) => Trusted.Add(c.Thumbprint);
        public void Untrust(string t, string expectedSubject)
        {
            if (RefuseSubject) throw new TrustStoreException($"Refusing to untrust {t}: its subject is not '{expectedSubject}'.");
            Untrusted.Add((t, expectedSubject));
            Trusted.Remove(t);
        }
    }

    static FakeInstaller InstalledState(bool withLenovo)
    {
        var f = new FakeInstaller();
        f.Store.Add(D("oem3.inf", "acpivpc.inf", "15.11.30.14", Whcp, "System"));
        f.Store.Add(D("oem10.inf", "u0205499.inf", "32.0.32015.12012", OurSigner, "Display"));
        f.Store.Add(D("oem27.inf", "amdxe.inf", "26.10.0.1", Whcp, "System"));
        f.Store.Add(D("oem64.inf", "amdwin-u0205499.inf", "32.0.32015.12012", Whcp, "SoftwareComponent"));
        f.Store.Add(D("oem54.inf", "amduw23e.inf", "32.0.23044.19", Whcp, "Extension"));
        if (withLenovo) f.Store.Add(D("oem55.inf", "u0202094.inf", "32.0.23044.19", Whcp, "Display"));
        f.OnStage = _ => new[] { D("oem99.inf", "u0202094.inf", "32.0.23044.19", Whcp, "Display") };
        return f;
    }

    static readonly string[] OurDeletes = { "delete oem10.inf", "delete oem27.inf", "delete oem64.inf" };
    string Stage => "stage " + _lenovoInf;

    [Fact]
    public async Task Rollback_LenovoDriverPresent_DeletesOursDisplayFirstAndUntrusts()
    {
        var inst = InstalledState(withLenovo: true);
        var trust = new FakeTrust();
        var res = await new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None);
        Assert.Equal(OurDeletes, inst.Mutations); // no staging needed
        Assert.False(trust.IsTrusted(Thumb));
        Assert.Contains(inst.Store, d => d.Published == "oem54.inf"); // Lenovo's extension untouched
        Assert.Contains(inst.Store, d => d.Published == "oem55.inf");
        Assert.Equal(3, res.Removed.Count);
        Assert.False(res.RebootRequired);
    }

    [Fact]
    public async Task Rollback_OlderConvertedPackageSignedByUsRemains_KeepsCertificateTrusted()
    {
        // an earlier conversion (other AMD version, same certificate) is still in the store: it must stay usable
        var inst = InstalledState(withLenovo: true);
        inst.Store.Add(D("oem40.inf", "u0205499.inf", "32.0.30000.1000", OurSigner, "Display"));
        var trust = new FakeTrust();
        var log = new RecordingLog();
        var res = await new RollbackService(inst, trust, log).RunAsync(Manifest(preExisting: new[] { "oem40.inf" }), CancellationToken.None);
        Assert.Equal(OurDeletes, inst.Mutations);
        Assert.Contains(inst.Store, d => d.Published == "oem40.inf");
        Assert.True(trust.IsTrusted(Thumb));
        Assert.True(res.CertificateKept);
        Assert.Contains("oem40.inf", log.All);
    }

    [Fact]
    public async Task Rollback_NothingSignedByUsRemains_CertificateNotKept()
    {
        var trust = new FakeTrust();
        var res = await new RollbackService(InstalledState(withLenovo: true), trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None);
        Assert.False(res.CertificateKept);
        Assert.False(trust.IsTrusted(Thumb));
    }

    [Fact]
    public async Task Rollback_LenovoDriverMissing_ReAddsFromLenovoPackage()
    {
        // Lenovo's driver is staged (store only) and verified BEFORE anything of ours is deleted.
        var inst = InstalledState(withLenovo: false);
        var trust = new FakeTrust();
        await new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None);
        Assert.Equal(new[] { Stage }.Concat(OurDeletes), inst.Mutations);
        Assert.Contains(inst.Store, d => d.Original == "u0202094.inf");
        Assert.False(trust.IsTrusted(Thumb));
    }

    [Fact]
    public async Task Rollback_LenovoPackageUnavailable_ChangesNothingAndThrows()
    {
        var inst = InstalledState(withLenovo: false);
        var trust = new FakeTrust();
        var missing = Path.Combine(_tmp, "gone", "u0202094.inf");
        var ex = await Assert.ThrowsAsync<RollbackException>(() =>
            new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(lenovoInf: missing), CancellationToken.None));
        Assert.Empty(inst.Mutations);
        Assert.True(trust.IsTrusted(Thumb));
        Assert.Contains("u0202094.inf", ex.Message);
        Assert.Contains("nothing was changed", ex.Message);
    }

    [Fact]
    public async Task Rollback_LenovoPathNotAnInf_ChangesNothingAndThrows()
    {
        var inst = InstalledState(withLenovo: false);
        var trust = new FakeTrust();
        var dir = Path.GetDirectoryName(_lenovoInf)!; // a folder, not an INF file
        await Assert.ThrowsAsync<RollbackException>(() =>
            new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(lenovoInf: dir), CancellationToken.None));
        Assert.Empty(inst.Mutations);
        Assert.True(trust.IsTrusted(Thumb));
    }

    [Fact]
    public async Task Rollback_StagingFails_AbortsWithNothingDeleted()
    {
        var inst = InstalledState(withLenovo: false);
        inst.FailStage = true;
        var trust = new FakeTrust();
        var ex = await Assert.ThrowsAsync<RollbackException>(() =>
            new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None));
        Assert.Equal(new[] { Stage }, inst.Mutations);
        Assert.True(trust.IsTrusted(Thumb));
        Assert.Contains("nothing was removed", ex.Message);
    }

    [Fact]
    public async Task Rollback_StagedButNotListed_AbortsWithNothingDeleted()
    {
        var inst = InstalledState(withLenovo: false);
        inst.OnStage = _ => Array.Empty<StoredDriver>();
        var trust = new FakeTrust();
        await Assert.ThrowsAsync<RollbackException>(() =>
            new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None));
        Assert.Equal(new[] { Stage }, inst.Mutations);
        Assert.True(trust.IsTrusted(Thumb));
    }

    [Fact]
    public async Task Rollback_CancelledAfterStaging_DeletesNothingKeepsCert()
    {
        var inst = InstalledState(withLenovo: false);
        using var cts = new CancellationTokenSource();
        inst.AfterStage = cts.Cancel;
        var trust = new FakeTrust();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(), cts.Token));
        Assert.Equal(new[] { Stage }, inst.Mutations);
        Assert.Contains(inst.Store, d => d.Original == "u0202094.inf"); // staged Lenovo driver is harmless
        Assert.True(trust.IsTrusted(Thumb));

        // a later run finishes without staging again
        await new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None);
        Assert.Equal(new[] { Stage }.Concat(OurDeletes), inst.Mutations);
        Assert.False(trust.IsTrusted(Thumb));
    }

    [Fact]
    public async Task Rollback_RunTwice_SecondRunIsNoOp()
    {
        var inst = InstalledState(withLenovo: false);
        var trust = new FakeTrust();
        var svc = new RollbackService(inst, trust, new RecordingLog());
        await svc.RunAsync(Manifest(), CancellationToken.None);
        var after = inst.Mutations.ToList();
        var storeAfter = inst.Store.Select(d => d.Published).ToList();

        var second = await svc.RunAsync(Manifest(), CancellationToken.None);
        Assert.Equal(after, inst.Mutations);
        Assert.Equal(storeAfter, inst.Store.Select(d => d.Published));
        Assert.False(trust.IsTrusted(Thumb));
        Assert.Empty(second.Removed);
    }

    [Fact]
    public async Task Rollback_DisplayDeleteFails_StopsKeepsCertAndThrows()
    {
        var inst = InstalledState(withLenovo: true);
        inst.FailDelete.Add("oem10.inf");
        var trust = new FakeTrust();
        var ex = await Assert.ThrowsAsync<RollbackException>(() =>
            new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None));
        Assert.Equal(new[] { "delete oem10.inf" }, inst.Mutations); // nothing else removed
        Assert.True(trust.IsTrusted(Thumb));
        Assert.Contains("oem10.inf", ex.Message);
        Assert.Contains("Access is denied", ex.Message);
    }

    [Fact]
    public async Task Rollback_DisplayWithUnparsedClassButMarkedInManifest_StopsOnDeleteFailure()
    {
        var inst = InstalledState(withLenovo: true);
        inst.Store.RemoveAll(d => d.Published == "oem10.inf");
        inst.Store.Add(D("oem10.inf", "u0205499.inf", "32.0.32015.12012", OurSigner, cls: null));
        inst.FailDelete.Add("oem10.inf");
        var m = Manifest() with
        {
            Packages = new[]
            {
                new PackageRef("amdxe.inf", "26.10.0.0001"),
                new PackageRef("u0205499.inf", "32.0.32015.12012", IsDisplay: true),
                new PackageRef("amdwin-u0205499.inf", "32.0.32015.12012"),
            }
        };
        var trust = new FakeTrust();
        await Assert.ThrowsAsync<RollbackException>(() => new RollbackService(inst, trust, new RecordingLog()).RunAsync(m, CancellationToken.None));
        Assert.Equal(new[] { "delete oem10.inf" }, inst.Mutations);
        Assert.True(trust.IsTrusted(Thumb));
    }

    [Fact]
    public async Task Rollback_ComponentDeleteFails_ContinuesKeepsCertAndThrows()
    {
        var inst = InstalledState(withLenovo: false);
        inst.FailDelete.Add("oem27.inf");
        var trust = new FakeTrust();
        var ex = await Assert.ThrowsAsync<RollbackException>(() =>
            new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None));
        Assert.Equal(new[] { Stage }.Concat(OurDeletes), inst.Mutations);
        Assert.True(trust.IsTrusted(Thumb)); // one of our packages is still in the store
        Assert.Contains("oem27.inf", ex.Message);
        Assert.Contains("oem10.inf", ex.Message); // reported as removed so far

        // after the cause is fixed, running again finishes the job
        inst.FailDelete.Clear();
        await new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None);
        Assert.Equal("delete oem27.inf", inst.Mutations.Last());
        Assert.False(trust.IsTrusted(Thumb));
    }

    [Fact]
    public async Task Rollback_DeleteReportsSuccessButPackageRemains_KeepsCertAndThrows()
    {
        var inst = InstalledState(withLenovo: true);
        inst.SilentlyKeep.Add("oem64.inf");
        var trust = new FakeTrust();
        var ex = await Assert.ThrowsAsync<RollbackException>(() =>
            new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None));
        Assert.True(trust.IsTrusted(Thumb));
        Assert.Contains("oem64.inf", ex.Message);
    }

    [Fact]
    public async Task Rollback_RelistThrowsAfterDeletes_ReportsWhatWasRemoved()
    {
        var inst = InstalledState(withLenovo: true);
        inst.ThrowOnListCall = 2; // the verification listing after the deletes
        var trust = new FakeTrust();
        var ex = await Assert.ThrowsAsync<RollbackException>(() =>
            new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None));
        Assert.Equal(OurDeletes, inst.Mutations);
        Assert.Contains("oem10.inf", ex.Message);
        Assert.Contains("oem64.inf", ex.Message);
        Assert.Contains("boom", ex.Message);
        Assert.IsType<DriverStoreException>(ex.InnerException);
        Assert.True(trust.IsTrusted(Thumb)); // not verified, so not untrusted
    }

    [Fact]
    public async Task Rollback_RebootRequiredByAnyDelete_IsReported()
    {
        var inst = InstalledState(withLenovo: true);
        inst.RebootOnDelete.Add("oem10.inf");
        var log = new RecordingLog();
        var res = await new RollbackService(inst, new FakeTrust(), log).RunAsync(Manifest(), CancellationToken.None);
        Assert.True(res.RebootRequired);
        Assert.Contains("Restart Windows", log.All);
    }

    [Fact]
    public async Task Rollback_NoOurSignedDisplay_NeverDeletesPreExistingForeignPackage()
    {
        // (final review finding 2: with our signed display package present, pre-existing packages of the same release are
        // ours; without it, the pre-install snapshot still protects them)
        var inst = InstalledState(withLenovo: true);
        inst.Store.RemoveAll(d => d.Published == "oem10.inf");
        var trust = new FakeTrust();
        await new RollbackService(inst, trust, new RecordingLog()).RunAsync(Manifest(preExisting: new[] { "oem27.inf" }), CancellationToken.None);
        Assert.Equal(new[] { "delete oem64.inf" }, inst.Mutations);
        Assert.Contains(inst.Store, d => d.Published == "oem27.inf");
        Assert.False(trust.IsTrusted(Thumb));
    }

    // ---------------- certificate subject (final review finding 3) ----------------

    [Fact]
    public async Task Rollback_ManifestSubjectIsNotOurs_RefusesBeforeAnyChange()
    {
        var inst = InstalledState(withLenovo: true);
        var trust = new FakeTrust();
        var m = Manifest() with { CertSubject = "CN=Microsoft Root Certificate Authority 2011" };
        var ex = await Assert.ThrowsAsync<RollbackException>(() => new RollbackService(inst, trust, new RecordingLog()).RunAsync(m, CancellationToken.None));
        Assert.Empty(inst.Mutations);
        Assert.Empty(trust.Untrusted);
        Assert.Contains("nothing was changed", ex.Message);
        Assert.Contains("Microsoft Root Certificate Authority 2011", ex.Message);
    }

    [Fact]
    public async Task Rollback_Untrust_PassesTheManifestSubjectForTheStoreToCheck()
    {
        var trust = new FakeTrust();
        await new RollbackService(InstalledState(withLenovo: true), trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None);
        Assert.Equal((Thumb, SigningCertificate.DefaultSubject), Assert.Single(trust.Untrusted));
    }

    [Fact]
    public async Task Rollback_TrustStoreRefusesForeignSubject_ReportedAsFailure()
    {
        var trust = new FakeTrust { RefuseSubject = true };
        var ex = await Assert.ThrowsAsync<RollbackException>(() =>
            new RollbackService(InstalledState(withLenovo: true), trust, new RecordingLog()).RunAsync(Manifest(), CancellationToken.None));
        Assert.Contains("subject", ex.Message);
        Assert.True(trust.IsTrusted(Thumb));
    }

    // ---------------- AMD Software (final review finding 5) ----------------

    sealed class FakeSoftware : IAmdSoftwareUninstaller
    {
        public List<string> Ops { get; }
        public Exception? Throws;
        public FakeSoftware(List<string> ops) { Ops = ops; }
        public Task UninstallAsync(IReadOnlyList<string> productCodes, CancellationToken ct)
        {
            Ops.Add("uninstall " + string.Join(",", productCodes) + (ct.CanBeCanceled ? " (cancellable)" : ""));
            if (Throws != null) throw Throws;
            return Task.CompletedTask;
        }
    }

    const string CodeA = "{5E5E4309-86A8-4B2A-84E1-A686D2B185EA}";
    const string CodeB = "{9CE31951-23D9-40F7-B738-63B49B9B552A}";

    [Fact]
    public async Task Rollback_UninstallsAmdSoftwareOfThatRun_AfterTheDriverRollback()
    {
        var inst = InstalledState(withLenovo: true);
        var sw = new FakeSoftware(inst.Ops);
        var m = Manifest() with { AmdSoftwareProductCodes = new[] { CodeA, CodeB } };
        var res = await new RollbackService(inst, new FakeTrust(), new RecordingLog(), sw).RunAsync(m, CancellationToken.None);
        Assert.Equal(OurDeletes.Append($"uninstall {CodeA},{CodeB}"), inst.Ops);
        Assert.Equal(new[] { CodeA, CodeB }, res.SoftwareRemoved);
        Assert.Contains("AMD Software", res.SoftwareNote);
        Assert.Contains("Microsoft Store", res.SoftwareNote); // Lenovo's Store app is not restored
    }

    [Fact]
    public async Task Rollback_AmdSoftwareUninstallFails_DriverRollbackStillSucceeds_NoteHasCodes()
    {
        var inst = InstalledState(withLenovo: true);
        var sw = new FakeSoftware(inst.Ops) { Throws = new AmdSoftwareException("Uninstall of x failed with exit code 1603.") };
        var trust = new FakeTrust();
        var m = Manifest() with { AmdSoftwareProductCodes = new[] { CodeA } };
        var res = await new RollbackService(inst, trust, new RecordingLog(), sw).RunAsync(m, CancellationToken.None);
        Assert.Equal(3, res.Removed.Count);
        Assert.False(trust.IsTrusted(Thumb));
        Assert.Empty(res.SoftwareRemoved);
        Assert.Contains("1603", res.SoftwareNote);
        Assert.Contains(CodeA, res.SoftwareNote);
    }

    [Fact]
    public async Task Rollback_NoAmdSoftwareCodes_DoesNotUninstall()
    {
        var inst = InstalledState(withLenovo: true);
        var sw = new FakeSoftware(inst.Ops);
        var res = await new RollbackService(inst, new FakeTrust(), new RecordingLog(), sw).RunAsync(Manifest(), CancellationToken.None);
        Assert.DoesNotContain(inst.Ops, o => o.StartsWith("uninstall"));
        Assert.Empty(res.SoftwareRemoved);
        Assert.Null(res.SoftwareNote);
    }

    [Fact]
    public async Task Rollback_DriverRollbackFails_AmdSoftwareIsNotTouched()
    {
        var inst = InstalledState(withLenovo: true);
        inst.FailDelete.Add("oem10.inf");
        var sw = new FakeSoftware(inst.Ops);
        var m = Manifest() with { AmdSoftwareProductCodes = new[] { CodeA } };
        await Assert.ThrowsAsync<RollbackException>(() => new RollbackService(inst, new FakeTrust(), new RecordingLog(), sw).RunAsync(m, CancellationToken.None));
        Assert.DoesNotContain(inst.Ops, o => o.StartsWith("uninstall"));
    }

    // ---------------- re-probe after the in-app rollback (final review finding 12) ----------------

    sealed class Probe(Func<IReadOnlyList<GpuDevice>> f) : IDeviceProbe { public IReadOnlyList<GpuDevice> GetAmdDisplayDevices() => f(); }

    void WriteLenovoInf() => File.WriteAllText(_lenovoInf, "[Version]\r\nClass=Display\r\nDriverVer=06/29/2026, 32.0.23044.19\r\n");

    static GpuDevice Gpu(string ver, int problem, string inf = "oem99.inf") =>
        new(@"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04\4&1", new[] { @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04" }, ver, inf, problem);

    [Fact]
    public void Report_GpuOnLenovoProblem0_SaysSo()
    {
        WriteLenovoInf();
        var r = RollbackReport.Describe(Manifest(), new Probe(() => new[] { Gpu("32.0.23044.19", 0) }));
        Assert.True(r.OnLenovo);
        Assert.Contains("now runs Lenovo's driver 32.0.23044.19", r.Text);
    }

    [Fact]
    public void Report_GpuStillOnConvertedDriver_GivesTheManualPnputilFallback()
    {
        WriteLenovoInf();
        var r = RollbackReport.Describe(Manifest(), new Probe(() => new[] { Gpu("32.0.32015.12012", 43) }));
        Assert.False(r.OnLenovo);
        Assert.Contains("problem code 43", r.Text);
        Assert.Contains("Restart Windows", r.Text);
        Assert.Contains($"pnputil /add-driver \"{_lenovoInf}\" /install", r.Text);
        Assert.Contains("System Restore", r.Text);
    }

    [Fact]
    public void Report_ProbeFailsOrNoGpu_IsNotClaimedAsSuccess()
    {
        WriteLenovoInf();
        var failed = RollbackReport.Describe(Manifest(), new Probe(() => throw new DeviceProbeException("wmi down", new Exception())));
        Assert.False(failed.OnLenovo);
        Assert.Contains("wmi down", failed.Text);
        var none = RollbackReport.Describe(Manifest(), new Probe(Array.Empty<GpuDevice>));
        Assert.False(none.OnLenovo);
        Assert.Contains("pnputil /add-driver", none.Text);
    }

    // ---------------- restore point ----------------

    sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    const string Ps = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    const string QueryScript =
        "$ErrorActionPreference = 'Stop'; Get-ComputerRestorePoint | ForEach-Object { 'RP ' + [Management.ManagementDateTimeConverter]::ToDateTime($_.CreationTime).ToUniversalTime().ToString('o') }";
    const string CreateScript =
        "$ErrorActionPreference = 'Stop'; Enable-ComputerRestore -Drive 'C:\\'; Checkpoint-Computer -Description 'Before Legion Go driver install' -RestorePointType MODIFY_SETTINGS";
    static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    static string[] PsArgs(string script) => new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script };

    static SystemRestorePoint Rp(QueueRunner r, ISessionLog? log = null) => new(r, Ps, @"C:\", new FixedClock(Now), log ?? new RecordingLog());

    [Fact]
    public async Task RestorePoint_Create_LogsEachStepAndPowerShellOutput()
    {
        int queries = 0;
        var r = new QueueRunner
        {
            Respond = (_, a) => a[^1] == QueryScript
                ? (0, ++queries == 1 ? "RP 2026-11-10T09:00:00.0000000Z" : "RP 2026-11-10T09:00:00.0000000Z\r\nRP 2026-11-16T12:00:05.0000000Z")
                : (0, "WARNING: something the checkpoint printed")
        };
        var log = new RecordingLog();
        await Rp(r, log).CreateAsync("Before Legion Go driver install", CancellationToken.None);
        Assert.Contains("RP 2026-11-10T09:00:00.0000000Z", log.All);
        Assert.Contains("RP 2026-11-16T12:00:05.0000000Z", log.All);
        Assert.Contains("WARNING: something the checkpoint printed", log.All);
        Assert.Contains("Before Legion Go driver install", log.All);
        Assert.Contains("created and verified", log.All);
    }

    [Fact]
    public async Task RestorePoint_RecentExists_IsLogged()
    {
        var r = new QueueRunner { Respond = (_, _) => (0, "RP 2026-11-16T08:00:00.0000000Z") };
        var log = new RecordingLog();
        await Rp(r, log).CreateAsync("Before Legion Go driver install", CancellationToken.None);
        Assert.Contains("RP 2026-11-16T08:00:00.0000000Z", log.All);
        Assert.Contains("recent restore point exists", log.All);
    }

    [Fact]
    public async Task RestorePoint_Failure_IsLoggedWithOutput()
    {
        var r = new QueueRunner { Respond = (_, _) => (1, "Get-ComputerRestorePoint : Access denied") };
        var log = new RecordingLog();
        await Assert.ThrowsAsync<RestorePointException>(() => Rp(r, log).CreateAsync("Before Legion Go driver install", CancellationToken.None));
        Assert.Contains(log.Lines, l => l.StartsWith("E ") && l.Contains("Access denied"));
    }

    [Fact]
    public async Task RestorePoint_RecentExists_ReturnsOutcomeNotException()
    {
        var r = new QueueRunner { Respond = (_, _) => (0, "RP 2026-11-10T09:00:00.0000000Z\r\nRP 2026-11-16T08:00:00.0000000Z\r\n") };
        var outcome = await Rp(r).CreateAsync("Before Legion Go driver install", CancellationToken.None);
        Assert.Equal(RestorePointOutcome.RecentOneExists, outcome);
        var c = Assert.Single(r.Calls);
        Assert.Equal(Ps, c.Exe);
        Assert.Equal(PsArgs(QueryScript), c.Args);
    }

    [Fact]
    public async Task RestorePoint_NoneRecent_EnablesAndCheckpoints_ThenVerifies()
    {
        int queries = 0;
        var r = new QueueRunner
        {
            Respond = (_, a) => a[^1] == QueryScript
                ? (0, ++queries == 1 ? "RP 2026-11-10T09:00:00.0000000Z" : "RP 2026-11-10T09:00:00.0000000Z\r\nRP 2026-11-16T12:00:05.0000000Z")
                : (0, "")
        };
        var outcome = await Rp(r).CreateAsync("Before Legion Go driver install", CancellationToken.None);
        Assert.Equal(RestorePointOutcome.Created, outcome);
        Assert.Equal(new[] { PsArgs(QueryScript), PsArgs(CreateScript), PsArgs(QueryScript) }, r.Calls.Select(c => c.Args));
        Assert.All(r.Calls, c => Assert.Equal(Ps, c.Exe));
    }

    [Fact]
    public async Task RestorePoint_NoPointsAtAll_Creates()
    {
        int queries = 0;
        var r = new QueueRunner { Respond = (_, a) => a[^1] == QueryScript ? (0, ++queries == 1 ? "" : "RP 2026-11-16T12:00:05.0000000Z") : (0, "") };
        Assert.Equal(RestorePointOutcome.Created, await Rp(r).CreateAsync("Before Legion Go driver install", CancellationToken.None));
    }

    [Fact]
    public async Task RestorePoint_PreCheckFails_IsLogged_AndStillEnablesAndCreates()
    {
        // Final review extra item (this test used to expect a stop without a checkpoint): with System Protection off,
        // Get-ComputerRestorePoint under ErrorActionPreference=Stop may fail. That is not a reason to give up: enable
        // System Restore and create the point, then verify it.
        int queries = 0;
        var r = new QueueRunner
        {
            Respond = (_, a) => a[^1] == QueryScript
                ? (++queries == 1 ? (1, "Get-ComputerRestorePoint : The service cannot be started") : (0, "RP 2026-11-16T12:00:05.0000000Z"))
                : (0, "")
        };
        var log = new RecordingLog();
        var outcome = await Rp(r, log).CreateAsync("Before Legion Go driver install", CancellationToken.None);
        Assert.Equal(RestorePointOutcome.Created, outcome);
        Assert.Equal(new[] { PsArgs(QueryScript), PsArgs(CreateScript), PsArgs(QueryScript) }, r.Calls.Select(c => c.Args));
        Assert.Contains(log.Lines, l => l.StartsWith("W ") && l.Contains("service cannot be started"));
    }

    [Fact]
    public async Task RestorePoint_PreCheckAndVerificationFail_Throws()
    {
        int queries = 0;
        var r = new QueueRunner { Respond = (_, a) => a[^1] == QueryScript ? (++queries == 1 ? (1, "boom 1") : (1, "boom 2")) : (0, "") };
        var ex = await Assert.ThrowsAsync<RestorePointException>(() => Rp(r).CreateAsync("Before Legion Go driver install", CancellationToken.None));
        Assert.Contains("boom 2", ex.Message);
        Assert.Equal(3, r.Calls.Count);
    }

    [Fact]
    public async Task RestorePoint_CheckpointFails_Throws()
    {
        var r = new QueueRunner { Respond = (_, a) => a[^1] == QueryScript ? (0, "") : (1, "Checkpoint-Computer : The service cannot be started") };
        var ex = await Assert.ThrowsAsync<RestorePointException>(() => Rp(r).CreateAsync("Before Legion Go driver install", CancellationToken.None));
        Assert.Contains("service cannot be started", ex.Message);
    }

    [Fact]
    public async Task RestorePoint_CheckpointSilentlySkipped_Throws()
    {
        // Checkpoint-Computer only warns (exit 0) when Windows refuses to create a point; verification must catch it.
        var r = new QueueRunner { Respond = (_, a) => a[^1] == QueryScript ? (0, "RP 2026-11-10T09:00:00.0000000Z") : (0, "WARNING: A new system restore point cannot be created ...") };
        await Assert.ThrowsAsync<RestorePointException>(() => Rp(r).CreateAsync("Before Legion Go driver install", CancellationToken.None));
    }

    [Fact]
    public async Task RestorePoint_UnparsableTimestamp_Throws()
    {
        var r = new QueueRunner { Respond = (_, _) => (0, "RP 16.11.2026 08:00") };
        await Assert.ThrowsAsync<RestorePointException>(() => Rp(r).CreateAsync("Before Legion Go driver install", CancellationToken.None));
    }

    [Theory]
    [InlineData("x'; Remove-Item C:\\ -Recurse; '")]
    [InlineData("smart \u2019 quote")]
    [InlineData("line\nbreak")]
    [InlineData("")]
    public async Task RestorePoint_UnsafeDescription_RejectedWithoutRunning(string description)
    {
        var r = new QueueRunner();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Rp(r).CreateAsync(description, CancellationToken.None));
        Assert.Empty(r.Calls);
    }
}
