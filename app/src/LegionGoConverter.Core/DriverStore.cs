using System.Text.RegularExpressions;

namespace LegionGoConverter.Core;

public class DriverStoreException : Exception
{
    public DriverStoreException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>A driver package in the Windows driver store, as listed by <c>pnputil /enum-drivers</c>.
/// <see cref="ClassName"/> is the INF class (e.g. <c>Display</c>) when pnputil reports it.</summary>
public sealed record StoredDriver(string Published, string Original, Version Version, string Signer)
{
    public string? ClassName { get; init; }
}

public static class DriverStoreParser
{
    /// <summary>Parses English <c>pnputil /enum-drivers</c> text. Blocks are separated by blank lines; a block is
    /// kept only when it has a published name, an original name and a parsable driver version (the version is the
    /// last token of <c>Driver Version:</c>). Anything else (the tool banner, unknown blocks) is skipped.</summary>
    public static IReadOnlyList<StoredDriver> Parse(string pnputilEnumDriversOutput)
    {
        ArgumentNullException.ThrowIfNull(pnputilEnumDriversOutput);
        var result = new List<StoredDriver>();
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (fields.TryGetValue("Published Name", out var pub) && pub.Length > 0
                && fields.TryGetValue("Original Name", out var orig) && orig.Length > 0
                && fields.TryGetValue("Driver Version", out var dv)
                && TryParseVersion(dv, out var ver))
            {
                fields.TryGetValue("Signer Name", out var signer);
                fields.TryGetValue("Class Name", out var cls);
                result.Add(new StoredDriver(pub, orig, ver, signer ?? "") { ClassName = string.IsNullOrEmpty(cls) ? null : cls });
            }
            fields.Clear();
        }

        foreach (var raw in pnputilEnumDriversOutput.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) { Flush(); continue; }
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            if (!fields.ContainsKey(key)) fields[key] = line[(colon + 1)..].Trim();
        }
        Flush();
        return result;
    }

    static bool TryParseVersion(string driverVersionField, out Version version)
    {
        version = null!;
        var parts = driverVersionField.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return false;
        if (!Version.TryParse(parts[^1], out var v)) return false;
        version = v;
        return true;
    }

    /// <summary>Pads missing build/revision with 0 so <c>1.2</c> equals <c>1.2.0.0</c>.</summary>
    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

/// <summary>Outcome of a successful pnputil mutation. <see cref="RebootRequired"/> is set for exit code 3010.</summary>
public sealed record DriverOpResult(bool RebootRequired, string Output);

public interface IDriverInstaller
{
    Task<DriverOpResult> AddAndInstallAsync(string infGlobRoot, CancellationToken ct);
    /// <summary>Adds one INF's package to the driver store only (no <c>/install</c>): the active driver does not change.</summary>
    Task<DriverOpResult> StageAsync(string infPath, CancellationToken ct);
    Task<DriverOpResult> DeleteAsync(string publishedName, CancellationToken ct);
    Task<IReadOnlyList<StoredDriver>> ListAsync(CancellationToken ct);
}

/// <summary>Collects every streamed output line (the runner's returned Output is only a bounded tail).</summary>
internal sealed class LineCollector : IProgress<string>
{
    readonly List<string> _lines = new();
    public void Report(string value) { lock (_lines) _lines.Add(value); }
    public string Text { get { lock (_lines) return string.Join("\n", _lines); } }
}

internal static class SystemTools
{
    /// <summary>The real System32 folder, also from a 32-bit process on 64-bit Windows (where System32 is redirected).</summary>
    public static string NativeSystemDir()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return Environment.Is64BitOperatingSystem && !Environment.Is64BitProcess
            ? Path.Combine(win, "Sysnative")
            : Path.Combine(win, "System32");
    }

    public static string PnpUtil => Path.Combine(NativeSystemDir(), "pnputil.exe");
    public static string PowerShell => Path.Combine(NativeSystemDir(), "WindowsPowerShell", "v1.0", "powershell.exe");
}

/// <summary><see cref="IDriverInstaller"/> over pnputil. Requires admin for add/stage/delete. Mutating calls honour
/// cancellation only before pnputil starts: a driver-store change is never killed half-way. The output of every
/// successful mutation is written to the session log.</summary>
public sealed class PnpUtilDriverInstaller : IDriverInstaller
{
    const int RebootRequiredExit = 3010; // ERROR_SUCCESS_REBOOT_REQUIRED: the operation succeeded
    static readonly Regex PublishedName = new(@"^oem\d+\.inf\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    readonly IProcessRunner _runner;
    readonly string _pnputil;
    readonly ISessionLog? _log;

    public PnpUtilDriverInstaller(IProcessRunner runner, ISessionLog? log = null) : this(runner, SystemTools.PnpUtil, log) { }

    public PnpUtilDriverInstaller(IProcessRunner runner, string pnputilPath, ISessionLog? log = null)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentException.ThrowIfNullOrWhiteSpace(pnputilPath);
        _pnputil = pnputilPath;
        _log = log;
    }

