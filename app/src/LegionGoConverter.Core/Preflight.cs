using System.Management;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LegionGoConverter.Core;

/// <summary>Boot-time security state, read only (the app never changes any of it).</summary>
public sealed record SecurityState(bool SecureBoot, bool Hvci, bool TestSigning);

public interface IEnvironmentProbe
{
    bool IsAdministrator();
    /// <summary>Free bytes available to the current user on the drive holding <paramref name="path"/>.</summary>
    long FreeBytes(string path);
    SecurityState GetSecurityState();
}

/// <summary>Read-only queries of the real machine: token elevation, drive free space, Secure Boot / HVCI / test signing
/// (registry and WMI reads only; nothing is ever written).</summary>
public sealed class WindowsEnvironmentProbe : IEnvironmentProbe
{
    public bool IsAdministrator()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public long FreeBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) throw new IOException($"Cannot determine the drive of {path}.");
        return new DriveInfo(root).AvailableFreeSpace;
    }

    public SecurityState GetSecurityState() => new(ReadSecureBoot(), ReadHvciRunning(), ReadTestSigning());

    static bool ReadSecureBoot()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
        return k?.GetValue("UEFISecureBootEnabled") is int v && v == 1;
    }

    static bool ReadHvciRunning()
    {
        try
        {
            using var s = new ManagementObjectSearcher(@"root\Microsoft\Windows\DeviceGuard", "SELECT SecurityServicesRunning FROM Win32_DeviceGuard");
            using var results = s.Get();
            foreach (ManagementObject o in results)
                using (o)
                    if (o["SecurityServicesRunning"] is uint[] running) return running.Contains(2u); // 2 = HVCI
            return false;
        }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            // Fall back to the configured (not necessarily running) state.
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity");
            return k?.GetValue("Enabled") is int v && v == 1;
        }
    }

    static bool ReadTestSigning()
    {
        using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control");
        return k?.GetValue("SystemStartOptions") is string s && s.Contains("TESTSIGNING", StringComparison.OrdinalIgnoreCase);
    }
}

public enum InstallerKind { Lenovo, Amd }

/// <summary>Identifies an installer by its content (never by its file name).</summary>
public interface IInstallerInspector
{
    Task<bool> IsAsync(string installer, InstallerKind kind, CancellationToken ct);
}

/// <summary>Lists the installer's content without extracting it: the Lenovo package is an Inno Setup installer
/// (<c>innoextract --list</c>), the AMD package a 7-Zip self-extractor (<c>7z l</c>). Either is accepted only when the
/// listing contains a display driver INF under <c>Packages\Drivers\Display\</c>; 7-Zip opens any .exe as a PE file,
/// so a successful listing alone proves nothing.</summary>
public sealed class ListingInstallerInspector(IProcessRunner runner, ToolPaths tools) : IInstallerInspector
{
    static readonly Regex DisplayInf = new(@"(^|[\\/\s])Packages[\\/]Drivers[\\/]Display[\\/][^\r\n]*\.inf\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task<bool> IsAsync(string installer, InstallerKind kind, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installer);
        var (exe, args) = kind == InstallerKind.Lenovo
            ? (tools.InnoExtract, new[] { "--list", "--silent", "--", installer })
            : (tools.SevenZip, new[] { "l", "-ba", "--", installer });
        var lines = new LineCollector();
        ProcessResult r;
        try { r = await runner.RunAsync(exe, args, lines, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            throw new ToolMissingException($"Could not run {Path.GetFileName(exe)} ({exe}) to inspect {installer}: {e.Message}");
        }
        return r.ExitCode == 0 && lines.Text.Split('\n').Any(l => DisplayInf.IsMatch(l.TrimEnd('\r')));
    }
}

public sealed class PreflightException : Exception
{
    public IReadOnlyList<string> Problems { get; }
    public PreflightException(IReadOnlyList<string> problems)
        : base(problems.Count == 1 ? problems[0] : "Several problems were found:\n- " + string.Join("\n- ", problems))
    { Problems = problems; }
}

/// <summary>Read-only checks before anything is extracted. Collects every problem and throws one
/// <see cref="PreflightException"/>; returns the (logged) security state.</summary>
internal static class Preflight
{
    public const long Gb = 1L << 30;
    public const long WorkRootBytes = 12 * Gb;
    /// <summary>Retained Lenovo copy (1.6 GB) + driver store copy of the converted package (2.2 GB) + AMD Software.</summary>
    public const long SystemDataBytes = 5 * Gb;
    public const long ExportBytes = 3 * Gb;

