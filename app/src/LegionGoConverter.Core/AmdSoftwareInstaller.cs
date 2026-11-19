using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace LegionGoConverter.Core;

public sealed class AmdSoftwareException : Exception
{
    /// <summary>Product codes installed by THIS run before the failure; safe to pass to UninstallAsync (never contains pre-existing products).</summary>
    public IReadOnlyList<string> InstalledProductCodes { get; }
    /// <summary>Product codes that were already installed before this run; do NOT uninstall these on rollback.</summary>
    public IReadOnlyList<string> PreExistingProductCodes { get; }
    public AmdSoftwareException(string message, IReadOnlyList<string>? installed = null, IReadOnlyList<string>? preExisting = null, Exception? inner = null) : base(message, inner)
    {
        InstalledProductCodes = installed ?? Array.Empty<string>();
        PreExistingProductCodes = preExisting ?? Array.Empty<string>();
    }
}

/// <summary>Answers whether an MSI product (by ProductCode) is registered as installed, and which installed products share an UpgradeCode.</summary>
public interface IMsiProductQuery
{
    bool IsInstalled(string productCode);
    /// <summary>Installed products with this UpgradeCode (other versions of the same product), with their version strings.</summary>
    IReadOnlyList<(string ProductCode, string Version)> RelatedProducts(string upgradeCode);
}

/// <summary>Registry-backed query (HKLM Uninstall key, 64-bit and WOW6432Node views); related products via msi.dll.</summary>
public sealed class RegistryMsiProductQuery : IMsiProductQuery
{
    const uint ErrorSuccess = 0, ErrorMoreData = 234;

    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true),
     System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    static extern uint MsiEnumRelatedProductsW(string upgradeCode, uint reserved, uint index, StringBuilder productBuf);

    [System.Runtime.InteropServices.DllImport("msi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, ExactSpelling = true),
     System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    static extern uint MsiGetProductInfoW(string product, string property, StringBuilder? value, ref uint size);

    public IReadOnlyList<(string ProductCode, string Version)> RelatedProducts(string upgradeCode)
    {
        var result = new List<(string, string)>();
        if (!AmdSoftwareInstaller.IsProductCode(upgradeCode)) return result;
        for (uint i = 0; ; i++)
        {
            var buf = new StringBuilder(39);
            if (MsiEnumRelatedProductsW(upgradeCode, 0, i, buf) != ErrorSuccess) break; // 259 = no more items
            var code = buf.ToString();
            uint size = 0;
            var rc = MsiGetProductInfoW(code, "VersionString", null, ref size);
            var version = "";
            if (rc is ErrorSuccess or ErrorMoreData)
            {
                var v = new StringBuilder((int)size + 1);
                size = (uint)v.Capacity;
                if (MsiGetProductInfoW(code, "VersionString", v, ref size) == ErrorSuccess) version = v.ToString();
            }
            result.Add((code, version));
        }
        return result;
    }

    public bool IsInstalled(string productCode)
    {
        using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var root in new[]
                 {
                     @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                     @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
                 })
        {
            using var k = hklm.OpenSubKey(root + "\\" + productCode);
            if (k != null) return true;
        }
        return false;
    }
}

/// <summary>Removes AMD Software products by ProductCode with the system msiexec only (no bundled tools), so rollback can
/// use it. Each msiexec log goes into the session folder (the directory of the session log), never the user's %TEMP%.</summary>
public sealed class AmdSoftwareUninstaller : IAmdSoftwareUninstaller
{
    readonly IProcessRunner _runner;
    readonly ISessionLog _log;

