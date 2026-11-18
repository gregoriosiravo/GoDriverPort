using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LegionGoConverter.Core.Tests;

/// <summary>Pipeline tests. Everything that touches the system is a fake (<see cref="FakeSystem"/> records every call);
/// tools are a fake runner that "extracts" synthetic packages and writes a fake catalog. No real process, store,
/// certificate store, restore point or driver store is touched.</summary>
public class ConversionPipelineTests : IDisposable
{
    internal const string Go1 = @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04";
    internal const string Go1Other = @"PCI\VEN_1002&DEV_15C8&SUBSYS_381217AA&REV_C9";
    internal const string Instance = @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04\4&2F3A1B&0&0041";
    internal const string AmdVer = "32.0.32015.12012";
    internal const string LenovoVer = "32.0.23044.19";
    internal const string AmdRelease = "26.20.15.12-261023a1-205499C-AMD-Software-Adrenalin-Edition";
    internal const string LenovoRelease = "25.30.44-260512a-202094C-Lenovo";
    internal const string OurCn = "Legion Go Driver Converter (local self-signed)";
    internal const string Whcp = "Microsoft Windows Hardware Compatibility Publisher";
    internal const string CodeA = "{5E5E4309-86A8-4B2A-84E1-A686D2B185EA}";
    internal const string CodeB = "{9CE31951-23D9-40F7-B738-63B49B9B552A}";
    internal static readonly ToolPaths Tools = new(@"X:\tools\7z.exe", @"X:\tools\innoextract.exe", @"X:\tools\makecat.exe", @"X:\tools\signtool.exe");

    readonly string _root = Path.Combine(Path.GetTempPath(), "lgo-pipe-" + Guid.NewGuid().ToString("N"));
    readonly X509Certificate2 _cert;
    readonly FakeSystem _sys;
    readonly FakeToolRunner _runner;

    string AmdSrc => Path.Combine(_root, "src", "amd");
    string LenovoSrc => Path.Combine(_root, "src", "lenovo");
    string AmdExe => Path.Combine(_root, "in", "driver_amd.exe");
    string LenovoExe => Path.Combine(_root, "in", "driver_lenovo.exe");
    string RandomExe => Path.Combine(_root, "in", "notepad.exe");
    string WorkRoot => Path.Combine(_root, "work");
    string SessionsRoot => Path.Combine(_root, "sessions");
    string RetainRoot => Path.Combine(_root, "lenovo-original");
    string AmdDisplaySrc => Path.Combine(AmdSrc, "Packages", "Drivers", "Display", "WT6A_INF");
    string LenovoDisplaySrc => Path.Combine(LenovoSrc, "code$GetExtractPath$", LenovoVer, "Packages", "Drivers", "Display", "WT6A_INF");

