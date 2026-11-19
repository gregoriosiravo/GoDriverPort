using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace LegionGoConverter.Core;

public sealed record ConversionRequest(string AmdInstaller, string LenovoInstaller, string WorkRoot, bool InstallDriver, bool InstallAmdSoftware, string? ExportFolder);

public enum Stage { Preflight, ExtractLenovo, ExtractAmd, Analyze, Build, Sign, Confirm, RestorePoint, StageLenovo, TrustCertificate, InstallDriver, VerifyDriver, InstallAmdSoftware, Export, Done }

public sealed record StageProgress(Stage Stage, string Message, double? Fraction);

public interface IUserPrompt
{
    Task<bool> ConfirmAsync(string title, string message, CancellationToken ct);
}

/// <summary><see cref="SessionDir"/> holds <c>session.log</c> and (once anything was about to change) <c>manifest.json</c>.</summary>
public sealed record ConversionResult(bool Success, bool RebootRequired, string SessionDir, string? Error,
    bool VerificationPending = false, string? Notice = null);

/// <summary>Removes AMD Software products by ProductCode; implemented by <see cref="AmdSoftwareUninstaller"/> (needs no bundled tools).</summary>
public interface IAmdSoftwareUninstaller
{
    Task UninstallAsync(IReadOnlyList<string> productCodes, CancellationToken ct);
}

/// <summary>The AMD Software (Adrenalin) step; implemented by <see cref="AmdSoftwareInstaller"/>.</summary>
public interface IAmdSoftwareInstaller : IAmdSoftwareUninstaller
{
    Task<AmdSoftwareInstallResult> InstallDetailedAsync(string ccc2Path, string workDir, CancellationToken ct);
}