    public AmdSoftwareUninstaller(IProcessRunner runner, ISessionLog log)
    {
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    static string MsiexecExe => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    /// <summary>Uninstalls the given product codes (in reverse order). Not-installed (1605) counts as success. msiexec always
    /// runs to completion; <paramref name="ct"/> is only checked between products.</summary>
    public async Task UninstallAsync(IReadOnlyList<string> productCodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(productCodes);
        foreach (var c in productCodes)
            if (c is null || !AmdSoftwareInstaller.IsProductCode(c)) throw new ArgumentException($"Not a product code: '{c}'", nameof(productCodes));
        var logDir = Path.IsPathFullyQualified(_log.Path) ? Path.GetDirectoryName(_log.Path) : null;
        foreach (var code in productCodes.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            var args = new List<string> { "/x", code, "/qn", "/norestart", "REBOOT=ReallySuppress" };
            string? msiLog = null;
            if (logDir is not null)
            {
                msiLog = Path.Combine(logDir, "msi-uninstall-" + code.Trim('{', '}') + ".log");
                args.AddRange(new[] { "/L*v", msiLog });
            }
            else _log.Warn($"msiexec /x {code}: no session folder known, so msiexec writes no log of its own.");
            var r = await _runner.RunAsync(MsiexecExe, args, null, CancellationToken.None).ConfigureAwait(false);
            _log.Info($"msiexec /x {code}: exit {r.ExitCode}" + (msiLog is null ? "" : $" (log: {msiLog})"));
            if (r.ExitCode is not (0 or 1605 or 1641 or 3010))
                throw new AmdSoftwareException($"Uninstall of {code} failed with exit code {r.ExitCode}." + (msiLog is null ? "" : $" See {msiLog}"));
        }
    }
}

/// <summary>ProductCodes = everything now installed; InstalledByThisRun / AlreadyPresent partition it.</summary>
public sealed record AmdSoftwareInstallResult(IReadOnlyList<string> ProductCodes, IReadOnlyList<string> InstalledByThisRun, IReadOnlyList<string> AlreadyPresent, bool RebootRequired);

/// <summary>
/// Replaces Lenovo's Store "AMD Radeon Software" with the AMD Software (Adrenalin) MSIs inside ccc2_install.exe,
/// installed directly with msiexec. Performs the steps only; confirmation is the caller's job.
/// </summary>
public sealed class AmdSoftwareInstaller : IAmdSoftwareInstaller
{
    public const string StoreAppName = "AdvancedMicroDevicesInc-2.AMDRadeonSoftware";
    const string CnextMsi = @"CN\cnext\cnext64\ccc-next64.msi";
    const string DvrMsi = @"CN\amddvr\amddvr64\amddvr64.msi";
    static readonly string[] ExtractPatterns = { @"CN\cnext\cnext64\*", @"CN\amddvr\amddvr64\*" };
    static readonly Regex ProductCodeRx = new(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$", RegexOptions.Compiled);

    readonly IProcessRunner _runner;
    readonly ToolPaths _tools;
    readonly ISessionLog _log;
    readonly IMsiProductQuery _query;
    readonly Func<string, MsiInfo> _readMsi;

    public AmdSoftwareInstaller(IProcessRunner runner, ToolPaths tools, ISessionLog log)
        : this(runner, tools, log, new RegistryMsiProductQuery(), MsiInfo.Read) { }

    internal AmdSoftwareInstaller(IProcessRunner runner, ToolPaths tools, ISessionLog log, IMsiProductQuery query, Func<string, MsiInfo> readMsi)
    {
        _runner = runner; _tools = tools; _log = log; _query = query; _readMsi = readMsi;
    }

    static string PowerShellExe => Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
    static string MsiexecExe => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    /// <summary>Installs both products and returns the product codes now installed (including ones already present).</summary>
    public async Task<IReadOnlyList<string>> InstallAsync(string ccc2Path, string workDir, CancellationToken ct)
        => (await InstallDetailedAsync(ccc2Path, workDir, ct).ConfigureAwait(false)).ProductCodes;

    /// <remarks>Cancellation contract: before the Store app is touched, a plain OperationCanceledException. Once it is removed, msiexec
    /// always runs to completion and cancellation is observed only between products, surfacing as an AmdSoftwareException whose
    /// InnerException is an OperationCanceledException and whose InstalledProductCodes lists what this run installed.</remarks>
    public async Task<AmdSoftwareInstallResult> InstallDetailedAsync(string ccc2Path, string workDir, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(ccc2Path)) throw new AmdSoftwareException($"AMD installer not found: {ccc2Path}");
        if (Directory.Exists(workDir) && Directory.EnumerateFileSystemEntries(workDir).Any())
            throw new AmdSoftwareException($"Work folder is not empty: {workDir}");

        // 1. extract only the two MSI folders
        try
        {
            await new SevenZipExtractor(_runner, _tools.SevenZip)
                .ExtractSelectedAsync(ccc2Path, workDir, ExtractPatterns, null, ct).ConfigureAwait(false);
        }
        catch (ExtractionException ex) { throw new AmdSoftwareException("Could not extract AMD Software: " + ex.Message); }

        var products = new List<(string Msi, MsiInfo Info, bool Dvr)>();
        foreach (var (rel, dvr) in new[] { (CnextMsi, false), (DvrMsi, true) })
        {
            var msi = Path.Combine(workDir, rel);
            if (!File.Exists(msi)) throw new AmdSoftwareException($"Expected MSI missing after extraction: {msi}");
            MsiInfo info;
            try { info = _readMsi(msi); }
            catch (IOException ex) { throw new AmdSoftwareException(ex.Message); }
            if (!ProductCodeRx.IsMatch(info.ProductCode)) throw new AmdSoftwareException($"{msi} has an invalid ProductCode '{info.ProductCode}'.");
            products.Add((msi, info, dvr));
        }

        // 2. every MSI must be validly AMD-signed before anything on the machine changes
        foreach (var p in products)
        {
            await RequireAmdSignatureAsync(p.Msi, ct).ConfigureAwait(false);
            _log.Info($"AMD signature OK: {p.Info.ProductName} {p.Info.ProductVersion} <- {p.Msi}");
        }

        ct.ThrowIfCancellationRequested(); // before anything on the machine changes: plain cancellation
        // 3. remove Lenovo's Store app; stop if it is still registered
        await RemoveStoreAppAsync(ct).ConfigureAwait(false);

        // 4. install
        var logDir = Path.Combine(workDir, "logs");
        Directory.CreateDirectory(logDir);
        var all = new List<string>();
        var installed = new List<string>();
        var preExisting = new List<string>();
        var reboot = false;
        foreach (var p in products)
        {
            // The Store app is already gone, so cancellation here is reported with what this run installed.
            if (ct.IsCancellationRequested)
                throw new AmdSoftwareException("Cancelled between products; the Store app was already removed. See InstalledProductCodes for what this run installed.", installed.ToArray(), preExisting.ToArray(), new OperationCanceledException(ct));
            var code = p.Info.ProductCode;
            if (_query.IsInstalled(code))
            {
                _log.Info($"{p.Info.ProductName} already installed, skipping");
                all.Add(code); preExisting.Add(code);
                continue;
            }
            // Another version of the same product (same UpgradeCode, different ProductCode). AMD's version numbers
            // (e.g. 2026.1109) overflow Windows Installer's 255 limit per field, so msiexec cannot tell older from newer and
            // refuses with "a newer version is already installed" (1603). Compare the versions ourselves: keep a newer one,
            // uninstall an older one first (as AMD's own setup does).
            var related = string.IsNullOrEmpty(p.Info.UpgradeCode)
                ? Array.Empty<(string ProductCode, string Version)>()
                : _query.RelatedProducts(p.Info.UpgradeCode).Where(x => !string.Equals(x.ProductCode, code, StringComparison.OrdinalIgnoreCase)).ToArray();
            var newer = related.Where(x => CompareVersions(x.Version, p.Info.ProductVersion) >= 0).ToArray();
            if (newer.Length > 0)
            {
                foreach (var n in newer)
                {
                    _log.Info($"{p.Info.ProductName} {n.Version} ({n.ProductCode}) is already installed and is not older than {p.Info.ProductVersion}; keeping it.");
                    all.Add(n.ProductCode); preExisting.Add(n.ProductCode);
                }
                continue;
            }
            foreach (var old in related)
            {
                _log.Info($"Removing the older {p.Info.ProductName} {old.Version} ({old.ProductCode}) before installing {p.Info.ProductVersion}.");
                try { await new AmdSoftwareUninstaller(_runner, _log).UninstallAsync(new[] { old.ProductCode }, CancellationToken.None).ConfigureAwait(false); }
                catch (AmdSoftwareException ex)
                {
                    throw new AmdSoftwareException($"Could not remove the older {p.Info.ProductName} {old.Version} ({old.ProductCode}): {ex.Message}", installed.ToArray(), preExisting.ToArray(), ex);
                }
            }
            var msiLog = Path.Combine(logDir, "msi-" + Regex.Replace(p.Info.ProductName, @"[^A-Za-z0-9]+", "-") + ".log");
            var args = new List<string> { "/i", p.Msi };
            if (p.Dvr) args.Add("LAUNCHED_FROM_CIM=1");
            args.AddRange(new[] { "/qn", "/norestart", "REBOOT=ReallySuppress", "/L*v", msiLog });
            _log.Info($"Installing {p.Info.ProductName} {p.Info.ProductVersion} (this can take a few minutes)");
            // msiexec runs to completion (CancellationToken.None): killing the client mid-install leaves the Windows Installer service in an unknown state.
            var r = await _runner.RunAsync(MsiexecExe, args, null, CancellationToken.None).ConfigureAwait(false);
            _log.Info($"{p.Info.ProductName}: msiexec exit {r.ExitCode} (log: {msiLog})");
            if (r.ExitCode is not (0 or 1641 or 3010))
                throw new AmdSoftwareException($"{p.Info.ProductName} install failed with exit code {r.ExitCode}. See {msiLog}", installed.ToArray(), preExisting.ToArray());
            if (r.ExitCode is 1641 or 3010) reboot = true;
            if (!_query.IsInstalled(code))
                throw new AmdSoftwareException($"{p.Info.ProductName} is not registered after install (exit {r.ExitCode}). See {msiLog}", installed.ToArray(), preExisting.ToArray());
            all.Add(code); installed.Add(code);
        }
        return new AmdSoftwareInstallResult(all, installed, preExisting, reboot);
    }