    /// <summary>Same pattern as the Lenovo extension INF IDs: an AMD Phoenix-family GPU with a Lenovo (17AA) subsystem.</summary>
    static readonly Regex LegionGoLike = new(@"^PCI\\VEN_1002&DEV_15[0-9A-F]{2}&SUBSYS_[0-9A-F]{4}17AA&REV_[0-9A-F]{2}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Returns the security state, or null when it could not be read (shown as "unknown", never as "off").</summary>
    public static async Task<SecurityState?> RunAsync(ConversionRequest r, PipelineServices s, ISessionLog log, CancellationToken ct)
    {
        var problems = new List<string>();
        var env = s.EnvironmentProbe;

        // ---- request
        if (!r.InstallDriver && r.ExportFolder is null)
            problems.Add("Nothing to do: choose to install the converted driver, to export it to a folder, or both.");
        if (r.InstallAmdSoftware && !r.InstallDriver)
            problems.Add("AMD Software can only be installed together with the converted driver.");

        // ---- administrator
        if (!env.IsAdministrator())
            problems.Add("The app must run as administrator (it trusts a certificate, installs a driver and creates a restore point).");
        else log.Info("Preflight: running as administrator.");

        // ---- folders
        var work = CheckFolder(r.WorkRoot, "work folder", requireAnsi: true, problems);
        string? export = null;
        if (r.ExportFolder is not null)
        {
            export = CheckFolder(r.ExportFolder, "export folder", requireAnsi: false, problems);
            if (export != null && File.Exists(export))
            { problems.Add($"The export folder {export} is a file."); export = null; }
            else if (export != null && Directory.Exists(export) && Directory.EnumerateFileSystemEntries(export).Any())
            { problems.Add($"The export folder {export} is not empty. Choose an empty or new folder."); export = null; }
        }

        // ---- free space (per drive, summed when several needs share a drive)
        if (work != null)
        {
            var need = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            void Need(string path, long bytes) { var root = Path.GetPathRoot(path)!; need[root] = need.GetValueOrDefault(root) + bytes; }
            Need(work, WorkRootBytes);
            if (r.InstallDriver)
            {
                // summed with the work folder when both are on the same drive
                Need(Path.GetFullPath(s.LenovoOriginalRoot), SystemDataBytes);
            }
            if (export != null) Need(export, ExportBytes);
            foreach (var (root, bytes) in need)
            {
                long free;
                try { free = env.FreeBytes(root); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
                { problems.Add($"Could not read the free disk space on {root}: {e.Message}"); continue; }
                log.Info($"Preflight: {root} has {free / (double)Gb:0.0} GB free, {bytes / Gb} GB needed.");
                if (free < bytes)
                    problems.Add($"Not enough free disk space on {root}: {free / (double)Gb:0.0} GB free, {bytes / Gb} GB needed.");
            }
        }

        // ---- installers, identified by content
        if (string.IsNullOrWhiteSpace(r.LenovoInstaller) || string.IsNullOrWhiteSpace(r.AmdInstaller))
            problems.Add("Both installers must be selected: Lenovo's Legion Go graphics driver and AMD's driver.");
        else if (string.Equals(Path.GetFullPath(r.LenovoInstaller), Path.GetFullPath(r.AmdInstaller), StringComparison.OrdinalIgnoreCase))
            problems.Add("The same file was selected for both installers.");
        else
        {
            await IdentifyAsync(s.Inspector, r.LenovoInstaller, InstallerKind.Lenovo, problems, log, ct).ConfigureAwait(false);
            await IdentifyAsync(s.Inspector, r.AmdInstaller, InstallerKind.Amd, problems, log, ct).ConfigureAwait(false);
        }

        // ---- device (exact match against the Lenovo package happens in Analyze)
        try
        {
            var devices = s.DeviceProbe.GetAmdDisplayDevices();
            var hit = devices.FirstOrDefault(d => d.HardwareIds.Any(h => LegionGoLike.IsMatch(h)));
            if (hit == null)
                problems.Add("This PC does not look like a Lenovo Legion Go: no AMD GPU with a Lenovo (17AA) subsystem ID was found. Found: " +
                             (devices.Count == 0 ? "no AMD display device" : string.Join("; ", devices.Select(d => d.HardwareIds.FirstOrDefault() ?? d.InstanceId))));
            else
                log.Info($"Preflight: GPU {hit.InstanceId}, driver {hit.DriverVersion ?? "unknown"} ({hit.DriverInf ?? "?"}), problem code {hit.ProblemCode}.");
        }
        catch (DeviceProbeException e) { problems.Add(e.Message); }

        // ---- security state (read only, logged)
        SecurityState? sec;
        try { sec = env.GetSecurityState(); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Warn("Preflight: could not read Secure Boot / HVCI / test signing state: " + e.Message);
            sec = null;
        }
        log.Info("Preflight: " + Describe(sec) + " (read only; the app never changes these).");
        if (sec is { TestSigning: true }) log.Warn("Preflight: test signing is on. The app does not need it and does not change it.");

        if (problems.Count > 0)
        {
            foreach (var p in problems) log.Error("Preflight: " + p);
            throw new PreflightException(problems);
        }
        log.Info("Preflight: all checks passed.");
        return sec;
    }

    public static string Describe(SecurityState? s) => s is null
        ? "Secure Boot: unknown, Memory Integrity (HVCI): unknown, Test signing: unknown (the state could not be read)"
        : $"Secure Boot: {OnOff(s.SecureBoot)}, Memory Integrity (HVCI): {OnOff(s.Hvci)}, Test signing: {OnOff(s.TestSigning)}";

    static string OnOff(bool b) => b ? "on" : "off";

    static async Task IdentifyAsync(IInstallerInspector insp, string path, InstallerKind kind, List<string> problems, ISessionLog log, CancellationToken ct)
    {
        string label = Label(kind), other = Label(kind == InstallerKind.Lenovo ? InstallerKind.Amd : InstallerKind.Lenovo);
        if (!File.Exists(path)) { problems.Add($"The {label} file was not found: {path}"); return; }
        try
        {
            if (await insp.IsAsync(path, kind, ct).ConfigureAwait(false))
            {
                log.Info($"Preflight: {path} is the {label}.");
                return;
            }
            if (await insp.IsAsync(path, kind == InstallerKind.Lenovo ? InstallerKind.Amd : InstallerKind.Lenovo, ct).ConfigureAwait(false))
                problems.Add($"The file chosen as the {label} ({path}) is the {other}. The two files appear to be swapped.");
            else
                problems.Add($"The file chosen as the {label} ({path}) is not the {label}: " + (kind == InstallerKind.Lenovo
                    ? "it is not an Inno Setup installer containing a Packages\\Drivers\\Display driver."
                    : "it is not an AMD self-extracting package containing a Packages\\Drivers\\Display driver."));
        }
        catch (ToolMissingException e) { problems.Add(e.Message); }
    }

    static string Label(InstallerKind k) => k == InstallerKind.Lenovo ? "Lenovo driver installer" : "AMD driver installer";

    /// <summary>Returns the normalized full path, or null after adding a problem.</summary>
    static string? CheckFolder(string? path, string what, bool requireAnsi, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(path)) { problems.Add($"No {what} was given."); return null; }
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        { problems.Add($"The {what} must be on a local drive, not a network path: {path}"); return null; }
        if (!Path.IsPathFullyQualified(path) || !Regex.IsMatch(path, @"^[A-Za-z]:\\"))
        { problems.Add($"The {what} must be a full local path such as C:\\ProgramData\\LegionGoDriverConverter\\work: {path}"); return null; }
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        { problems.Add($"The {what} path is not valid ({path}): {e.Message}"); return null; }
        if (requireAnsi)
        {
            try { CdfWriter.AnsiEncoding.GetBytes(full); }
            catch (EncoderFallbackException)
            {
                problems.Add($"The {what} path {full} contains characters the system ANSI code page cannot represent, and the catalog tool " +
                             "(makecat) reads its input in that code page. Choose a folder with plain letters, for example C:\\ProgramData\\LegionGoDriverConverter\\work.");
                return null;
            }
        }
        return full;
    }
}