    public Task<DriverOpResult> AddAndInstallAsync(string infGlobRoot, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(infGlobRoot);
        if (!Directory.Exists(infGlobRoot)) throw new DriverStoreException($"Driver package folder not found: {infGlobRoot}");
        ct.ThrowIfCancellationRequested();
        var root = infGlobRoot.TrimEnd('\\', '/');
        return Mutate(new[] { "/add-driver", root + @"\*.inf", "/subdirs", "/install" }, "add-driver " + root, allowNoMoreItems: true);
    }

    public Task<DriverOpResult> StageAsync(string infPath, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(infPath);
        if (!string.Equals(Path.GetExtension(infPath), ".inf", StringComparison.OrdinalIgnoreCase) || !File.Exists(infPath))
            throw new DriverStoreException($"Driver INF not found: {infPath}");
        ct.ThrowIfCancellationRequested();
        return Mutate(new[] { "/add-driver", infPath }, "add-driver (stage) " + infPath);
    }

    public Task<DriverOpResult> DeleteAsync(string publishedName, CancellationToken ct)
    {
        if (publishedName is null || !PublishedName.IsMatch(publishedName))
            throw new ArgumentException($"Only published driver-store names (oemNN.inf) may be deleted, got '{publishedName}'.", nameof(publishedName));
        ct.ThrowIfCancellationRequested();
        return Mutate(new[] { "/delete-driver", publishedName, "/uninstall" }, "delete-driver " + publishedName);
    }

    public async Task<IReadOnlyList<StoredDriver>> ListAsync(CancellationToken ct)
    {
        var lines = new LineCollector();
        await RunChecked(new[] { "/enum-drivers" }, lines, ct, "enum-drivers").ConfigureAwait(false);
        var text = lines.Text;
        var list = DriverStoreParser.Parse(text);
        if (list.Count == 0)
            throw new DriverStoreException(
                "pnputil /enum-drivers output was not recognized (no driver packages parsed). " +
                "The parser expects English pnputil output. Output:\n" + Truncate(text));
        return list;
    }

    /// <summary>ERROR_NO_MORE_ITEMS: packages added, but no device was updated. Accepted for add-and-install only
    /// (unverified on the device); the verification after the install decides whether the GPU runs the new driver.</summary>
    const int NoMoreItemsExit = 259;

    async Task<DriverOpResult> Mutate(string[] args, string what, bool allowNoMoreItems = false)
    {
        // Not cancellable once started: a driver-store change is never killed half-way.
        var r = await RunChecked(args, null, CancellationToken.None, what, allowNoMoreItems).ConfigureAwait(false);
        if (r.ExitCode == NoMoreItemsExit)
        {
            _log?.Warn($"pnputil {what} returned exit 259 (ERROR_NO_MORE_ITEMS: driver packages added, but pnputil reports that no device was updated). " +
                       $"Treated as added; the verification step decides whether the GPU runs the new driver. Output:\n{r.Output}");
            return new DriverOpResult(false, r.Output);
        }
        var reboot = r.ExitCode == RebootRequiredExit;
        _log?.Info($"pnputil {what} succeeded (exit {r.ExitCode}{(reboot ? ", reboot required" : "")}):\n{r.Output}");
        return new DriverOpResult(reboot, r.Output);
    }

    async Task<ProcessResult> RunChecked(string[] args, IProgress<string>? lines, CancellationToken ct, string what, bool allowNoMoreItems = false)
    {
        ProcessResult r;
        try { r = await _runner.RunAsync(_pnputil, args, lines, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { throw new DriverStoreException($"Could not run pnputil ({_pnputil}) for {what}: {e.Message}", e); }
        if (r.ExitCode != 0 && r.ExitCode != RebootRequiredExit && !(allowNoMoreItems && r.ExitCode == NoMoreItemsExit))
            throw new DriverStoreException($"pnputil {what} failed (exit {r.ExitCode}):\n{r.Output}");
        return r;
    }

    static string Truncate(string s) => s.Length <= 2000 ? s : s[..2000] + "...";
}