    /// <summary>Uninstalls the given product codes (see <see cref="AmdSoftwareUninstaller"/>).</summary>
    public Task UninstallAsync(IReadOnlyList<string> productCodes, CancellationToken ct) =>
        new AmdSoftwareUninstaller(_runner, _log).UninstallAsync(productCodes, ct);

    internal static bool IsProductCode(string code) => ProductCodeRx.IsMatch(code);

    /// <summary>Compares dotted version strings numerically field by field (missing fields count as 0, no 255 limit).
    /// An unparsable field counts as 0.</summary>
    internal static int CompareVersions(string a, string b)
    {
        var x = (a ?? "").Split('.'); var y = (b ?? "").Split('.');
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
        {
            long.TryParse(i < x.Length ? x[i] : "0", out var u);
            long.TryParse(i < y.Length ? y[i] : "0", out var v);
            if (u != v) return u.CompareTo(v);
        }
        return 0;
    }

    /// <summary>Quotes a value as a PowerShell single-quoted literal (doubles every quote-like character).</summary>
    static string PsQuote(string s)
    {
        var sb = new StringBuilder("'");
        foreach (var ch in s)
        {
            sb.Append(ch);
            if (ch is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(ch);
        }
        return sb.Append('\'').ToString();
    }

    Task<ProcessResult> RunPowerShellAsync(string script, CancellationToken ct) =>
        _runner.RunAsync(PowerShellExe, new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }, null, ct);

