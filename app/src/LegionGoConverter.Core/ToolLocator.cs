using System.Text.RegularExpressions;

namespace LegionGoConverter.Core;

public class ToolMissingException : Exception
{
    public ToolMissingException(string message) : base(message) { }
}

public sealed record ToolPaths(string SevenZip, string InnoExtract, string MakeCat, string SignTool);

/// <summary>Finds the external tools by absolute path.
/// <para>7z.exe and innoextract.exe come from the app's tools folder, by location: the trust boundary is that the installer puts
/// the app in Program Files (administrator-writable only); the startup warning about a user-writable app folder remains.</para>
/// <para>signtool.exe and makecat.exe come from the user's installed Windows SDK (Windows Kits\10\bin\&lt;version&gt;\x64 under Program
/// Files, administrator-writable only) in preference to a copy in tools\ (the fallback), and the chosen file must carry a valid
/// Authenticode signature by Microsoft Corporation (an EMBEDDED signature: a file that is only catalog-signed is refused). Microsoft does not allow makecat.exe to be redistributed.
/// Neither PATH nor the current directory is ever searched.</para></summary>
public static class ToolLocator
{
    public const string SdkWingetId = "Microsoft.WindowsSDK.10.0.28000";
    public const string SdkDownloadUrl = "https://developer.microsoft.com/windows/downloads/windows-sdk/";

    static readonly Regex VersionFolder = new(@"^\d+\.\d+\.\d+(\.\d+)?\z", RegexOptions.CultureInvariant);

    /// <summary>The default Windows Kits\10\bin folders: Program Files (x86) first, then Program Files.</summary>
    public static IReadOnlyList<string> DefaultSdkRoots()
    {
        var roots = new List<string>();
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            var r = Path.Combine(pf, "Windows Kits", "10", "bin");
            if (!roots.Contains(r, StringComparer.OrdinalIgnoreCase)) roots.Add(r);
        }
        return roots;
    }

    /// <param name="toolsDir">The bundled tools folder; default is "tools" beside the app.</param>
    /// <param name="sdkRoots">Windows Kits\10\bin folders to search; default is <see cref="DefaultSdkRoots"/>. Tests inject temp folders.</param>
    /// <param name="verifier">Authenticode check for signtool/makecat; default is <see cref="WinTrustMicrosoftVerifier"/>.</param>
    public static ToolPaths Resolve(string? toolsDir = null, IEnumerable<string>? sdkRoots = null, IAuthenticodeVerifier? verifier = null)
    {
        var dir = Path.GetFullPath(toolsDir ?? Path.Combine(AppContext.BaseDirectory, "tools"));
        var roots = (sdkRoots ?? DefaultSdkRoots()).ToList();
        verifier ??= new WinTrustMicrosoftVerifier();

        string Bundled(string name)
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p)) throw new ToolMissingException(
                $"Required tool {name} was not found in {dir}. Reinstall the app so its tools folder is complete.");
            return p;
        }
        string SdkOrBundled(string name)
        {
            var p = FindInSdk(name, roots);
            if (p is null)
            {
                var bundled = Path.Combine(dir, name);
                if (File.Exists(bundled)) p = bundled;
            }
            if (p is null) throw new ToolMissingException(SdkMissingMessage(name, dir, roots));
            string? problem;
            try { problem = verifier.Check(p); }
            catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                // Fail closed: a verifier that cannot run (missing wintrust entry point, bad path, access denied ...) must not crash the app at startup.
                problem = $"the signature check could not be run ({e.GetType().Name}: {e.Message})";
            }
            if (problem is not null) throw new ToolMissingException(
                $"{name} was found at {p} but is not trusted: {problem}. The app only runs {name} when it carries a valid embedded Microsoft signature (a catalog-signed-only file is refused). " +
                $"Reinstall the Windows SDK \"Signing Tools for Desktop Apps\" component ({SdkInstallHint}) and restart this app.");
            return p;
        }
        return new ToolPaths(Bundled("7z.exe"), Bundled("innoextract.exe"), SdkOrBundled("makecat.exe"), SdkOrBundled("signtool.exe"));
    }

    static string? FindInSdk(string name, IEnumerable<string> roots)
    {
        var candidates = new List<(Version Version, string Path)>();
        string? unversioned = null;
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            IEnumerable<string> subs;
            try { subs = Directory.GetDirectories(root); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sub in subs)
            {
                var leaf = Path.GetFileName(sub);
                var p = Path.Combine(sub, "x64", name);
                if (VersionFolder.IsMatch(leaf) && Version.TryParse(leaf, out var v) && File.Exists(p)) candidates.Add((v, Path.GetFullPath(p)));
            }
            var flat = Path.Combine(root, "x64", name);
            if (unversioned is null && File.Exists(flat)) unversioned = Path.GetFullPath(flat);
        }
        if (candidates.Count > 0) return candidates.OrderByDescending(c => c.Version).First().Path;
        return unversioned;
    }

    static string SdkInstallHint =>
        $"run \"winget install --interactive {SdkWingetId}\", or download the installer from {SdkDownloadUrl}, and in the installer choose only the signing tools";

    static string SdkMissingMessage(string name, string dir, IReadOnlyList<string> roots) =>
        $"Required tool {name} was not found in the installed Windows SDK ({(roots.Count == 0 ? "no SDK folders to search" : string.Join("; ", roots.Select(r => Path.Combine(r, "<version>", "x64"))))}) or in {dir}. " +
        $"{name} comes from the free Windows SDK, which this app does not include. To fix this, install the Windows SDK \"Signing Tools for Desktop Apps\" feature " +
        $"(called \"Windows SDK Signing Tools for Desktop Apps\" in the SDK installer's feature list; the exact label may vary by SDK version, and the file you need is {name}): " +
        $"run \"winget install --interactive {SdkWingetId}\" so you can pick features (a silent install may install all of them), or download the installer from {SdkDownloadUrl}, " +
        "and in the installer untick everything except the signing tools. Then restart this app.";
}