/// <summary>Everything the pipeline uses to look at or change the system. Services that log take the run's session log,
/// hence the factories. <see cref="CertificateProvider"/> returns a signing certificate (with private key, in
/// CurrentUser\My) that the pipeline disposes.</summary>
public sealed record PipelineServices(
    IProcessRunner Runner,
    ToolPaths Tools,
    IDeviceProbe DeviceProbe,
    IEnvironmentProbe EnvironmentProbe,
    IInstallerInspector Inspector,
    ITrustStore TrustStore,
    Func<ISessionLog, IRestorePoint> RestorePointFactory,
    Func<ISessionLog, IDriverInstaller> DriverInstallerFactory,
    Func<ISessionLog, IAmdSoftwareInstaller> AmdSoftwareFactory,
    Func<X509Certificate2> CertificateProvider)
{
    /// <summary><c>C:\ProgramData\LegionGoDriverConverter</c> (ASCII on a standard install).</summary>
    // Legacy identifier kept for compatibility: the folder keeps its pre-rename name so existing installs, sessions and rollback manifests keep working.
    public static string DataRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LegionGoDriverConverter");

    /// <summary>One timestamped subfolder per run holds the session log and the manifest (never inside the work folder).</summary>
    public string SessionsRoot { get; init; } = Path.Combine(DataRoot, "sessions");
    /// <summary>Lenovo's original display package is kept here (one subfolder per driver version) for rollback.</summary>
    public string LenovoOriginalRoot { get; init; } = Path.Combine(DataRoot, "lenovo-original");
    public int VerifyAttempts { get; init; } = 10;
    public TimeSpan VerifyDelay { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Called first, before anything under <see cref="DataRoot"/> is created or read: secures the data folder and
    /// returns a line for the log, or throws <see cref="DataRootSecurityException"/> to stop the run. Null skips it (tests
    /// with temp roots); <see cref="CreateDefault"/> sets <see cref="DataRootSecurity.ForDefaultRoot"/>.</summary>
    public Func<string>? SecureDataRoot { get; init; }

    /// <summary>The real services. Only <see cref="ConversionPipeline.RunAsync"/> after the user's confirmation uses the mutating ones.</summary>
    public static PipelineServices CreateDefault(ToolPaths tools)
    {
        var runner = new ProcessRunner();
        return new PipelineServices(runner, tools, new WmiDeviceProbe(), new WindowsEnvironmentProbe(),
            new ListingInstallerInspector(runner, tools), new LocalMachineTrustStore(),
            log => new SystemRestorePoint(runner, log), log => new PnpUtilDriverInstaller(runner, log),
            log => new AmdSoftwareInstaller(runner, tools, log), () => SigningCertificate.GetOrCreate())
        {
            SecureDataRoot = () => DataRootSecurity.ForDefaultRoot().Ensure(),
        };
    }
}

/// <summary>Result of the in-memory analysis: the patched INF and DAT bytes, where they go (relative to the display
/// package folder), the catalog file named by the INF, and every INF of the package with its DriverVer.</summary>
internal sealed record PackageAnalysis(string InfRelativePath, string BinariesRelativePath, byte[] PatchedInf, byte[] PatchedDat,
    string CatalogName, IReadOnlyList<PackageRef> Packages);

/// <summary>Analyze, Build and Sign, shared by the pipeline and the real-package test.</summary>
internal static class ConversionSteps
{
    static readonly Regex CatalogFile = new(@"^\s*CatalogFile(\.NTamd64)?\s*=\s*(?<n>[^\s;]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>Patches the INF and amdgcf.dat in memory (the DAT header self-test runs on read and again on the result).
    /// Throws <see cref="PackageLayoutException"/> on anything unexpected; nothing is written.</summary>
    public static PackageAnalysis Analyze(AmdPackage amd, ResolvedTarget target, ISessionLog log)
    {
        var binRel = Path.GetRelativePath(amd.DisplayDir, amd.BinariesDir);
        if (binRel == "." || binRel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(binRel))
            throw new PackageLayoutException($"The driver binaries folder {amd.BinariesDir} is not inside the display package {amd.DisplayDir}.");
        var infRel = Path.GetRelativePath(amd.DisplayDir, amd.InfPath);

        // INF
        var infBytes = File.ReadAllBytes(amd.InfPath);
        var patch = InfPatcher.AddDeviceId(infBytes, target.HardwareId);
        log.Info(patch.AlreadyPresent
            ? $"Analyze: the AMD INF already lists {target.HardwareId} ({patch.InsertedLine}); the INF is not changed."
            : $"Analyze: INF line added in [{patch.Section}]: {patch.InsertedLine}");

        var infText = AmdPackageLocator.ReadInf(amd.InfPath);
        var cats = CatalogFile.Matches(infText).Select(m => m.Groups["n"].Value.Trim('"')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cats.Count != 1)
            throw new PackageLayoutException($"Expected exactly one CatalogFile name in {amd.InfPath}, found {cats.Count}: {string.Join(", ", cats)}.");
        var catName = cats[0];
        if (!catName.EndsWith(".cat", StringComparison.OrdinalIgnoreCase) || catName.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
            throw new PackageLayoutException($"Unexpected CatalogFile name '{catName}' in {amd.InfPath}.");

        // amdgcf.dat
        var datPath = Path.Combine(amd.BinariesDir, "amdgcf.dat");
        byte[] dat, patchedDat;
        IReadOnlyList<GcfRecord> before, after;
        try
        {
            dat = File.ReadAllBytes(datPath);
            before = AmdGcf.Read(dat, amd.ReleaseString);
            patchedDat = AmdGcf.AddRecords(dat, amd.ReleaseString, new[] { target.GcfRecord });
            after = AmdGcf.Read(patchedDat, amd.ReleaseString);
        }
        catch (AmdGcfFormatException e)
        {
            throw new PackageLayoutException($"AMD's amdgcf.dat ({datPath}) is not in the format this converter understands, so it cannot be " +
                                             $"patched safely. Nothing was changed. ({e.Message})");
        }
        bool present = before.Contains(target.GcfRecord);
        if (!after.Contains(target.GcfRecord) || after.Count != before.Count + (present ? 0 : 1))
            throw new PackageLayoutException("amdgcf.dat verification failed after adding the device record. Nothing was changed.");
        log.Info(present
            ? $"Analyze: amdgcf.dat already allows device {target.GcfRecord.Device:X4} revision {target.GcfRecord.Revision:X2}."
            : $"Analyze: amdgcf.dat self-test passed; device {target.GcfRecord.Device:X4} revision {target.GcfRecord.Revision:X2} added ({before.Count} -> {after.Count} records).");

        // every INF in the display package (for rollback)
        var packages = new List<PackageRef>();
        foreach (var f in Directory.EnumerateFiles(amd.DisplayDir, "*.inf", AmdPackageLocator.Recurse).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var m = AmdPackageLocator.DriverVer.Match(AmdPackageLocator.ReadInf(f));
            if (!m.Success || !Version.TryParse(m.Groups[1].Value, out _))
                throw new PackageLayoutException($"DriverVer not found or unparsable in {f}; rollback could not identify that package.");
            packages.Add(new PackageRef(Path.GetFileName(f), m.Groups[1].Value,
                IsDisplay: string.Equals(Path.GetFullPath(f), Path.GetFullPath(amd.InfPath), StringComparison.OrdinalIgnoreCase)));
        }
        log.Info("Analyze: driver packages: " + string.Join(", ", packages.Select(p => $"{p.InfName} {p.Version}{(p.IsDisplay ? " (display)" : "")}")));
        return new PackageAnalysis(infRel, binRel, patch.Bytes, patchedDat, catName, packages);
    }

    /// <summary>Copies the AMD display package to <paramref name="convertedDir"/> (which must not exist) and writes the
    /// patched INF and DAT into the copy. The AMD source is never modified.</summary>
    public static async Task BuildAsync(AmdPackage amd, PackageAnalysis a, string convertedDir, Action<double>? fraction, CancellationToken ct)
    {
        if (Directory.Exists(convertedDir) || File.Exists(convertedDir))
            throw new IOException($"Build folder already exists: {convertedDir}");
        await TreeCopy.CopyAsync(amd.DisplayDir, convertedDir, fraction, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        File.WriteAllBytes(Path.Combine(convertedDir, a.InfRelativePath), a.PatchedInf);
        File.WriteAllBytes(Path.Combine(convertedDir, a.BinariesRelativePath, "amdgcf.dat"), a.PatchedDat);
    }

    /// <summary>Builds the catalog next to the INF (replacing AMD's) and signs it.</summary>
    public static Task SignAsync(CatalogBuilder builder, string convertedDir, PackageAnalysis a, X509Certificate2 cert, CancellationToken ct)
    {
        var inf = Path.Combine(convertedDir, a.InfRelativePath);
        return builder.BuildAndSignAsync(Path.GetDirectoryName(inf)!, Path.GetFileName(inf),
            Path.Combine(convertedDir, a.BinariesRelativePath), a.CatalogName, cert, ct);
    }
}

/// <summary>File-tree copy and comparison helpers.</summary>
internal static class TreeCopy
{
    /// <summary>Relative path -> size of every file under <paramref name="root"/> (reparse points skipped).</summary>
    public static Dictionary<string, long> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", AmdPackageLocator.Recurse)
            .ToDictionary(f => Path.GetRelativePath(root, f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);

    public static bool Same(IReadOnlyDictionary<string, long> a, IReadOnlyDictionary<string, long> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var len) && len == kv.Value);

    /// <summary>True when every <c>.inf</c> and <c>.cat</c> file (relative path) has the same SHA-256 in both trees. The
    /// catalog then vouches for every other file (Windows checks the hashes it lists when the package is staged).</summary>
    public static bool SameInfAndCatalogHashes(string a, string b, IEnumerable<string> relativePaths)
    {
        foreach (var rel in relativePaths)
        {
            var ext = Path.GetExtension(rel);
            if (!ext.Equals(".inf", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".cat", StringComparison.OrdinalIgnoreCase)) continue;
            var pa = Path.Combine(a, rel);
            var pb = Path.Combine(b, rel);
            if (!File.Exists(pa) || !File.Exists(pb)) return false;
            if (!Sha256(pa).AsSpan().SequenceEqual(Sha256(pb))) return false;
        }
        return true;
    }

    static byte[] Sha256(string path)
    {
        using var s = File.OpenRead(path);
        return System.Security.Cryptography.SHA256.HashData(s);
    }

    /// <summary>Copies every file (cancellation checked between files) and clears the read-only attribute on the copies.</summary>
    public static async Task CopyAsync(string from, string to, Action<double>? fraction, CancellationToken ct)
    {
        var files = Snapshot(from);
        long total = Math.Max(1, files.Values.Sum()), done = 0;
        Directory.CreateDirectory(to);
        foreach (var (rel, len) in files.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var dst = Path.Combine(to, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            await Task.Run(() => File.Copy(Path.Combine(from, rel), dst), ct).ConfigureAwait(false);
            var attrs = File.GetAttributes(dst);
            if ((attrs & FileAttributes.ReadOnly) != 0) File.SetAttributes(dst, attrs & ~FileAttributes.ReadOnly);
            done += len;
            fraction?.Invoke(done / (double)total);
        }
    }

    public static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Converts an AMD driver for the Legion Go and (optionally) installs it. Stages up to <see cref="Stage.Sign"/>
/// only write the work folder, the session folder, the retained Lenovo copy and a certificate in CurrentUser\My. Nothing
/// else changes until the user confirms the exact list of changes; the session manifest is written before the first
/// change so a rollback is always possible.</summary>
public sealed class ConversionPipeline
{
    public const string ManifestFileName = "manifest.json";
    public const string CertificateFileName = "LegionGoDriverPort.cer";

    readonly PipelineServices _s;

    public ConversionPipeline(PipelineServices services) { _s = services ?? throw new ArgumentNullException(nameof(services)); }

    /// <summary><c>C:\ProgramData\LegionGoDriverConverter\work</c>: an ASCII path makecat can read.</summary>
    public static string DefaultWorkRoot { get; } = Path.Combine(PipelineServices.DataRoot, "work");

    public async Task<ConversionResult> RunAsync(ConversionRequest r, IProgress<StageProgress> progress, IUserPrompt prompt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(r);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(prompt);
        string? dataRootNote = null;
        if (_s.SecureDataRoot is { } secure)
        {
            try { dataRootNote = secure(); }
            catch (DataRootSecurityException e) { return new ConversionResult(false, false, "", e.Message + " Nothing was changed."); }
        }
        string sessionDir;
        FileSessionLog log;
        try
        {
            sessionDir = NewUniqueDir(_s.SessionsRoot);
            log = new FileSessionLog(sessionDir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new ConversionResult(false, false, "", $"Could not create the session folder under {_s.SessionsRoot}: {e.Message}. Nothing was changed.");
        }
        using (log)
        {
            if (dataRootNote is not null) log.Info("Data folder: " + dataRootNote);
            var run = new Run(_s, r, progress, prompt, log, sessionDir);
            return await run.ExecuteAsync(ct).ConfigureAwait(false);
        }
    }

    internal static string NewUniqueDir(string root)
    {
        Directory.CreateDirectory(root);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        for (int i = 0; ; i++)
        {
            var p = Path.Combine(root, i == 0 ? stamp : $"{stamp}-{i}");
            if (Directory.Exists(p) || File.Exists(p)) continue;
            Directory.CreateDirectory(p);
            return p;
        }
    }

    /// <summary>A stop that is not an error in a stage (declined confirmation, failed confirmation dialog).</summary>
    sealed class StopException(string message) : Exception(message);

    sealed class Run
    {
        readonly PipelineServices _s;
        readonly ConversionRequest _r;
        readonly IProgress<StageProgress> _progress;
        readonly IUserPrompt _prompt;
        readonly FileSessionLog _log;
        readonly string _sessionDir;
        readonly string _manifestPath;

        Stage _stage = Stage.Preflight;
        bool _mutated;
        bool _reboot;
        /// <summary>The restart flag of the AddAndInstall call alone (staging and other flags do not count for verification).</summary>
        bool _installReboot;
        bool _verificationPending;
        const string PendingNotice =
            "The driver was installed but could not be verified yet. Restart Windows, then check Device Manager / run the app again to confirm.";
        SecurityState? _security;
        string _runDir = "", _lenovoDir = "", _amdDir = "", _convertedDir = "";
        LegionGoProfile? _profile;
        AmdPackage? _amd;
        ResolvedTarget? _target;
        PackageAnalysis? _analysis;
        X509Certificate2? _cert;
        string _lenovoInf = "";
        SessionManifest? _manifest;
        IDriverInstaller? _installer;

        public Run(PipelineServices s, ConversionRequest r, IProgress<StageProgress> progress, IUserPrompt prompt, FileSessionLog log, string sessionDir)
        {
            _s = s; _r = r; _progress = progress; _prompt = prompt; _log = log; _sessionDir = sessionDir;
            _manifestPath = Path.Combine(sessionDir, ManifestFileName);
        }

        LegionGoProfile Profile => _profile!;
        AmdPackage Amd => _amd!;
        ResolvedTarget Target => _target!;
        PackageAnalysis Analysis => _analysis!;
        X509Certificate2 Cert => _cert!;
        SessionManifest Manifest => _manifest!;

        public async Task<ConversionResult> ExecuteAsync(CancellationToken ct)
        {
            _log.Info($"Conversion started. Lenovo installer: {_r.LenovoInstaller}; AMD installer: {_r.AmdInstaller}; work root: {_r.WorkRoot}; " +
                      $"install driver: {_r.InstallDriver}; install AMD Software: {_r.InstallAmdSoftware}; export folder: {_r.ExportFolder ?? "(none)"}.");
            try
            {
                Enter(Stage.Preflight, "Checking this PC and the two installers");
                _security = await Preflight.RunAsync(_r, _s, _log, ct).ConfigureAwait(false);

                await ExtractAsync(ct).ConfigureAwait(false);
                Analyze(ct);
                await BuildAsync(ct).ConfigureAwait(false);
                await SignAsync(ct).ConfigureAwait(false);

                var exportCt = ct;
                if (_r.InstallDriver)
                {
                    await ConfirmAsync(ct).ConfigureAwait(false);
                    // From the confirmation on, nothing is cancellable: a stray Cancel must never skip verification or the
                    // rollback offer after the driver store was changed.
                    var post = CancellationToken.None;
                    exportCt = post;
                    _log.Info("Confirm: from here on the run cannot be cancelled.");
                    await RestorePointAsync(post).ConfigureAwait(false);
                    await StageLenovoAsync(post).ConfigureAwait(false);
                    TrustCertificate(post);
                    if (await InstallAndVerifyAsync(post).ConfigureAwait(false) is { } failed) return failed;
                    if (_r.InstallAmdSoftware && await InstallAmdSoftwareAsync(post).ConfigureAwait(false) is { } swFailed) return swFailed;
                }
                else
                {
                    _log.Info("Export only: nothing is installed and nothing on the system changes, so no confirmation is needed.");
                }

                if (_r.ExportFolder is not null) await ExportAsync(exportCt).ConfigureAwait(false);

                var done = _verificationPending ? "Done. " + PendingNotice : _reboot ? "Done. Restart Windows to finish." : "Done.";
                Enter(Stage.Done, done);
                return new ConversionResult(true, _reboot, _sessionDir, null, _verificationPending, _verificationPending ? PendingNotice : null);
            }
            catch (StopException e)
            {
                _log.Warn("Stopped: " + e.Message);
                return Failed(e.Message);
            }
            catch (OperationCanceledException e) when (ct.IsCancellationRequested)
            {
                _log.Warn($"Cancelled during {_stage}.");
                _log.Info("Cancellation detail: " + e.Message);
                return Failed($"Cancelled during {_stage}. " + StateNote());
            }
            catch (Exception e)
            {
                return Fail(e);
            }
            finally
            {
                _cert?.Dispose();
            }
        }

        ConversionResult Failed(string error) =>
            new(false, _reboot, _sessionDir, error, _verificationPending, _verificationPending ? PendingNotice : null);

        ConversionResult Fail(Exception e, string? extra = null)
        {
            _log.Error($"{_stage} failed", e);
            var msg = $"{_stage} failed: {e.Message}" + (extra is null ? "" : " " + extra);
            return Failed(msg + " " + StateNote());
        }

        string StateNote() => _mutated
            ? $"Changes were already made to this PC; the session manifest {_manifestPath} lets the app roll them back. Log: {_log.Path}"
            : $"Nothing on the system was changed. Log: {_log.Path}";

        void Enter(Stage stage, string message)
        {
            _stage = stage;
            _log.Info($"== {stage}: {message}");
            _progress.Report(new StageProgress(stage, message, null));
        }

        void Report(string message, double? fraction = null) => _progress.Report(new StageProgress(_stage, message, fraction));

        IProgress<string> Lines() => new LineSink(this);

        sealed class LineSink(Run run) : IProgress<string>
        {
            public void Report(string value) { if (!string.IsNullOrWhiteSpace(value)) run.Report(value); }
        }

        // ------------------------------------------------------------------ extract / analyze / build / sign

        async Task ExtractAsync(CancellationToken ct)
        {
            Enter(Stage.ExtractLenovo, "Unpacking Lenovo's driver package");
            _runDir = NewUniqueDir(_r.WorkRoot);
            _log.Info("Work folder for this run: " + _runDir);
            _lenovoDir = Path.Combine(_runDir, "lenovo");
            await new InnoExtractor(_s.Runner, _s.Tools.InnoExtract).ExtractAsync(_r.LenovoInstaller, _lenovoDir, Lines(), ct).ConfigureAwait(false);

            Enter(Stage.ExtractAmd, "Unpacking AMD's driver package");
            _amdDir = Path.Combine(_runDir, "amd");
            await new SevenZipExtractor(_s.Runner, _s.Tools.SevenZip).ExtractAsync(_r.AmdInstaller, _amdDir, Lines(), ct).ConfigureAwait(false);
        }

        void Analyze(CancellationToken ct)
        {
            Enter(Stage.Analyze, "Reading both packages and matching this device");
            _profile = LenovoPackage.Load(_lenovoDir);
            _log.Info($"Analyze: Lenovo package {Profile.LenovoDriverVersion}, display INF {Profile.LenovoDisplayInfPath}, " +
                      $"IDs {string.Join(", ", Profile.HardwareIds)}.");
            _amd = AmdPackageLocator.Locate(_amdDir);
            _log.Info($"Analyze: AMD package {Amd.DriverVersion} (release {Amd.ReleaseString}), INF {Amd.InfPath}, binaries {Amd.BinariesDir}.");
            ct.ThrowIfCancellationRequested();
            _target = TargetResolver.Resolve(_s.DeviceProbe.GetAmdDisplayDevices(), Profile);
            _log.Info($"Analyze: device {Target.Device.InstanceId} matches {Target.HardwareId}; current driver {Target.Device.DriverVersion ?? "unknown"}, problem code {Target.Device.ProblemCode}.");
            if (Amd.DriverVersion <= Profile.LenovoDriverVersion)
                _log.Warn($"Analyze: the AMD driver {Amd.DriverVersion} is not newer than Lenovo's {Profile.LenovoDriverVersion}.");
            _analysis = ConversionSteps.Analyze(Amd, Target, _log);
            if (_r.InstallAmdSoftware && Amd.Ccc2Path is null)
                throw new PackageLayoutException($"AMD Software was requested but ccc2_install.exe was not found next to the driver binaries in {Amd.BinariesDir}.");
        }

        async Task BuildAsync(CancellationToken ct)
        {
            Enter(Stage.Build, "Building the converted driver package");
            _convertedDir = Path.Combine(_runDir, "converted", Path.GetFileName(Amd.DisplayDir));
            await ConversionSteps.BuildAsync(Amd, Analysis, _convertedDir, f => Report("Copying the AMD package", f), ct).ConfigureAwait(false);
            _log.Info("Build: converted package at " + _convertedDir);
            if (_r.InstallDriver) _lenovoInf = await RetainLenovoAsync(ct).ConfigureAwait(false);
        }

        /// <summary>Keeps Lenovo's display package (the display INF's whole folder) under LenovoOriginalRoot\&lt;version&gt;.
        /// An identical copy (same files and sizes, same SHA-256 of every INF and catalog) is reused; a different one is
        /// renamed aside, never deleted.</summary>
        async Task<string> RetainLenovoAsync(CancellationToken ct)
        {
            var src = Path.GetDirectoryName(Profile.LenovoDisplayInfPath)!;
            var infRel = Path.GetRelativePath(src, Profile.LenovoDisplayInfPath);
            var dst = Path.Combine(_s.LenovoOriginalRoot, Profile.LenovoDriverVersion.ToString());
            var want = TreeCopy.Snapshot(src);
            if (Directory.Exists(dst) && TreeCopy.Same(TreeCopy.Snapshot(dst), want) && TreeCopy.SameInfAndCatalogHashes(src, dst, want.Keys))
            {
                _log.Info($"Build: Lenovo's original driver is already kept at {dst} (identical copy, reused).");
                return Path.Combine(dst, infRel);
            }
            long bytes = want.Values.Sum();
            long free = _s.EnvironmentProbe.FreeBytes(_s.LenovoOriginalRoot);
            if (free < bytes + (512L << 20))
                throw new IOException($"Not enough free disk space to keep Lenovo's original driver for rollback at {dst}: " +
                                      $"{bytes / (double)Preflight.Gb:0.0} GB needed, {free / (double)Preflight.Gb:0.0} GB free.");
            Directory.CreateDirectory(_s.LenovoOriginalRoot);
            var tmp = dst + ".partial-" + Guid.NewGuid().ToString("N")[..8];
            _log.Info($"Build: keeping Lenovo's original driver ({want.Count} files, {bytes / (double)Preflight.Gb:0.0} GB) at {dst}.");
            try
            {
                await TreeCopy.CopyAsync(src, tmp, f => Report("Keeping Lenovo's original driver for rollback", f), ct).ConfigureAwait(false);
                if (!TreeCopy.Same(TreeCopy.Snapshot(tmp), want) || !TreeCopy.SameInfAndCatalogHashes(src, tmp, want.Keys))
                    throw new IOException($"The copy of Lenovo's driver at {tmp} does not match the source {src}.");
            }
            catch
            {
                TreeCopy.TryDelete(tmp); // only our own, just-created, incomplete copy
                throw;
            }
            if (Directory.Exists(dst))
            {
                var aside = dst + ".old-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..4];
                Directory.Move(dst, aside);
                _log.Warn($"Build: an older, different copy at {dst} was moved to {aside} (kept, not deleted).");
            }
            Directory.Move(tmp, dst);
            return Path.Combine(dst, infRel);
        }

        async Task SignAsync(CancellationToken ct)
        {
            Enter(Stage.Sign, "Building and signing the catalog");
            _cert = _s.CertificateProvider();
            _log.Info($"Sign: certificate {Cert.Subject}, thumbprint {Cert.Thumbprint} (CurrentUser\\My).");
            if (SigningCertificate.IsKeyExportable(Cert)) _log.Warn("Sign: " + ExportableKeyWarning);
            await ConversionSteps.SignAsync(new CatalogBuilder(_s.Runner, _s.Tools), _convertedDir, Analysis, Cert, ct).ConfigureAwait(false);
            _log.Info("Sign: catalog " + Analysis.CatalogName + " built and signed.");
        }

        string ExportableKeyWarning =>
            $"the private key of the signing certificate {Cert.Thumbprint} in CurrentUser\\My is exportable (probably an earlier, manually made " +
            "certificate with the same subject). Anyone who can run code as you could copy the key and sign code this PC trusts. The app does " +
            "not delete it; see the README to replace it with a non-exportable one created by the app.";

        // ------------------------------------------------------------------ confirmation gate

        async Task ConfirmAsync(CancellationToken ct)
        {
            Enter(Stage.Confirm, "Waiting for your confirmation");
            ct.ThrowIfCancellationRequested();
            bool trusted = _s.TrustStore.IsTrusted(Cert.Thumbprint);
            var message = ConfirmMessage(trusted);
            _log.Info("Confirm: asking the user:\n" + message);
            bool yes;
            try { yes = await _prompt.ConfirmAsync("Apply these changes to this PC?", message, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                _log.Error("Confirm: the confirmation dialog failed", e);
                throw new StopException($"The confirmation dialog failed ({e.Message}), so nothing was installed. " + StateNote());
            }
            ct.ThrowIfCancellationRequested();
            if (!yes) throw new StopException("You declined the changes. " + StateNote());
            _log.Info("Confirm: the user confirmed.");
        }

        string ConfirmMessage(bool trusted)
        {
            var sb = new StringBuilder();
            int n = 0;
            void Item(string s) => sb.Append(++n).Append(". ").Append(s).Append("\r\n");
            sb.Append("The following changes will be made to this PC:\r\n\r\n");
            Item($"Display driver for {Target.Device.InstanceId} (hardware ID {Target.HardwareId}): " +
                 $"{Target.Device.DriverVersion ?? "unknown"} now (Lenovo package {Profile.LenovoDriverVersion}) -> AMD {Amd.DriverVersion} (release {Amd.ReleaseString}).");
            Item("Create a System Restore point first. If System Protection is off for the system drive, it is turned on " +
                 "(Enable-ComputerRestore) so the restore point can be made. Windows reuses a restore point made in the last 24 hours.");
            Item($"Make sure Lenovo's original display driver ({Path.GetFileName(_lenovoInf)} {Profile.LenovoDriverVersion}) is in the Windows driver store, " +
                 $"from the copy kept at {_lenovoInf}, so you can always go back.");
            Item(trusted
                ? $"The certificate \"{Cert.Subject}\" (thumbprint {Cert.Thumbprint}) is already trusted in the Local Machine Trusted Root and Trusted Publishers stores: no change."
                : $"Trust the certificate \"{Cert.Subject}\" (thumbprint {Cert.Thumbprint}) in the Local Machine \"Trusted Root Certification Authorities\" and \"Trusted Publishers\" stores.");
            Item($"Install the converted driver package ({Analysis.Packages.Count} INF packages) from {_convertedDir} with pnputil.");
            if (_r.InstallAmdSoftware)
                Item($"Remove Lenovo's Microsoft Store app \"AMD Radeon Software\" ({AmdSoftwareInstaller.StoreAppName}) and install AMD Software: Adrenalin Edition from ccc2_install.exe. An older AMD Software already installed is uninstalled first; a newer one is kept.");
            Item("A restart may be required at the end.");
            if (SigningCertificate.IsKeyExportable(Cert)) sb.Append("\r\nNote: ").Append(ExportableKeyWarning).Append("\r\n");
            sb.Append("\r\nOnce you confirm, the changes run to the end and cannot be cancelled (the result is always checked, and a rollback is offered if it fails).\r\n");
            sb.Append("\r\n").Append(Preflight.Describe(_security)).Append(". None of these is changed.\r\n");
            sb.Append($"Session log and rollback manifest: {_sessionDir}");
            return sb.ToString();
        }

        // ------------------------------------------------------------------ system changes (after Confirm)

        void SaveManifest()
        {
            Manifest.Save(_manifestPath);
            _log.Info("Manifest written: " + _manifestPath);
        }

        async Task RestorePointAsync(CancellationToken ct)
        {
            Enter(Stage.RestorePoint, "Creating a System Restore point");
            _installer = _s.DriverInstallerFactory(_log);
            var pre = await _installer.ListAsync(ct).ConfigureAwait(false);
            _log.Info($"Driver store before install: {pre.Count} packages.");
            _manifest = new SessionManifest(Amd.DriverVersion.ToString(), Cert.Thumbprint, Cert.Subject, Analysis.Packages, _lenovoInf,
                pre.Select(d => d.Published).ToList(), Array.Empty<string>());
            SaveManifest(); // before the first change

            ct.ThrowIfCancellationRequested();
            _mutated = true;
            var outcome = await _s.RestorePointFactory(_log).CreateAsync($"Legion Go Driver Port: AMD {Amd.DriverVersion}", ct).ConfigureAwait(false);
            _log.Info(outcome == RestorePointOutcome.Created
                ? "Restore point created."
                : "A restore point from the last 24 hours exists; Windows allows one per day, so that one is used.");
        }

        /// <summary>Lenovo's display driver is put into the driver store (store only, active driver unchanged) BEFORE the
        /// certificate is trusted: if this fails, nothing signed by our certificate is trusted or installed.</summary>
        async Task StageLenovoAsync(CancellationToken ct)
        {
            Enter(Stage.StageLenovo, "Keeping Lenovo's driver available in the driver store");
            var installer = _installer!;
            var store = await installer.ListAsync(ct).ConfigureAwait(false);
            var lenovoName = Path.GetFileName(_lenovoInf);
            if (store.Any(d => Eq(d.Original, lenovoName)))
            {
                _log.Info($"Lenovo's display driver {lenovoName} is already in the driver store.");
                return;
            }
            _log.Warn($"Lenovo's display driver {lenovoName} is not in the driver store; staging it from {_lenovoInf} (store only, active driver unchanged).");
            _reboot |= (await installer.StageAsync(_lenovoInf, ct).ConfigureAwait(false)).RebootRequired;
            store = await installer.ListAsync(CancellationToken.None).ConfigureAwait(false);
            if (!store.Any(d => Eq(d.Original, lenovoName)))
                throw new DriverStoreException($"Lenovo's display driver {lenovoName} was staged but is not listed in the driver store.");
            _log.Info($"Lenovo's display driver {lenovoName} is in the driver store.");
        }

        void TrustCertificate(CancellationToken ct)
        {
            Enter(Stage.TrustCertificate, "Trusting the signing certificate");
            ct.ThrowIfCancellationRequested();
            if (_s.TrustStore.IsTrusted(Cert.Thumbprint))
            {
                _log.Info($"Certificate {Cert.Thumbprint} is already trusted; no change.");
                return;
            }
            _s.TrustStore.Trust(Cert);
            if (!_s.TrustStore.IsTrusted(Cert.Thumbprint))
                throw new TrustStoreException($"The certificate {Cert.Thumbprint} is still not trusted after adding it.");
            _log.Info($"Certificate {Cert.Thumbprint} trusted in LocalMachine Root and TrustedPublisher.");
        }

        /// <summary>InstallDriver + VerifyDriver. Returns null on success; on failure, offers rollback and returns the result.</summary>
        async Task<ConversionResult?> InstallAndVerifyAsync(CancellationToken ct)
        {
            try
            {
                Enter(Stage.InstallDriver, "Installing the converted driver");
                ct.ThrowIfCancellationRequested();
                var installer = _installer!;
                var store = await installer.ListAsync(ct).ConfigureAwait(false);
                if (ConvertedAlreadyActive(store))
                    _log.Info($"The converted driver {Amd.DriverVersion} signed by {Cert.Subject} is already in the driver store and running with " +
                              "problem code 0; it is not added again.");
                else
                {
                    ct.ThrowIfCancellationRequested();
                    var res = await installer.AddAndInstallAsync(_convertedDir, ct).ConfigureAwait(false);
                    _reboot |= res.RebootRequired;
                    _installReboot = res.RebootRequired;
                    _log.Info("pnputil install finished" + (res.RebootRequired ? "; Windows reports that a restart is required." : "."));
                }

                Enter(Stage.VerifyDriver, "Checking that the GPU runs the new driver");
                await VerifyAsync(ct).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                return await DriverFailureAsync(e, ct).ConfigureAwait(false);
            }
        }

        bool ConvertedAlreadyActive(IReadOnlyList<StoredDriver> store)
        {
            var displayInf = Path.GetFileName(Analysis.InfRelativePath);
            var want = DriverStoreParser.Normalize(Amd.DriverVersion);
            if (!store.Any(d => Eq(d.Original, displayInf) && DriverStoreParser.Normalize(d.Version) == want && RollbackPlanner.SignedBy(d.Signer, Cert.Subject)))
                return false;
            var dev = _s.DeviceProbe.GetAmdDisplayDevices().FirstOrDefault(d => Eq(d.InstanceId, Target.Device.InstanceId));
            return dev != null && dev.ProblemCode == 0 && SameVersion(dev.DriverVersion, Amd.DriverVersion);
        }

        async Task VerifyAsync(CancellationToken ct)
        {
            GpuDevice? dev = null;
            int attempts = Math.Max(1, _s.VerifyAttempts);
            for (int i = 1; i <= attempts; i++)
            {
                ct.ThrowIfCancellationRequested();
                dev = _s.DeviceProbe.GetAmdDisplayDevices().FirstOrDefault(d => Eq(d.InstanceId, Target.Device.InstanceId));
                _log.Info($"Verify attempt {i}: " + (dev == null ? "device not found" : $"driver {dev.DriverVersion ?? "unknown"}, problem code {dev.ProblemCode}"));
                if (dev != null && dev.ProblemCode == 0 && SameVersion(dev.DriverVersion, Amd.DriverVersion))
                {
                    _log.Info($"Verify: the GPU runs AMD {Amd.DriverVersion} with problem code 0.");
                    return;
                }
                if (i < attempts && _s.VerifyDelay > TimeSpan.Zero) await Task.Delay(_s.VerifyDelay, ct).ConfigureAwait(false);
            }
            // Only the install call's own restart request (3010) may explain an old driver; 14 = CM_PROB_NEED_RESTART.
            if (_installReboot && dev != null && dev.ProblemCode is 0 or 14)
            {
                _verificationPending = true;
                _log.Warn($"Verify: pnputil asked for a restart; the GPU still reports driver {dev.DriverVersion ?? "unknown"} " +
                          $"(problem code {dev.ProblemCode}). {PendingNotice}");
                return;
            }
            throw new InvalidOperationException(dev == null
                ? $"After installation the GPU {Target.Device.InstanceId} is no longer reported by Windows."
                : $"After installation the GPU reports driver {dev.DriverVersion ?? "unknown"} with problem code {dev.ProblemCode} " +
                  $"(expected {Amd.DriverVersion} with problem code 0).");
        }

        /// <summary>Describes what the GPU actually runs after a rollback (re-probed, never assumed).</summary>
        string AfterRollback(RollbackResult rb)
        {
            var head = "Rolled back: removed " +
                       (rb.Removed.Count == 0 ? "nothing (no converted package was in the driver store)" : string.Join("; ", rb.Removed)) + "." +
                       (rb.CertificateKept
                           ? $" The certificate {Cert.Thumbprint} stays trusted because other driver packages signed by it remain in the driver store."
                           : "");
            GpuDevice? dev = null;
            string? probeError = null;
            try { dev = _s.DeviceProbe.GetAmdDisplayDevices().FirstOrDefault(d => Eq(d.InstanceId, Target.Device.InstanceId)); }
            catch (Exception pe) when (pe is not OperationCanceledException) { probeError = pe.Message; }

            if (dev != null && dev.ProblemCode == 0 && SameVersion(dev.DriverVersion, Profile.LenovoDriverVersion))
                return head + $" The GPU now runs Lenovo's driver {dev.DriverVersion} ({dev.DriverInf ?? "?"}) with problem code 0." +
                       (_reboot ? " Restart Windows to finish." : "");
            var what = dev != null
                ? $"the GPU reports driver {dev.DriverVersion ?? "unknown"} ({dev.DriverInf ?? "?"}) with problem code {dev.ProblemCode}"
                : probeError != null ? $"the GPU could not be checked ({probeError})" : "Windows does not report the GPU";
            return head + $" However, {what}, not Lenovo's driver {Profile.LenovoDriverVersion} with problem code 0. Restart Windows; if it is " +
                   $"still wrong afterwards, re-add Lenovo's driver from the copy kept at {_lenovoInf} (pnputil /add-driver \"{_lenovoInf}\" /install) " +
                   "or use System Restore.";
        }

        async Task<ConversionResult> DriverFailureAsync(Exception e, CancellationToken ct)
        {
            var stage = _stage;
            _log.Error($"{stage} failed", e);
            var failure = $"{stage} failed: {e.Message}";
            var lenovoName = Path.GetFileName(_lenovoInf);
            bool yes = false;
            try
            {
                yes = await _prompt.ConfirmAsync("Driver installation failed: roll back?",
                    failure + "\r\n\r\nRoll back now? This removes the driver packages this run added, puts the GPU back on Lenovo's driver " +
                    $"({lenovoName}) and removes the certificate trust once none of the converted packages remain. The restore point is kept.", ct).ConfigureAwait(false);
            }
            catch (Exception pe) { _log.Error("The rollback question failed; treating it as \"no\"", pe); }

            if (!yes)
            {
                _log.Warn("Rollback declined; the system is left as it is.");
                return Failed($"{failure} Rollback was not run. Use the app's rollback with the session manifest {_manifestPath} to undo this run. Log: {_log.Path}");
            }
            try
            {
                _log.Info("Rolling back (user confirmed).");
                var rb = await new RollbackService(_installer!, _s.TrustStore, _log).RunAsync(Manifest, CancellationToken.None).ConfigureAwait(false);
                _reboot |= rb.RebootRequired;
                var outcome = AfterRollback(rb);
                _log.Info("Rollback outcome: " + outcome);
                return Failed($"{failure} {outcome} Log: {_log.Path}");
            }
            catch (Exception re)
            {
                _log.Error("Rollback failed", re);
                return Failed($"{failure} Rollback failed: {re.Message} The session manifest {_manifestPath} lets the app try again. Log: {_log.Path}");
            }
        }

        /// <summary>Returns null on success; a failure result otherwise (the driver itself stays installed).</summary>
        async Task<ConversionResult?> InstallAmdSoftwareAsync(CancellationToken ct)
        {
            Enter(Stage.InstallAmdSoftware, "Installing AMD Software: Adrenalin Edition");
            ct.ThrowIfCancellationRequested();
            var sw = _s.AmdSoftwareFactory(_log);
            var dir = Path.Combine(_runDir, "amd-software");
            try
            {
                var res = await sw.InstallDetailedAsync(Amd.Ccc2Path!, dir, ct).ConfigureAwait(false);
                _reboot |= res.RebootRequired;
                _manifest = Manifest with { AmdSoftwareProductCodes = res.InstalledByThisRun.ToList() };
                SaveManifest();
                _log.Info($"AMD Software installed. Installed by this run: {Join(res.InstalledByThisRun)}; already present: {Join(res.AlreadyPresent)}.");
                return null;
            }
            catch (AmdSoftwareException e)
            {
                return await AmdSoftwareFailureAsync(sw, e, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // Unexpected error: nothing is known to be installed by this run, but the driver itself is fine.
                return await AmdSoftwareFailureAsync(sw, new AmdSoftwareException(e.Message, inner: e), ct).ConfigureAwait(false);
            }
        }

        async Task<ConversionResult> AmdSoftwareFailureAsync(IAmdSoftwareInstaller sw, AmdSoftwareException e, CancellationToken ct)
        {
            _log.Error("InstallAmdSoftware failed", e);
            bool cancelled = e.InnerException is OperationCanceledException;
            var codes = e.InstalledProductCodes;
            var msg = (cancelled ? "AMD Software installation was cancelled: " : "AMD Software installation failed: ") + e.Message +
                      (_verificationPending
                          ? " The converted display driver itself is installed but not verified yet: restart Windows, then check Device Manager."
                          : " The converted display driver itself is installed and working.");
            if (codes.Count == 0)
                return Failed(msg + $" Nothing from AMD Software was installed by this run; Lenovo's Store app \"AMD Radeon Software\" may have been removed " +
                          $"(reinstall it from the Microsoft Store if needed). Log: {_log.Path}");

            _manifest = Manifest with { AmdSoftwareProductCodes = codes.ToList() };
            SaveManifest();
            bool yes = false;
            if (!ct.IsCancellationRequested)
            {
                try
                {
                    yes = await _prompt.ConfirmAsync("AMD Software installation failed",
                        msg + $"\r\n\r\nThis run installed part of it ({Join(codes)}). Remove those parts now? The display driver is not touched.", ct).ConfigureAwait(false);
                }
                catch (Exception pe) { _log.Error("The uninstall question failed; treating it as \"no\"", pe); }
            }
            if (!yes)
                return Failed(msg + $" The partly installed AMD Software ({Join(codes)}) was kept. To remove only it, uninstall \"AMD Software: Adrenalin Edition\" " +
                              $"in Settings > Apps; the app's Rollback (session manifest {_manifestPath}) removes it together with the converted driver. Log: {_log.Path}");
            try
            {
                await sw.UninstallAsync(codes, CancellationToken.None).ConfigureAwait(false);
                _manifest = Manifest with { AmdSoftwareProductCodes = Array.Empty<string>() };
                SaveManifest();
                return Failed(msg + $" The parts this run installed were removed again. Log: {_log.Path}");
            }
            catch (Exception ue)
            {
                _log.Error("Removing the partial AMD Software install failed", ue);
                return Failed(msg + $" Removing the partial install failed: {ue.Message} Uninstall \"AMD Software: Adrenalin Edition\" in Settings > Apps, " +
                              $"or use the app's Rollback (session manifest {_manifestPath}), which also removes the converted driver. Log: {_log.Path}");
            }
        }

        // ------------------------------------------------------------------ export

        async Task ExportAsync(CancellationToken ct)
        {
            Enter(Stage.Export, "Exporting the converted package");
            var export = Path.GetFullPath(_r.ExportFolder!);
            try
            {
                if (Directory.Exists(export) && Directory.EnumerateFileSystemEntries(export).Any())
                    throw new IOException($"The export folder {export} is not empty.");
                Directory.CreateDirectory(export);
                await TreeCopy.CopyAsync(_convertedDir, Path.Combine(export, Path.GetFileName(_convertedDir)), f => Report("Copying", f), ct).ConfigureAwait(false);
                File.WriteAllBytes(Path.Combine(export, CertificateFileName), Cert.Export(X509ContentType.Cert));
                // Export only: the exported manifest is informational (no Lenovo driver path, nothing installed). It is never
                // written into the session folder, where it would be the newest manifest and hide the last install's one.
                var m = _manifest ?? new SessionManifest(Amd.DriverVersion.ToString(), Cert.Thumbprint, Cert.Subject, Analysis.Packages, "",
                    Array.Empty<string>(), Array.Empty<string>());
                m.Save(Path.Combine(export, ManifestFileName));
                _log.Info($"Exported the converted package, the public certificate and the manifest to {export}.");
            }
            catch (Exception e) when (_r.InstallDriver && e is not OperationCanceledException)
            {
                throw new IOException("The driver was installed successfully; only the export failed: " + e.Message, e);
            }
        }

        static bool Eq(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static bool SameVersion(string? reported, Version want) =>
            reported != null && Version.TryParse(reported.Trim(), out var v) && DriverStoreParser.Normalize(v) == DriverStoreParser.Normalize(want);

        static string Join(IReadOnlyList<string> xs) => xs.Count == 0 ? "none" : string.Join(", ", xs);
    }
}