    async Task RequireAmdSignatureAsync(string msi, CancellationToken ct)
    {
        var script = $"$s = Get-AuthenticodeSignature -LiteralPath {PsQuote(msi)}; \"$($s.Status)|$($s.SignerCertificate.Subject)\"";
        var r = await RunPowerShellAsync(script, ct).ConfigureAwait(false);
        var line = r.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains('|')) ?? "";
        var i = line.IndexOf('|');
        var status = i < 0 ? "" : line[..i];
        var subject = i < 0 ? "" : line[(i + 1)..];
        const string Cn = "CN=Advanced Micro Devices";
        var ok = r.ExitCode == 0 && status == "Valid" && subject.StartsWith(Cn, StringComparison.Ordinal)
                 && (subject.Length == Cn.Length || subject[Cn.Length] == ',');
        if (!ok)
            throw new AmdSoftwareException($"{msi} is not validly signed by Advanced Micro Devices (status '{status}', signer '{subject}'). Nothing was changed.");
    }

    async Task RemoveStoreAppAsync(CancellationToken ct)
    {
        var name = PsQuote(StoreAppName);
        var remove =
            "$ErrorActionPreference = 'Stop'; " +
            $"$n = {name}; " +
            "foreach ($p in @(Get-AppxPackage -AllUsers -Name $n)) { try { Remove-AppxPackage -Package $p.PackageFullName -AllUsers } catch { Remove-AppxPackage -Package $p.PackageFullName } }; " +
            "foreach ($v in @(Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq $n)) { Remove-AppxProvisionedPackage -Online -PackageName $v.PackageName | Out-Null }";
        _log.Info($"Removing Store app {StoreAppName} (all users + provisioned)");
        var r = await RunPowerShellAsync(remove, ct).ConfigureAwait(false);
        if (r.ExitCode != 0)
            throw new AmdSoftwareException($"Removing the Store app failed (exit {r.ExitCode}): {r.Output}");

        var check =
            $"$n = {name}; " +
            "@(Get-AppxPackage -AllUsers -Name $n).Count + @(Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq $n).Count";
        var c = await RunPowerShellAsync(check, ct).ConfigureAwait(false);
        var last = c.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
        if (c.ExitCode != 0 || !int.TryParse(last, out var n))
            throw new AmdSoftwareException($"Could not verify the Store app removal (exit {c.ExitCode}): {c.Output}");
        if (n != 0)
            throw new AmdSoftwareException("Store app is still registered; stopping before installing AMD Software.");
        _log.Info("Store app removed (or was not present)");
    }
}
