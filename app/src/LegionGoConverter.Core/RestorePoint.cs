using System.Globalization;
using System.Text.RegularExpressions;

namespace LegionGoConverter.Core;

public enum RestorePointOutcome { Created, RecentOneExists }

public interface IRestorePoint
{
    Task<RestorePointOutcome> CreateAsync(string description, CancellationToken ct);
}

public class RestorePointException : Exception
{
    public RestorePointException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>System Restore through Windows PowerShell. Language-independent: instead of matching the localized
/// "a restore point was created in the past 1440 minutes" warning, it lists existing restore points (creation
/// times converted to ISO-8601 UTC) and returns <see cref="RestorePointOutcome.RecentOneExists"/> when one is
/// younger than 24 hours. Otherwise it enables System Restore on the system drive, runs Checkpoint-Computer and
/// verifies that a new point really exists. Requires admin.</summary>
public sealed class SystemRestorePoint : IRestorePoint
{
    static readonly TimeSpan Window = TimeSpan.FromHours(24);
    static readonly Regex SafeDescription = new(@"^[A-Za-z0-9 ._,()+:\-]{1,200}\z", RegexOptions.CultureInvariant);
    static readonly Regex DriveRoot = new(@"^[A-Za-z]:\\\z", RegexOptions.CultureInvariant);
    const string QueryScript =
        "$ErrorActionPreference = 'Stop'; Get-ComputerRestorePoint | ForEach-Object { 'RP ' + [Management.ManagementDateTimeConverter]::ToDateTime($_.CreationTime).ToUniversalTime().ToString('o') }";

    readonly IProcessRunner _runner;
    readonly string _powershell;
    readonly string _systemDrive;
    readonly TimeProvider _clock;
    readonly ISessionLog _log;

    public SystemRestorePoint(IProcessRunner runner, ISessionLog log)
        : this(runner, SystemTools.PowerShell, Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\", TimeProvider.System, log) { }

    public SystemRestorePoint(IProcessRunner runner, string powershellPath, string systemDriveRoot, TimeProvider clock, ISessionLog log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        ArgumentException.ThrowIfNullOrWhiteSpace(powershellPath);
        if (systemDriveRoot is null || !DriveRoot.IsMatch(systemDriveRoot))
            throw new ArgumentException($"System drive root must look like 'C:\\', got '{systemDriveRoot}'.", nameof(systemDriveRoot));
        _powershell = powershellPath;
        _systemDrive = systemDriveRoot;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<RestorePointOutcome> CreateAsync(string description, CancellationToken ct)
    {
        if (description is null || !SafeDescription.IsMatch(description))
            throw new ArgumentException(
                "Restore point description must be 1-200 characters of letters, digits, spaces and . _ , ( ) + : -", nameof(description));

        _log.Info("Restore point: checking for a restore point created in the last 24 hours.");
        bool recent;
        try { recent = await HasRecentAsync(ct).ConfigureAwait(false); }
        catch (RestorePointException e)
        {
            // With System Protection off, Get-ComputerRestorePoint can fail (unverified on the device). Not a reason to stop:
            // enable System Restore, create the point, and let the verification below decide.
            _log.Warn("Restore point: the check for a recent restore point failed; enabling System Restore and creating one anyway. " + e.Message);
            recent = false;
        }
        if (recent)
        {
            _log.Info("Restore point: a recent restore point exists; Windows allows only one per 24 hours, so it is used instead of creating a new one.");
            return RestorePointOutcome.RecentOneExists;
        }

        ct.ThrowIfCancellationRequested();
        _log.Info($"Restore point: enabling System Restore on {_systemDrive} and creating restore point '{description}'.");
        var create = $"$ErrorActionPreference = 'Stop'; Enable-ComputerRestore -Drive '{_systemDrive}'; " +
                     $"Checkpoint-Computer -Description '{description}' -RestorePointType MODIFY_SETTINGS";
        // Not cancellable once started: never kill the restore-point service call half-way.
        var created = await RunAsync(create, null, CancellationToken.None, "Checkpoint-Computer").ConfigureAwait(false);
        _log.Info("Restore point: Checkpoint-Computer output:\n" + created.Output);

        // Checkpoint-Computer only warns (exit 0) when Windows declines to create a point, so verify.
        if (!await HasRecentAsync(CancellationToken.None).ConfigureAwait(false))
        {
            _log.Error("Restore point: Checkpoint-Computer finished but no new restore point was found.");
            throw new RestorePointException(
                "Checkpoint-Computer finished but no new restore point was found. System Protection may be disabled by policy; " +
                "create a restore point manually (System Properties > System Protection) before continuing.");
        }
        _log.Info("Restore point: created and verified.");
        return RestorePointOutcome.Created;
    }

    async Task<bool> HasRecentAsync(CancellationToken ct)
    {
        var lines = new LineCollector();
        var r = await RunAsync(QueryScript, lines, ct, "Get-ComputerRestorePoint").ConfigureAwait(false);
        _log.Info("Restore point: Get-ComputerRestorePoint output:\n" + lines.Text);
        var now = _clock.GetUtcNow().UtcDateTime;
        bool recent = false;
        foreach (var raw in lines.Text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("RP ", StringComparison.Ordinal)) continue;
            if (!DateTime.TryParse(line[3..], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
                || t.Kind != DateTimeKind.Utc)
                throw new RestorePointException($"Unexpected restore point timestamp from PowerShell: '{line}'.\n{r.Output}");
            if (now - t < Window) recent = true; // also true for a point slightly in the future (clock skew)
        }
        return recent;
    }

    async Task<ProcessResult> RunAsync(string script, IProgress<string>? lines, CancellationToken ct, string what)
    {
        ProcessResult r;
        try
        {
            r = await _runner.RunAsync(_powershell,
                new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }, lines, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) { throw new RestorePointException($"Could not run PowerShell ({_powershell}) for {what}: {e.Message}", e); }
        if (r.ExitCode != 0)
        {
            _log.Error($"Restore point: {what} failed (exit {r.ExitCode}):\n{r.Output}");
            throw new RestorePointException($"{what} failed (exit {r.ExitCode}). Administrator rights are required.\n{r.Output}");
        }
        return r;
    }
}