    public ConversionPipelineTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "in"));
        File.WriteAllText(AmdExe, "AMD");
        File.WriteAllText(LenovoExe, "LENOVO");
        File.WriteAllText(RandomExe, "RANDOM");
        SyntheticPackages.WriteAmd(AmdDisplaySrc);
        SyntheticPackages.WriteLenovo(LenovoDisplaySrc);

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(SigningCertificate.DefaultSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var eph = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        _cert = new X509Certificate2(eph.RawData); // public only, never in any store
        _sys = new FakeSystem(SessionsRoot);
        _runner = new FakeToolRunner(Tools, AmdSrc, LenovoSrc);
    }

    public void Dispose()
    {
        _cert.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    Func<string>? _secureDataRoot;
    Func<X509Certificate2>? _certProvider;

    PipelineServices Services() => new(
        _runner, Tools, _sys, _sys, new ListingInstallerInspector(_runner, Tools), _sys,
        _ => _sys, _ => _sys, _ => _sys, _certProvider ?? (() => new X509Certificate2(_cert.RawData)))
    {
        SessionsRoot = SessionsRoot,
        LenovoOriginalRoot = RetainRoot,
        VerifyAttempts = 2,
        VerifyDelay = TimeSpan.Zero,
        SecureDataRoot = _secureDataRoot,
    };

    ConversionRequest Request(bool install = true, bool software = true, string? export = null) =>
        new(AmdExe, LenovoExe, WorkRoot, install, software, export);

    async Task<(ConversionResult Result, Recorder Progress)> Run(
        ConversionRequest? request = null, FakePrompt? prompt = null, Action<StageProgress, CancellationTokenSource>? onReport = null)
    {
        using var cts = new CancellationTokenSource();
        var rec = new Recorder();
        if (onReport != null) rec.OnReport = p => onReport(p, cts);
        var result = await new ConversionPipeline(Services()).RunAsync(request ?? Request(), rec, prompt ?? new FakePrompt(), cts.Token);
        return (result, rec);
    }

    static string Log(ConversionResult r) => File.ReadAllText(Path.Combine(r.SessionDir, "session.log"));

    static void AssertStagesInOrder(Recorder rec, params Stage[] expected)
    {
        var stages = rec.Items.Select(i => i.Stage).ToList();
        Assert.True(stages.Zip(stages.Skip(1)).All(p => p.First <= p.Second), "stages went backwards: " + string.Join(",", stages));
        Assert.Equal(expected, stages.Distinct());
    }

    // ------------------------------------------------------------------ happy path

    [Fact]
    public async Task Run_HappyPath_StagesInOrder_AndWritesManifestBeforeInstall()
    {
        var prompt = new FakePrompt();
        var (r, rec) = await Run(prompt: prompt);

        Assert.True(r.Success, r.Error);
        Assert.Null(r.Error);
        // final review finding 9: Lenovo's driver is staged BEFORE the certificate is trusted (this test encoded the old order)
        AssertStagesInOrder(rec, Stage.Preflight, Stage.ExtractLenovo, Stage.ExtractAmd, Stage.Analyze, Stage.Build, Stage.Sign,
            Stage.Confirm, Stage.RestorePoint, Stage.StageLenovo, Stage.TrustCertificate, Stage.InstallDriver, Stage.VerifyDriver,
            Stage.InstallAmdSoftware, Stage.Done);

        var retainedInf = Path.Combine(RetainRoot, LenovoVer, "u0202094.inf");
        Assert.Equal(new[] { "RestorePoint", "Stage:" + retainedInf, "Trust", "AddAndInstall", "AmdInstall" }, _sys.Mutations);
        Assert.True(_sys.ManifestExistedAtFirstMutation);

        // manifest
        var m = SessionManifest.Load(Path.Combine(r.SessionDir, "manifest.json"));
        Assert.Equal(AmdVer, m.AmdVersion);
        Assert.Equal(_cert.Thumbprint, m.CertThumbprint);
        Assert.Equal(_cert.Subject, m.CertSubject);
        Assert.Equal(retainedInf, m.LenovoInfPath);
        Assert.True(File.Exists(m.LenovoInfPath));
        Assert.Equal(new[] { "oem54.inf" }, m.PreExistingPublishedNames);
        Assert.Equal(new[] { CodeA, CodeB }, m.AmdSoftwareProductCodes);
        Assert.Contains(m.Packages, p => p.InfName == "u0205499.inf" && p.Version == AmdVer && p.IsDisplay);
        Assert.Contains(m.Packages, p => p.InfName == "amdxe.inf" && p.Version == "26.10.0.0001" && !p.IsDisplay);
        Assert.Equal(2, m.Packages.Count);

        // converted package: INF line + DAT record, catalog written by the (fake) makecat in the INF folder
        var installRoot = Assert.Single(_sys.AddAndInstallRoots);
        Assert.StartsWith(WorkRoot, installRoot);
        Assert.Contains($"\"%AMD15BF.781%\" = ati2mtag_Phoenix, {Go1}", File.ReadAllText(Path.Combine(installRoot, "u0205499.inf")));
        var dat = AmdGcf.Read(File.ReadAllBytes(Path.Combine(installRoot, "B026716", "amdgcf.dat")), AmdRelease);
        Assert.Contains(new GcfRecord(0x15BF, 0x04), dat);
        Assert.Equal("FAKE CATALOG", File.ReadAllText(Path.Combine(installRoot, "u0205499.cat")));

        // one confirmation, listing every change
        var (title, message) = Assert.Single(prompt.Calls);
        Assert.False(string.IsNullOrWhiteSpace(title));
        foreach (var s in new[] { Go1, Instance, LenovoVer, AmdVer, "restore point", "Trusted Root", "Trusted Publishers", _cert.Thumbprint,
                     "pnputil", AmdSoftwareInstaller.StoreAppName, "AMD Software", "restart", retainedInf,
                     "System Protection", "Enable-ComputerRestore", "cannot be cancelled" })
            Assert.Contains(s, message, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("Analyze", Log(r));
        Assert.Contains("Done", Log(r));
    }

    [Fact]
    public async Task Run_SessionAndWorkFolders_AreSeparate_AndWorkIsKept()
    {
        var (r, _) = await Run();
        Assert.True(r.Success, r.Error);
        Assert.StartsWith(SessionsRoot, r.SessionDir);
        Assert.True(File.Exists(Path.Combine(r.SessionDir, "session.log")));
        var run = Assert.Single(Directory.GetDirectories(WorkRoot));
        Assert.Empty(Directory.GetFiles(run, "manifest.json", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(run, "session.log", SearchOption.AllDirectories));
    }

    // ------------------------------------------------------------------ gate: nothing changes before Confirm

    [Fact]
    public async Task Run_UserDeclinesConfirm_NoMutatingCalls()
    {
        var (r, rec) = await Run(prompt: new FakePrompt { Answer = (_, _) => false });
        Assert.False(r.Success);
        Assert.Contains("declined", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Nothing on the system was changed", r.Error);
        Assert.Empty(_sys.Mutations);
        Assert.DoesNotContain(rec.Items, i => i.Stage > Stage.Confirm);
        Assert.True(File.Exists(Path.Combine(r.SessionDir, "session.log")));
    }

    [Fact]
    public async Task Run_PromptThrows_NoMutatingCalls()
    {
        var (r, _) = await Run(prompt: new FakePrompt { Answer = (_, _) => throw new InvalidOperationException("dialog crashed") });
        Assert.False(r.Success);
        Assert.Contains("dialog crashed", r.Error);
        Assert.Contains("Nothing on the system was changed", r.Error);
        Assert.Empty(_sys.Mutations);
        Assert.Contains("dialog crashed", Log(r));
    }

    [Fact]
    public async Task Run_CancelledDuringBuild_NoMutatingCalls()
    {
        var prompt = new FakePrompt();
        var (r, rec) = await Run(prompt: prompt, onReport: (p, cts) => { if (p.Stage == Stage.Build) cts.Cancel(); });
        Assert.False(r.Success);
        Assert.Contains("Cancelled", r.Error);
        Assert.Contains("Nothing on the system was changed", r.Error);
        Assert.Empty(_sys.Mutations);
        Assert.Empty(prompt.Calls);
        Assert.DoesNotContain(rec.Items, i => i.Stage > Stage.Build);
        Assert.Contains("Cancelled", Log(r));
    }

    public static IEnumerable<object[]> CancelPoints() => new[]
    {
        new object[] { Stage.Preflight },
        new object[] { Stage.ExtractLenovo },
        new object[] { Stage.ExtractAmd },
        new object[] { Stage.Analyze },
        new object[] { Stage.Build },
        new object[] { Stage.Sign },
        new object[] { Stage.Confirm },
    };

    [Theory]
    [MemberData(nameof(CancelPoints))]
    public async Task Run_CancelBeforeOrAtConfirm_StopsWithNothingChanged(Stage at)
    {
        var prompt = new FakePrompt();
        var (r, _) = await Run(prompt: prompt, onReport: (p, cts) => { if (p.Stage == at) cts.Cancel(); });
        Assert.False(r.Success);
        Assert.Contains("Cancelled", r.Error);
        Assert.Empty(_sys.Mutations);
        Assert.Contains("Nothing on the system was changed", r.Error);
        Assert.Contains("Cancelled", Log(r));
    }

    public static IEnumerable<object[]> PostConfirmStages() => new[]
    {
        new object[] { Stage.RestorePoint },
        new object[] { Stage.StageLenovo },
        new object[] { Stage.TrustCertificate },
        new object[] { Stage.InstallDriver },
        new object[] { Stage.VerifyDriver },
        new object[] { Stage.InstallAmdSoftware },
    };

    /// <summary>Final review finding 6 (the old theory expected these to stop half-way): once the user confirmed, a Cancel
    /// press (B, Esc, a double A) is ignored; the run installs, verifies and finishes normally.</summary>
    [Theory]
    [MemberData(nameof(PostConfirmStages))]
    public async Task Run_CancelAfterConfirm_IsIgnored_RunVerifiesAndFinishes(Stage at)
    {
        var prompt = new FakePrompt();
        var (r, rec) = await Run(prompt: prompt, onReport: (p, cts) => { if (p.Stage == at) cts.Cancel(); });
        Assert.True(r.Success, r.Error);
        Assert.Equal(new[] { "RestorePoint", "Stage", "Trust", "AddAndInstall", "AmdInstall" }, _sys.Mutations.Select(m => m.Split(':')[0]));
        Assert.Contains(rec.Items, i => i.Stage == Stage.VerifyDriver);
        Assert.Equal(Stage.Done, rec.Items.Last().Stage);
    }

    [Fact]
    public async Task Run_AfterConfirm_PromptsAndStepsGetNoCancellableToken()
    {
        _sys.ProblemAfterInstall = 43; // verification fails -> rollback offer
        var prompt = new FakePrompt();
        var (r, _) = await Run(prompt: prompt);
        Assert.False(r.Success);
        Assert.Equal(2, prompt.Calls.Count);
        Assert.True(prompt.Tokens[0].CanBeCanceled);  // Confirm: cancellable
        Assert.False(prompt.Tokens[1].CanBeCanceled); // rollback offer after the install: not
        Assert.Contains("Rolled back", r.Error);
    }

    [Fact]
    public async Task Run_StagingLenovoFails_CertificateIsNotTrusted_NothingInstalled()
    {
        // final review finding 9
        _sys.StageThrows = true;
        var (r, _) = await Run();
        Assert.False(r.Success);
        Assert.StartsWith("StageLenovo", r.Error);
        Assert.Equal(new[] { "RestorePoint", "Stage" }, _sys.Mutations.Select(m => m.Split(':')[0]));
        Assert.Empty(_sys.Trusted);
    }

    [Fact]
    public async Task Run_SigningKeyExportable_WarnsInLogAndConfirmation()
    {
        // final review finding 11: the reused spike certificate's key is probably exportable; warn, never delete it
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(SigningCertificate.DefaultSubject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var eph = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = eph.Export(X509ContentType.Pfx, "pw");
        _certProvider = () => new X509Certificate2(pfx, "pw", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        var prompt = new FakePrompt();
        var (r, _) = await Run(prompt: prompt);
        Assert.True(r.Success, r.Error);
        Assert.Contains("exportable", Log(r));
        Assert.Contains("exportable", prompt.Calls[0].Message);
    }

    [Fact]
    public async Task Run_SecurityStateUnreadable_ConfirmSaysUnknownNotOff()
    {
        // final review finding 12
        _sys.SecurityThrows = true;
        var prompt = new FakePrompt();
        var (r, _) = await Run(prompt: prompt);
        Assert.True(r.Success, r.Error);
        var msg = prompt.Calls[0].Message;
        Assert.Contains("Secure Boot: unknown", msg);
        Assert.Contains("Memory Integrity (HVCI): unknown", msg);
        Assert.DoesNotContain(": off", msg);
        Assert.Contains("Secure Boot: unknown", Log(r));
    }

    // ------------------------------------------------------------------ preflight

    [Fact]
    public async Task Run_SwappedInputs_FailsInPreflight()
    {
        var (r, rec) = await Run(new ConversionRequest(LenovoExe, AmdExe, WorkRoot, true, true, null));
        Assert.False(r.Success);
        Assert.Contains("swapped", r.Error, StringComparison.OrdinalIgnoreCase);
        AssertStagesInOrder(rec, Stage.Preflight);
        Assert.DoesNotContain(_runner.Calls, c => c.Args.Contains("x") || c.Args.Contains("-d"));
        Assert.Empty(_sys.Mutations);
        Assert.False(Directory.Exists(WorkRoot) && Directory.EnumerateFileSystemEntries(WorkRoot).Any());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_RandomExe_FailsInPreflight(bool asLenovo)
    {
        var req = asLenovo
            ? new ConversionRequest(AmdExe, RandomExe, WorkRoot, true, false, null)
            : new ConversionRequest(RandomExe, LenovoExe, WorkRoot, true, false, null);
        var (r, rec) = await Run(req);
        Assert.False(r.Success);
        Assert.Contains(RandomExe, r.Error);
        Assert.Contains(asLenovo ? "is not the Lenovo" : "is not the AMD", r.Error);
        AssertStagesInOrder(rec, Stage.Preflight);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Run_MissingInstaller_FailsInPreflight()
    {
        var (r, _) = await Run(new ConversionRequest(AmdExe, Path.Combine(_root, "in", "nope.exe"), WorkRoot, true, false, null));
        Assert.False(r.Success);
        Assert.Contains("not found", r.Error);
    }

    [Fact]
    public async Task Run_UnsupportedDevice_FailsInPreflight()
    {
        _sys.Devices[0] = new GpuDevice(@"PCI\VEN_1002&DEV_15BF&SUBSYS_88881043&REV_04\1",
            new[] { @"PCI\VEN_1002&DEV_15BF&SUBSYS_88881043&REV_04", @"PCI\VEN_1002&DEV_15BF&SUBSYS_88881043" }, "31.0.1", "oem3.inf", 0);
        var (r, rec) = await Run();
        Assert.False(r.Success);
        Assert.Contains("Legion Go", r.Error);
        Assert.Contains("SUBSYS_88881043", r.Error);
        AssertStagesInOrder(rec, Stage.Preflight);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Run_LegionGoSubsystemNotInLenovoPackage_FailsInAnalyze()
    {
        // passes the preflight pre-check (Lenovo 17AA subsystem) but the Lenovo package does not list it
        const string goS = @"PCI\VEN_1002&DEV_15BF&SUBSYS_380C17AA&REV_04";
        _sys.Devices[0] = new GpuDevice(goS + @"\1", new[] { goS }, LenovoVer, "oem55.inf", 0);
        var (r, rec) = await Run();
        Assert.False(r.Success);
        Assert.StartsWith("Analyze", r.Error);
        Assert.DoesNotContain(rec.Items, i => i.Stage > Stage.Analyze);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Run_LowDisk_FailsInPreflight()
    {
        _sys.FreeBytes = 5L << 30;
        var (r, rec) = await Run();
        Assert.False(r.Success);
        Assert.Contains("disk space", r.Error);
        Assert.Contains("17 GB", r.Error); // work (12) + system data (5) on the same drive (was "12 GB" before the triage fix)
        AssertStagesInOrder(rec, Stage.Preflight);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Run_WorkAndSystemDataShareADrive_FreeSpaceCoversBoth()
    {
        // triage (task 9): 16 GB free is enough for the 12 GB work folder alone, not for work + system data
        _sys.FreeBytes = 16L << 30;
        var (r, _) = await Run();
        Assert.False(r.Success);
        Assert.Contains("17 GB", r.Error);
        Assert.Empty(_sys.Mutations);

        var (export, _) = await Run(Request(install: false, software: false, export: Path.Combine(_root, "export")));
        Assert.True(export.Success, export.Error); // export only: 12 + 3 GB
    }

    [Fact]
    public async Task Run_NotAdministrator_FailsInPreflight()
    {
        _sys.Admin = false;
        var (r, _) = await Run();
        Assert.False(r.Success);
        Assert.Contains("administrator", r.Error);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Run_WorkRootNotRepresentableInAnsi_FailsInPreflight()
    {
        var (r, _) = await Run(new ConversionRequest(AmdExe, LenovoExe, Path.Combine(_root, "w\U0001F600rk"), true, false, null));
        Assert.False(r.Success);
        Assert.Contains("code page", r.Error);
        Assert.Empty(_sys.Mutations);
    }

    [Theory]
    [InlineData(@"relative\work")]
    [InlineData(@"\\server\share\work")]
    public async Task Run_WorkRootNotALocalFullPath_FailsInPreflight(string workRoot)
    {
        var (r, _) = await Run(new ConversionRequest(AmdExe, LenovoExe, workRoot, true, false, null));
        Assert.False(r.Success);
        Assert.Contains("work folder", r.Error);
    }

    [Fact]
    public async Task Run_AmdSoftwareWithoutDriver_FailsInPreflight()
    {
        var (r, _) = await Run(Request(install: false, software: true, export: Path.Combine(_root, "export")));
        Assert.False(r.Success);
        Assert.Contains("AMD Software", r.Error);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Run_ExportFolderNotEmpty_FailsInPreflight()
    {
        var export = Path.Combine(_root, "export");
        Directory.CreateDirectory(export);
        File.WriteAllText(Path.Combine(export, "keep.txt"), "x");
        var (r, _) = await Run(Request(install: false, software: false, export: export));
        Assert.False(r.Success);
        Assert.Contains("not empty", r.Error);
        Assert.Equal("x", File.ReadAllText(Path.Combine(export, "keep.txt")));
    }

    [Fact]
    public async Task Run_PreflightLogsSecurityState()
    {
        var (r, _) = await Run();
        Assert.True(r.Success, r.Error);
        Assert.Contains("Secure Boot: on", Log(r));
        Assert.Contains("Memory Integrity (HVCI): on", Log(r));
        Assert.Contains("Test signing: off", Log(r));
    }

    // ------------------------------------------------------------------ analyze

    [Fact]
    public async Task Run_IncompatibleDat_FailsInAnalyze_BeforeAnyInstall()
    {
        var dat = Path.Combine(AmdDisplaySrc, "B026716", "amdgcf.dat");
        var bytes = File.ReadAllBytes(dat);
        bytes[5] ^= 0xFF; // header no longer matches: format or release string not understood
        File.WriteAllBytes(dat, bytes);
        var prompt = new FakePrompt();

        var (r, rec) = await Run(prompt: prompt);
        Assert.False(r.Success);
        Assert.StartsWith("Analyze", r.Error);
        Assert.Contains("amdgcf.dat", r.Error);
        Assert.DoesNotContain(rec.Items, i => i.Stage > Stage.Analyze);
        Assert.Empty(prompt.Calls);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Run_NoCcc2ButAmdSoftwareRequested_FailsInAnalyze()
    {
        File.Delete(Path.Combine(AmdDisplaySrc, "B026716", "ccc2_install.exe"));
        var (r, _) = await Run();
        Assert.False(r.Success);
        Assert.StartsWith("Analyze", r.Error);
        Assert.Contains("ccc2_install.exe", r.Error);
        Assert.Empty(_sys.Mutations);
    }

    // ------------------------------------------------------------------ re-run

    [Fact]
    public async Task Run_AlreadyConverted_CertTrustedStoreHasPackage_Succeeds()
    {
        var (first, _) = await Run();
        Assert.True(first.Success, first.Error);
        _sys.Calls.Clear();
        _sys.RestorePointOutcome = RestorePointOutcome.RecentOneExists;
        _sys.AddAndInstallRoots.Clear();

        var (r, _) = await Run();
        Assert.True(r.Success, r.Error);
        // nothing is added a second time: restore point reused, cert already trusted, driver already active, software present
        Assert.Equal(new[] { "RestorePoint", "AmdInstall" }, _sys.Mutations);
        Assert.Empty(_sys.AddAndInstallRoots);
        Assert.Single(_sys.Store, d => d.Original == "u0205499.inf");
        Assert.Single(_sys.Store, d => d.Original == "u0202094.inf");
        var m = SessionManifest.Load(Path.Combine(r.SessionDir, "manifest.json"));
        Assert.Empty(m.AmdSoftwareProductCodes); // already present: not ours to remove
        Assert.Contains(_sys.Store.Single(d => d.Original == "u0205499.inf").Published, m.PreExistingPublishedNames);
        // the retained Lenovo copy is reused, not copied again
        Assert.Equal(new[] { LenovoVer }, Directory.GetDirectories(RetainRoot).Select(Path.GetFileName));
        Assert.Contains("already", Log(r));
    }

    [Fact]
    public async Task Run_RetainedLenovoCopyIncomplete_IsSetAsideAndReplaced()
    {
        var stale = Path.Combine(RetainRoot, LenovoVer);
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "u0202094.inf"), "truncated");

        var (r, _) = await Run();
        Assert.True(r.Success, r.Error);
        Assert.Equal(File.ReadAllText(Path.Combine(LenovoDisplaySrc, "u0202094.inf")), File.ReadAllText(Path.Combine(stale, "u0202094.inf")));
        Assert.True(File.Exists(Path.Combine(stale, "B026069", "amdgcf.dat")));
        var aside = Assert.Single(Directory.GetDirectories(RetainRoot), d => Path.GetFileName(d) != LenovoVer);
        Assert.Equal("truncated", File.ReadAllText(Path.Combine(aside, "u0202094.inf"))); // never deleted
    }

    [Fact]
    public async Task Run_RetainedLenovoCopySameNamesAndSizesButDifferentContent_IsSetAsideAndReplaced()
    {
        // final review finding 3: a planted copy with matching names and sizes must not be reused
        var stale = Path.Combine(RetainRoot, LenovoVer);
        CopyTree(LenovoDisplaySrc, stale);
        var inf = Path.Combine(stale, "u0202094.inf");
        var bytes = File.ReadAllBytes(inf);
        bytes[^3] ^= 0x01; // same size, different content
        File.WriteAllBytes(inf, bytes);

        var (r, _) = await Run();
        Assert.True(r.Success, r.Error);
        Assert.Equal(File.ReadAllBytes(Path.Combine(LenovoDisplaySrc, "u0202094.inf")), File.ReadAllBytes(inf));
        var aside = Assert.Single(Directory.GetDirectories(RetainRoot), d => Path.GetFileName(d) != LenovoVer);
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(aside, "u0202094.inf")));
    }

    [Fact]
    public async Task Run_RetainedLenovoCopyWithDifferentCatalog_IsNotReused()
    {
        var stale = Path.Combine(RetainRoot, LenovoVer);
        CopyTree(LenovoDisplaySrc, stale);
        File.WriteAllText(Path.Combine(stale, "u0202094.cat"), "LENOVO CATALOX"); // same length
        var (r, _) = await Run();
        Assert.True(r.Success, r.Error);
        Assert.Equal("LENOVO CATALOG", File.ReadAllText(Path.Combine(stale, "u0202094.cat")));
    }

    static void CopyTree(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
    }

    [Fact]
    public async Task Run_DataRootRefused_StopsBeforeCreatingAnything()
    {
        _secureDataRoot = () => throw new DataRootSecurityException("The folder X is owned by SOMEONE, not by Administrators or SYSTEM.");
        var prompt = new FakePrompt();
        var (r, _) = await Run(prompt: prompt);
        Assert.False(r.Success);
        Assert.Contains("owned by SOMEONE", r.Error);
        Assert.Contains("Nothing was changed", r.Error);
        Assert.False(Directory.Exists(SessionsRoot));
        Assert.False(Directory.Exists(WorkRoot));
        Assert.Empty(prompt.Calls);
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Run_SecuresDataRootFirst_AndLogsWhatItDid()
    {
        bool sessionsExistedWhenSecured = true;
        _secureDataRoot = () => { sessionsExistedWhenSecured = Directory.Exists(SessionsRoot); return "DataRoot: ACL reset (Users could write)."; };
        var (r, _) = await Run();
        Assert.True(r.Success, r.Error);
        Assert.False(sessionsExistedWhenSecured);
        Assert.Contains("ACL reset (Users could write)", Log(r));
    }

    [Fact]
    public void CreateDefault_SecuresTheDataRoot()
    {
        Assert.NotNull(PipelineServices.CreateDefault(Tools).SecureDataRoot); // not invoked here: it would touch ProgramData
    }

    [Fact]
    public async Task Run_NoRoomToRetainLenovoCopy_FailsBeforeConfirm()
    {
        _sys.FreeBytesFor = p => p.StartsWith(RetainRoot, StringComparison.OrdinalIgnoreCase) ? 1000 : 100L << 30;
        var prompt = new FakePrompt();
        var (r, _) = await Run(prompt: prompt);
        Assert.False(r.Success);
        Assert.Contains("Lenovo", r.Error);
        Assert.Empty(prompt.Calls);
        Assert.Empty(_sys.Mutations);
    }

    // ------------------------------------------------------------------ export

    [Fact]
    public async Task Run_ExportOnly_WritesFolderWithPatchedTree_Cer_AndManifest_NoInstall()
    {
        var export = Path.Combine(_root, "export");
        var prompt = new FakePrompt();
        var (r, rec) = await Run(Request(install: false, software: false, export: export), prompt);

        Assert.True(r.Success, r.Error);
        AssertStagesInOrder(rec, Stage.Preflight, Stage.ExtractLenovo, Stage.ExtractAmd, Stage.Analyze, Stage.Build, Stage.Sign,
            Stage.Export, Stage.Done);
        Assert.Empty(_sys.Mutations);
        Assert.Empty(prompt.Calls);
        Assert.False(Directory.Exists(RetainRoot));

        var tree = Path.Combine(export, "WT6A_INF");
        Assert.Contains(Go1, File.ReadAllText(Path.Combine(tree, "u0205499.inf")));
        Assert.Equal("FAKE CATALOG", File.ReadAllText(Path.Combine(tree, "u0205499.cat")));
        Assert.Contains(new GcfRecord(0x15BF, 0x04), AmdGcf.Read(File.ReadAllBytes(Path.Combine(tree, "B026716", "amdgcf.dat")), AmdRelease));
        Assert.True(File.Exists(Path.Combine(tree, "amdxe", "amdxe.inf")));
        using (var cer = new X509Certificate2(File.ReadAllBytes(Path.Combine(export, "LegionGoDriverPort.cer"))))
        {
            Assert.Equal(_cert.Thumbprint, cer.Thumbprint);
            Assert.False(cer.HasPrivateKey);
        }
        // The exported manifest is informational (no Lenovo driver path: nothing was installed), so it is not loadable as a
        // rollback manifest, and no manifest is written into the session folder (it would hide the last install's one).
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(export, "manifest.json"))))
        {
            var m = doc.RootElement;
            Assert.Equal(AmdVer, m.GetProperty("AmdVersion").GetString());
            Assert.Equal(_cert.Thumbprint, m.GetProperty("CertThumbprint").GetString());
            Assert.Contains(m.GetProperty("Packages").EnumerateArray(),
                p => p.GetProperty("InfName").GetString() == "u0205499.inf" && p.GetProperty("IsDisplay").GetBoolean());
            Assert.Equal("", m.GetProperty("LenovoInfPath").GetString());
        }
        Assert.Throws<InvalidDataException>(() => SessionManifest.Load(Path.Combine(export, "manifest.json")));
        Assert.False(File.Exists(Path.Combine(r.SessionDir, "manifest.json")));
        Assert.True(File.Exists(Path.Combine(r.SessionDir, "session.log")));
    }

    [Fact]
    public async Task Run_InstallAndExport_ExportsAfterInstall()
    {
        var export = Path.Combine(_root, "export");
        var (r, rec) = await Run(Request(export: export));
        Assert.True(r.Success, r.Error);
        Assert.Equal(Stage.Export, rec.Items.Select(i => i.Stage).Distinct().Reverse().Skip(1).First());
        var m = SessionManifest.Load(Path.Combine(export, "manifest.json"));
        Assert.Equal(new[] { CodeA, CodeB }, m.AmdSoftwareProductCodes);
    }

    // ------------------------------------------------------------------ failures after Confirm

    [Fact]
    public async Task Run_InstallFails_OffersRollback()
    {
        _sys.AddAndInstallFailsAfterAdding = true;
        var prompt = new FakePrompt();
        var (r, rec) = await Run(prompt: prompt);

        Assert.False(r.Success);
        Assert.Equal(2, prompt.Calls.Count);
        Assert.Contains("roll back", prompt.Calls[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("InstallDriver", r.Error);
        Assert.Contains("Rolled back", r.Error);
        // display package first, then the rest, then the certificate; Lenovo's staged driver stays
        Assert.Equal(new[] { "RestorePoint", "Stage", "Trust", "AddAndInstall", "Delete", "Delete", "Untrust" },
            _sys.Mutations.Select(m => m.Split(':')[0]));
        Assert.DoesNotContain(_sys.Store, d => d.Original is "u0205499.inf" or "amdxe.inf");
        Assert.Contains(_sys.Store, d => d.Original == "u0202094.inf");
        Assert.DoesNotContain(rec.Items, i => i.Stage == Stage.InstallAmdSoftware);
    }

    [Fact]
    public async Task Run_InstallFails_RollbackDeclined_KeepsStateAndPointsToManifest()
    {
        _sys.AddAndInstallFailsAfterAdding = true;
        int n = 0;
        var prompt = new FakePrompt { Answer = (_, _) => ++n == 1 }; // yes to Confirm, no to rollback
        var (r, _) = await Run(prompt: prompt);

        Assert.False(r.Success);
        Assert.Equal(2, prompt.Calls.Count);
        Assert.DoesNotContain(_sys.Mutations, m => m.StartsWith("Delete") || m.StartsWith("Untrust"));
        Assert.Contains(Path.Combine(r.SessionDir, "manifest.json"), r.Error);
    }

    [Fact]
    public async Task Run_RollbackPromptThrows_TreatedAsDeclined()
    {
        _sys.AddAndInstallFailsAfterAdding = true;
        int n = 0;
        var prompt = new FakePrompt { Answer = (_, _) => ++n == 1 ? true : throw new InvalidOperationException("ui gone") };
        var (r, _) = await Run(prompt: prompt);
        Assert.False(r.Success);
        Assert.DoesNotContain(_sys.Mutations, m => m.StartsWith("Delete") || m.StartsWith("Untrust"));
        Assert.Contains("manifest.json", r.Error);
        Assert.Contains("ui gone", Log(r));
    }

    [Fact]
    public async Task Run_VerifyFails_Problem43_OffersRollback()
    {
        _sys.ProblemAfterInstall = 43;
        var prompt = new FakePrompt();
        var (r, _) = await Run(prompt: prompt);
        Assert.False(r.Success);
        Assert.Contains("VerifyDriver", r.Error);
        Assert.Contains("43", r.Error);
        Assert.Equal(2, prompt.Calls.Count);
        Assert.Contains("Untrust", _sys.Mutations);
        Assert.Contains(_sys.Mutations, m => m.StartsWith("Delete"));
        Assert.DoesNotContain("AmdInstall", _sys.Mutations);
    }

    [Fact]
    public async Task Run_VerifyPendingRestart_SucceedsWithRebootRequired()
    {
        _sys.InstallRebootRequired = true;
        _sys.InstallActivatesImmediately = false; // the device keeps the old driver until restart
        var (r, rec) = await Run();
        Assert.True(r.Success, r.Error);
        Assert.True(r.RebootRequired);
        Assert.True(r.VerificationPending);
        Assert.Contains("could not be verified yet", r.Notice);
        Assert.Contains("Restart Windows", r.Notice);
        Assert.Contains("could not be verified yet", rec.Items.Last(i => i.Stage == Stage.Done).Message);
        Assert.Contains("could not be verified yet", Log(r));
    }

    [Fact]
    public async Task Run_StagingRebootFlagOnly_OldDriverProblem0_IsFailureWithRollbackOffer()
    {
        _sys.StageRebootRequired = true;          // only Lenovo staging asked for a restart
        _sys.InstallRebootRequired = false;       // pnputil /install reported plain success
        _sys.InstallActivatesImmediately = false; // ...but the device still runs the old driver
        var prompt = new FakePrompt();
        var (r, _) = await Run(prompt: prompt);
        Assert.False(r.Success);
        Assert.False(r.VerificationPending);
        Assert.StartsWith("VerifyDriver", r.Error);
        Assert.Equal(2, prompt.Calls.Count);
        Assert.Contains("roll back", prompt.Calls[1].Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("AmdInstall", _sys.Mutations);
    }

    [Fact]
    public async Task Run_NewVersionProblem0_IsVerifiedNotPending()
    {
        _sys.InstallRebootRequired = true; // a restart is wanted, but the new driver already runs
        var (r, _) = await Run();
        Assert.True(r.Success, r.Error);
        Assert.True(r.RebootRequired);
        Assert.False(r.VerificationPending);
        Assert.Null(r.Notice);
    }

    [Fact]
    public async Task Run_VerificationPending_AmdSoftwareFails_DoesNotClaimDriverWorks()
    {
        _sys.InstallRebootRequired = true;
        _sys.InstallActivatesImmediately = false;
        _sys.SoftwareThrows = new AmdSoftwareException("failed", Array.Empty<string>(), Array.Empty<string>());
        var (r, _) = await Run();
        Assert.False(r.Success);
        Assert.True(r.VerificationPending);
        Assert.DoesNotContain("installed and working", r.Error);
        Assert.Contains("not verified yet", r.Error);
    }

    [Fact]
    public async Task Run_RollbackRestoresLenovo_ReportsLenovoDriverRunning()
    {
        _sys.ProblemAfterInstall = 43;
        var (r, _) = await Run();
        Assert.False(r.Success);
        Assert.Contains("Rolled back", r.Error);
        Assert.Contains($"now runs Lenovo's driver {LenovoVer}", r.Error);
        Assert.Contains("problem code 0", r.Error);
    }

    [Fact]
    public async Task Run_RollbackLeavesOtherDriverOrProblem_ReportsWhatTheGpuRuns()
    {
        _sys.ProblemAfterInstall = 43;
        _sys.RebindOnDisplayDelete = false; // the GPU keeps reporting the failed driver after rollback
        var (r, _) = await Run();
        Assert.False(r.Success);
        Assert.DoesNotContain("now runs Lenovo's driver", r.Error);
        Assert.Contains($"reports driver {AmdVer}", r.Error);
        Assert.Contains("problem code 43", r.Error);
        Assert.Contains("Restart Windows", r.Error);
        Assert.Contains(Path.Combine(RetainRoot, LenovoVer, "u0202094.inf"), r.Error);
    }

    [Fact]
    public async Task Run_RestorePointFails_NoTrustNoInstall()
    {
        _sys.RestorePointThrows = true;
        var (r, _) = await Run();
        Assert.False(r.Success);
        Assert.StartsWith("RestorePoint", r.Error);
        Assert.Equal(new[] { "RestorePoint" }, _sys.Mutations);
    }

    [Fact]
    public async Task Run_AmdSoftwarePartialFailure_RecordsCodes_OffersUninstall()
    {
        _sys.SoftwareThrows = new AmdSoftwareException("AMD DVR install failed with exit code 1603.", new[] { CodeA }, Array.Empty<string>());
        var prompt = new FakePrompt();
        var (r, _) = await Run(prompt: prompt);

        Assert.False(r.Success);
        Assert.Contains("1603", r.Error);
        Assert.Contains("driver", r.Error); // says the driver itself is installed
        Assert.Equal(2, prompt.Calls.Count);
        Assert.Contains(CodeA, prompt.Calls[1].Message);
        Assert.Equal("AmdUninstall:" + CodeA, _sys.Mutations.Last());
        Assert.DoesNotContain(_sys.Mutations, m => m.StartsWith("Delete")); // the working driver is not rolled back
        var m = SessionManifest.Load(Path.Combine(r.SessionDir, "manifest.json"));
        Assert.Empty(m.AmdSoftwareProductCodes);
    }

    [Fact]
    public async Task Run_AmdSoftwarePartialFailure_UninstallDeclined_ManifestKeepsCodes()
    {
        _sys.SoftwareThrows = new AmdSoftwareException("failed", new[] { CodeA }, Array.Empty<string>());
        int n = 0;
        var (r, _) = await Run(prompt: new FakePrompt { Answer = (_, _) => ++n == 1 });
        Assert.False(r.Success);
        Assert.DoesNotContain(_sys.Mutations, m => m.StartsWith("AmdUninstall"));
        var m = SessionManifest.Load(Path.Combine(r.SessionDir, "manifest.json"));
        Assert.Equal(new[] { CodeA }, m.AmdSoftwareProductCodes);
    }

    [Fact]
    public async Task Run_AmdSoftwareUnexpectedError_ReportsDriverStillInstalled_NoRollback()
    {
        _sys.SoftwareThrowsOther = new IOException("disk full");
        var prompt = new FakePrompt();
        var (r, _) = await Run(prompt: prompt);
        Assert.False(r.Success);
        Assert.Contains("disk full", r.Error);
        Assert.Contains("display driver itself is installed", r.Error);
        Assert.Single(prompt.Calls); // only Confirm: nothing of AMD Software is known to be installed, the driver is not rolled back
        Assert.DoesNotContain(_sys.Mutations, m => m.StartsWith("Delete") || m.StartsWith("AmdUninstall"));
    }

    [Fact]
    public async Task Run_ExtractionFails_KeepsWorkFolder_AndLogs()
    {
        _runner.FailExtraction = true;
        var (r, _) = await Run();
        Assert.False(r.Success);
        Assert.StartsWith("ExtractLenovo", r.Error);
        Assert.Contains("Nothing on the system was changed", r.Error);
        Assert.Single(Directory.GetDirectories(WorkRoot));
        Assert.Contains("ExtractLenovo", Log(r));
        Assert.Empty(_sys.Mutations);
    }

    [Fact]
    public async Task Run_SecondRun_UsesFreshWorkAndSessionFolders()
    {
        _sys.SoftwareThrows = null;
        var (a, _) = await Run();
        var (b, _) = await Run();
        Assert.True(a.Success && b.Success, a.Error + b.Error);
        Assert.NotEqual(a.SessionDir, b.SessionDir);
        Assert.Equal(2, Directory.GetDirectories(WorkRoot).Length);
    }
}

/// <summary>Installer identification by content, through the process runner (fake).</summary>
public class ListingInstallerInspectorTests
{
    static readonly ToolPaths Tools = ConversionPipelineTests.Tools;

    static ListingInstallerInspector With(int exit, string text)
    {
        var runner = new QueueRunner { Respond = (_, _) => (exit, text) };
        return new ListingInstallerInspector(runner, Tools);
    }

    [Fact]
    public async Task Lenovo_InnoListingWithDisplayInf_IsLenovo()
    {
        var runner = new QueueRunner { Respond = (_, _) => (0, "code$GetExtractPath$\\Setup.bat\ncode$GetExtractPath$\\32.0.23044.19\\Packages\\Drivers\\Display\\WT6A_INF\\u0202094.inf\n") };
        Assert.True(await new ListingInstallerInspector(runner, Tools).IsAsync(@"C:\in\l.exe", InstallerKind.Lenovo, CancellationToken.None));
        var call = Assert.Single(runner.Calls);
        Assert.Equal(Tools.InnoExtract, call.Exe);
        Assert.Equal(new[] { "--list", "--silent", "--", @"C:\in\l.exe" }, call.Args);
    }

    [Fact]
    public async Task Amd_SevenZipListingWithDisplayInf_IsAmd()
    {
        var runner = new QueueRunner { Respond = (_, _) => (0, "2026-10-28 13:49:59 .....      1119364               Packages\\Drivers\\Display\\WT6A_INF\\u0205499.inf") };
        Assert.True(await new ListingInstallerInspector(runner, Tools).IsAsync(@"C:\in\a.exe", InstallerKind.Amd, CancellationToken.None));
        var call = Assert.Single(runner.Calls);
        Assert.Equal(Tools.SevenZip, call.Exe);
        Assert.Equal(new[] { "l", "-ba", "--", @"C:\in\a.exe" }, call.Args);
    }

    [Fact]
    public async Task PeSectionsOnly_IsNotAmd() =>
        Assert.False(await With(0, "  .....  62020  62464  .text\n  .....  1980  1980  .rsrc\\1033\\version.txt").IsAsync("x.exe", InstallerKind.Amd, CancellationToken.None));

    [Fact]
    public async Task ToolFails_IsNotLenovo() =>
        Assert.False(await With(2, "Not a supported Inno Setup installer!").IsAsync("x.exe", InstallerKind.Lenovo, CancellationToken.None));

    [Fact]
    public async Task DisplayInfMentionedButNotAPath_IsNotAmd() =>
        Assert.False(await With(0, "Packages\\Drivers\\Display\\readme.txt\nsomething.inf").IsAsync("x.exe", InstallerKind.Amd, CancellationToken.None));

    /// <summary>Read-only listing of the real installers (skipped when absent).</summary>
    [RealInstallersFact]
    public async Task RealInstallers_AreIdentified_AndSwapIsDetected()
    {
        var amd = RealInstallersFactAttribute.Amd;
        var lenovo = RealInstallersFactAttribute.Lenovo;
        var insp = new ListingInstallerInspector(new ProcessRunner(),
            new ToolPaths(SevenZipFactAttribute.Path, RealInstallersFactAttribute.InnoExtract!, "makecat.exe", "signtool.exe"));
        var notepad = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        Assert.True(await insp.IsAsync(lenovo, InstallerKind.Lenovo, CancellationToken.None));
        Assert.True(await insp.IsAsync(amd, InstallerKind.Amd, CancellationToken.None));
        Assert.False(await insp.IsAsync(amd, InstallerKind.Lenovo, CancellationToken.None));
        Assert.False(await insp.IsAsync(lenovo, InstallerKind.Amd, CancellationToken.None));
        Assert.False(await insp.IsAsync(notepad, InstallerKind.Amd, CancellationToken.None));
        Assert.False(await insp.IsAsync(notepad, InstallerKind.Lenovo, CancellationToken.None));
    }
}

/// <summary>A fact skipped unless both real installers, 7-Zip and innoextract (on PATH) are present.</summary>
public sealed class RealInstallersFactAttribute : FactAttribute
{
    static readonly string Dir = Environment.GetEnvironmentVariable("LGO_INSTALLERS_DIR") ?? "";
    public static string Amd => Path.Combine(Dir, "driver_amd7840U.exe");
    public static string Lenovo => Path.Combine(Dir, "driver_lenovo.exe");
    public static string? InnoExtract =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), "innoextract.exe")).FirstOrDefault(File.Exists);

    public RealInstallersFactAttribute()
    {
        if (string.IsNullOrEmpty(Dir) || !File.Exists(Amd) || !File.Exists(Lenovo)) Skip = $"Real installers not found (set LGO_INSTALLERS_DIR to a folder with driver_amd7840U.exe and driver_lenovo.exe): '{Dir}'";
        else if (InnoExtract == null) Skip = "innoextract.exe not found on PATH";
        else if (!File.Exists(SevenZipFactAttribute.Path)) Skip = $"7z.exe not found: {SevenZipFactAttribute.Path}";
    }
}

public class WindowsEnvironmentProbeTests
{
    /// <summary>Read-only queries; must not throw on this machine.</summary>
    [Fact]
    public void Probe_ReadsWithoutThrowing()
    {
        var p = new WindowsEnvironmentProbe();
        _ = p.IsAdministrator();
        Assert.True(p.FreeBytes(Path.GetTempPath()) > 0);
        _ = p.GetSecurityState();
    }
}

// ====================================================================== fakes

internal sealed class Recorder : IProgress<StageProgress>
{
    public List<StageProgress> Items { get; } = new();
    public Action<StageProgress>? OnReport { get; set; }
    public void Report(StageProgress value) { Items.Add(value); OnReport?.Invoke(value); }
}

internal sealed class FakePrompt : IUserPrompt
{
    public List<(string Title, string Message)> Calls { get; } = new();
    public List<CancellationToken> Tokens { get; } = new();
    public Func<string, string, bool> Answer { get; set; } = (_, _) => true;
    public Task<bool> ConfirmAsync(string title, string message, CancellationToken ct)
    {
        Calls.Add((title, message));
        Tokens.Add(ct);
        return Task.FromResult(Answer(title, message));
    }
}

/// <summary>Fake system: records every call; mutating calls are prefixed with "M:" in <see cref="Calls"/>.</summary>
internal sealed class FakeSystem : ITrustStore, IRestorePoint, IDriverInstaller, IAmdSoftwareInstaller, IDeviceProbe, IEnvironmentProbe
{
    readonly string _sessionsRoot;
    int _nextOem = 70;
    public FakeSystem(string sessionsRoot)
    {
        _sessionsRoot = sessionsRoot;
        Devices.Add(new GpuDevice(ConversionPipelineTests.Instance,
            new[] { ConversionPipelineTests.Go1, @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA", @"PCI\VEN_1002&DEV_15BF&CC_030000" },
            ConversionPipelineTests.LenovoVer, "oem55.inf", 0));
        // like the user's device: Lenovo's extension INF is in the store, Lenovo's display INF is not
        Store.Add(new StoredDriver("oem54.inf", "amduw23e.inf", Version.Parse(ConversionPipelineTests.LenovoVer), ConversionPipelineTests.Whcp) { ClassName = "Extension" });
    }

    public List<string> Calls { get; } = new();
    public IEnumerable<string> Mutations => Calls.Where(c => c.StartsWith("M:")).Select(c => c[2..]);
    public bool ManifestExistedAtFirstMutation { get; private set; }
    bool _sawMutation;

    void Mut(string what)
    {
        if (!_sawMutation)
        {
            _sawMutation = true;
            ManifestExistedAtFirstMutation = Directory.Exists(_sessionsRoot)
                && Directory.EnumerateFiles(_sessionsRoot, "manifest.json", SearchOption.AllDirectories).Any();
        }
        Calls.Add("M:" + what);
    }

    // environment
    public bool Admin = true;
    public long FreeBytes = 100L << 30;
    public Func<string, long>? FreeBytesFor;
    public SecurityState Security = new(true, true, false);
    public bool SecurityThrows;
    public bool IsAdministrator() { Calls.Add("R:IsAdministrator"); return Admin; }
    long IEnvironmentProbe.FreeBytes(string path) { Calls.Add("R:FreeBytes"); return FreeBytesFor?.Invoke(path) ?? FreeBytes; }
    public SecurityState GetSecurityState()
    {
        Calls.Add("R:Security");
        if (SecurityThrows) throw new System.Management.ManagementException("Access denied");
        return Security;
    }

    // device
    public List<GpuDevice> Devices { get; } = new();
    public IReadOnlyList<GpuDevice> GetAmdDisplayDevices() { Calls.Add("R:Devices"); return Devices.ToList(); }

    // trust
    public HashSet<string> Trusted { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool IsTrusted(string thumbprint) { Calls.Add("R:IsTrusted"); return Trusted.Contains(thumbprint); }
    public void Trust(X509Certificate2 cert) { Mut("Trust"); Trusted.Add(cert.Thumbprint); }
    public void Untrust(string thumbprint, string expectedSubject) { Mut("Untrust"); Trusted.Remove(thumbprint); }

    // restore point
    public RestorePointOutcome RestorePointOutcome = RestorePointOutcome.Created;
    public bool RestorePointThrows;
    public Task<RestorePointOutcome> CreateAsync(string description, CancellationToken ct)
    {
        Mut("RestorePoint");
        if (RestorePointThrows) throw new RestorePointException("System Protection is off.");
        return Task.FromResult(RestorePointOutcome);
    }

    // driver store
    public List<StoredDriver> Store { get; } = new();
    public List<string> AddAndInstallRoots { get; } = new();
    public bool AddAndInstallFailsAfterAdding;
    public bool InstallRebootRequired;
    public bool InstallActivatesImmediately = true;
    public int ProblemAfterInstall;

    public Task<DriverOpResult> AddAndInstallAsync(string infGlobRoot, CancellationToken ct)
    {
        Mut("AddAndInstall");
        AddAndInstallRoots.Add(infGlobRoot);
        foreach (var inf in Directory.EnumerateFiles(infGlobRoot, "*.inf", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(inf);
            var text = File.ReadAllText(inf);
            var ver = System.Text.RegularExpressions.Regex.Match(text, @"DriverVer\s*=\s*[^,]+,\s*([0-9.]+)").Groups[1].Value;
            bool display = text.Contains("Class=Display");
            Store.Add(new StoredDriver($"oem{_nextOem++}.inf", name, Version.Parse(ver),
                display ? ConversionPipelineTests.OurCn : "Advanced Micro Devices, Inc.") { ClassName = display ? "Display" : "System" });
        }
        if (AddAndInstallFailsAfterAdding) throw new DriverStoreException("pnputil add-driver failed (exit 1): device did not start");
        var d = Devices[0];
        if (InstallActivatesImmediately)
            Devices[0] = d with { DriverVersion = ConversionPipelineTests.AmdVer, ProblemCode = ProblemAfterInstall };
        return Task.FromResult(new DriverOpResult(InstallRebootRequired, "ok"));
    }

    public Task<DriverOpResult> StageAsync(string infPath, CancellationToken ct)
    {
        Mut("Stage:" + infPath);
        if (StageThrows) throw new DriverStoreException("pnputil add-driver (stage) failed (exit 2): The system cannot find the file specified.");
        if (!File.Exists(infPath)) throw new DriverStoreException("Driver INF not found: " + infPath);
        Store.Add(new StoredDriver($"oem{_nextOem++}.inf", Path.GetFileName(infPath), Version.Parse(ConversionPipelineTests.LenovoVer), ConversionPipelineTests.Whcp) { ClassName = "Display" });
        return Task.FromResult(new DriverOpResult(StageRebootRequired, "staged"));
    }

    public bool StageRebootRequired;
    public bool StageThrows;
    /// <summary>Deleting a converted display package rebinds the GPU to Lenovo's driver (as Windows does).</summary>
    public bool RebindOnDisplayDelete = true;

    public Task<DriverOpResult> DeleteAsync(string publishedName, CancellationToken ct)
    {
        Mut("Delete:" + publishedName);
        var gone = Store.Where(s => s.Published == publishedName).ToList();
        if (RebindOnDisplayDelete && gone.Any(g => g.ClassName == "Display" && g.Original != "u0202094.inf"))
            Devices[0] = Devices[0] with { DriverVersion = ConversionPipelineTests.LenovoVer, ProblemCode = 0, DriverInf = "oem99.inf" };
        Store.RemoveAll(s => s.Published == publishedName);
        return Task.FromResult(new DriverOpResult(false, "deleted"));
    }

    public Task<IReadOnlyList<StoredDriver>> ListAsync(CancellationToken ct)
    {
        Calls.Add("R:List");
        return Task.FromResult<IReadOnlyList<StoredDriver>>(Store.ToList());
    }

    // AMD Software
    public HashSet<string> InstalledProducts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public AmdSoftwareException? SoftwareThrows;
    public Exception? SoftwareThrowsOther;
    public Task<AmdSoftwareInstallResult> InstallDetailedAsync(string ccc2Path, string workDir, CancellationToken ct)
    {
        Mut("AmdInstall");
        if (SoftwareThrowsOther != null) throw SoftwareThrowsOther;
        if (!File.Exists(ccc2Path)) throw new AmdSoftwareException("AMD installer not found: " + ccc2Path);
        if (Directory.Exists(workDir) && Directory.EnumerateFileSystemEntries(workDir).Any()) throw new AmdSoftwareException("Work folder is not empty: " + workDir);
        if (SoftwareThrows != null)
        {
            foreach (var c in SoftwareThrows.InstalledProductCodes) InstalledProducts.Add(c);
            throw SoftwareThrows;
        }
        var codes = new[] { ConversionPipelineTests.CodeA, ConversionPipelineTests.CodeB };
        var pre = codes.Where(InstalledProducts.Contains).ToArray();
        var added = codes.Except(pre).ToArray();
        foreach (var c in added) InstalledProducts.Add(c);
        return Task.FromResult(new AmdSoftwareInstallResult(codes, added, pre, false));
    }

    public Task UninstallAsync(IReadOnlyList<string> productCodes, CancellationToken ct)
    {
        Mut("AmdUninstall:" + string.Join(",", productCodes));
        foreach (var c in productCodes) InstalledProducts.Remove(c);
        return Task.CompletedTask;
    }
}

/// <summary>Fake tools: listings for the installer inspector, "extraction" by copying synthetic packages,
/// a fake makecat that writes the catalog named in the CDF, and a no-op signtool.</summary>
internal sealed class FakeToolRunner(ToolPaths tools, string amdSrc, string lenovoSrc) : IProcessRunner
{
    public List<(string Exe, string[] Args)> Calls { get; } = new();
    public bool FailExtraction;

    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? lines, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var a = args.ToArray();
        Calls.Add((exe, a));
        if (exe == tools.InnoExtract)
        {
            var kind = File.ReadAllText(a[^1]);
            if (a[0] == "--list")
                return kind == "LENOVO"
                    ? Listing(lines, @"code$GetExtractPath$\Setup.bat", @"code$GetExtractPath$\32.0.23044.19\Packages\Drivers\Display\WT6A_INF\u0202094.inf")
                    : Task.FromResult(new ProcessResult(2, "Not a supported Inno Setup installer!"));
            var target = a[Array.IndexOf(a, "-d") + 1];
            Directory.CreateDirectory(target);
            if (FailExtraction) { File.WriteAllText(Path.Combine(target, "partial.bin"), "x"); return Task.FromResult(new ProcessResult(1, "innoextract: disk full")); }
            Copy(lenovoSrc, target);
            return Task.FromResult(new ProcessResult(0, ""));
        }
        if (exe == tools.SevenZip)
        {
            var kind = File.ReadAllText(a[^1]);
            if (a[0] == "l")
                return kind == "AMD"
                    ? Listing(lines, "2026-10-29 05:09:15 D....            0            0  Bin64",
                        @"2026-10-28 13:49:59 .....      1119364               Packages\Drivers\Display\WT6A_INF\u0205499.inf")
                    : Listing(lines, "                    .....       157666       159744  .text");
            var target = a.First(x => x.StartsWith("-o"))[2..];
            Directory.CreateDirectory(target);
            Copy(amdSrc, target);
            return Task.FromResult(new ProcessResult(0, ""));
        }
        if (exe == tools.MakeCat)
        {
            var cdf = File.ReadAllLines(a[^1], Encoding.Latin1);
            var name = cdf.First(l => l.StartsWith("Name="))[5..];
            var dir = cdf.First(l => l.StartsWith("ResultDir="))[10..];
            File.WriteAllText(Path.Combine(dir, name), "FAKE CATALOG");
            return Task.FromResult(new ProcessResult(0, "Succeeded"));
        }
        if (exe == tools.SignTool) return Task.FromResult(new ProcessResult(0, "Successfully signed"));
        throw new InvalidOperationException("Unexpected tool " + exe);
    }

    static Task<ProcessResult> Listing(IProgress<string>? lines, params string[] text)
    {
        foreach (var l in text) lines?.Report(l);
        return Task.FromResult(new ProcessResult(0, string.Join(Environment.NewLine, text)));
    }

    static void Copy(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst);
        }
    }
}

/// <summary>Small synthetic AMD and Lenovo display packages with the real layouts and key lines.</summary>
internal static class SyntheticPackages
{
    public static void WriteAmd(string displayDir)
    {
        var bin = Directory.CreateDirectory(Path.Combine(displayDir, "B026716")).FullName;
        File.WriteAllText(Path.Combine(displayDir, "u0205499.inf"),
            "[Version]\r\nSignature=\"$WINDOWS NT$\"\r\nClass=Display\r\nClassGUID={4d36e968-e325-11ce-bfc1-08002be10318}\r\n" +
            "Provider=%AMD%\r\nCatalogFile=u0205499.cat\r\nDriverVer=10/28/2026, " + ConversionPipelineTests.AmdVer + "\r\n\r\n" +
            "[ATI.Mfg.NTamd64.10.0.1..19044]\r\n" +
            "\"%AMD15BF.781%\" = ati2mtag_Phoenix, PCI\\VEN_1002&DEV_15BF&REV_00\r\n" +
            "\"%AMD15BF.781%\" = ati2mtag_Phoenix, PCI\\VEN_1002&DEV_15BF&REV_C1\r\n" +
            "\"%AMD15C8.1%\" = ati2mtag_Phoenix, PCI\\VEN_1002&DEV_15C8&REV_C1\r\n\r\n" +
            "[ati2mtag_SoftwareDeviceSettings]\r\n" +
            "HKR,,DriverBuildNumber,%REG_SZ%,2610280849-" + ConversionPipelineTests.AmdRelease + "\r\n\r\n" +
            "[Strings]\r\nAMD15BF.781 = \"AMD Radeon 780M Graphics\"\r\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(displayDir, "u0205499.cat"), "ORIGINAL AMD CATALOG");
        File.WriteAllText(Path.Combine(bin, "amdkmdag.sys"), "kernel driver");
        File.WriteAllText(Path.Combine(bin, "ccc2_install.exe"), "ccc2");
        File.WriteAllBytes(Path.Combine(bin, "amdgcf.dat"), AmdGcf.Write(
            new[] { new GcfRecord(0x15BF, 0x00), new GcfRecord(0x15BF, 0xC1), new GcfRecord(0x15C8, 0xC1) }, ConversionPipelineTests.AmdRelease));
        var xe = Directory.CreateDirectory(Path.Combine(displayDir, "amdxe")).FullName;
        File.WriteAllText(Path.Combine(xe, "amdxe.inf"), "[Version]\r\nClass=System\r\nCatalogFile=amdxe.cat\r\nDriverVer=03/13/2026, 26.10.0.0001\r\n");
        File.WriteAllText(Path.Combine(xe, "amdxe.cat"), "AMD XE CATALOG");
    }

    public static void WriteLenovo(string displayDir)
    {
        var bin = Directory.CreateDirectory(Path.Combine(displayDir, "B026069")).FullName;
        File.WriteAllText(Path.Combine(displayDir, "u0202094.inf"),
            "[Version]\r\nClass=Display\r\nCatalogFile=u0202094.cat\r\nDriverVer=06/29/2026, " + ConversionPipelineTests.LenovoVer + "\r\n" +
            "[ATI.Mfg.NTamd64.10.0.1..19044]\r\n" +
            "\"%AMD15BF.1%\" = ati2mtag_Phoenix, " + ConversionPipelineTests.Go1 + "\r\n" +
            "[x]\r\nHKR,,DriverBuildNumber,%REG_SZ%,2606290140-" + ConversionPipelineTests.LenovoRelease + "\r\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(displayDir, "u0202094.cat"), "LENOVO CATALOG");
        File.WriteAllText(Path.Combine(displayDir, "amduw23e.inf"),
            "[Version]\r\nClass=Extension\r\nDriverVer=06/29/2026, " + ConversionPipelineTests.LenovoVer + "\r\n[M]\r\n" +
            "\"%AMD15BF.1%\" = ati2mtag_Phoenix, " + ConversionPipelineTests.Go1 + "\r\n" +
            "\"%AMD15C8.1%\" = ati2mtag_Phoenix, " + ConversionPipelineTests.Go1Other + "\r\n", Encoding.ASCII);
        File.WriteAllText(Path.Combine(bin, "amdkmdag.sys"), "lenovo kernel driver");
        File.WriteAllBytes(Path.Combine(bin, "amdgcf.dat"), AmdGcf.Write(
            new[] { new GcfRecord(0x15BF, 0x04), new GcfRecord(0x15C8, 0xC9) }, ConversionPipelineTests.LenovoRelease));
    }
}
