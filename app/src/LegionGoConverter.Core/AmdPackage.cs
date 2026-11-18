using System.Text;
using System.Text.RegularExpressions;

namespace LegionGoConverter.Core;

public class PackageLayoutException : Exception
{
    public PackageLayoutException(string message) : base(message) { }
}

public sealed record AmdPackage(
    string DisplayDir, string InfPath, string BinariesDir, string ReleaseString, Version DriverVersion, string? Ccc2Path);

public static class AmdPackageLocator
{
    internal static readonly Regex ClassDisplay = new(@"^\s*Class\s*=\s*Display\s*(;.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    static readonly Regex GenericLine = new(
        @"^\s*""[^""]*""\s*=\s*[^,\r\n]+,\s*PCI\\VEN_1002&DEV_15BF&REV_[0-9A-F]{2}\s*(;.*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline);
    internal static readonly Regex DriverVer = new(@"^\s*DriverVer\s*=\s*[^,\r\n]+,\s*([0-9.]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    internal static readonly Regex BuildNumber = new(@"^\s*HKR\s*,\s*,\s*DriverBuildNumber\s*,\s*[^,\r\n]+,\s*(?<v>[^\r\n]+)", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    internal static readonly EnumerationOptions Recurse = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static AmdPackage Locate(string extractedRoot)
    {
        if (!Directory.Exists(extractedRoot))
            throw new PackageLayoutException($"Package folder not found: {extractedRoot}");

        string? infPath = null;
        string? infText = null;
        foreach (var f in Directory.EnumerateFiles(extractedRoot, "*.inf", Recurse).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string text;
            try { text = ReadInf(f); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            if (ClassDisplay.IsMatch(text) && GenericLine.IsMatch(text))
            {
                if (infPath != null)
                    throw new PackageLayoutException($"More than one display INF with a generic DEV_15BF line: {infPath} and {f}");
                infPath = f; infText = text;
            }
        }
        if (infPath == null || infText == null)
            throw new PackageLayoutException(
                "No display INF (Class=Display with a generic PCI\\VEN_1002&DEV_15BF&REV_xx line, no SUBSYS) found under " + extractedRoot);

        var displayDir = Path.GetDirectoryName(infPath)!;

        var binCandidates = Directory.EnumerateFiles(extractedRoot, "amdkmdag.sys", Recurse)
            .Select(p => Path.GetDirectoryName(p)!)
            .Where(d => File.Exists(Path.Combine(d, "amdgcf.dat")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (binCandidates.Count == 0)
            throw new PackageLayoutException("No folder containing both amdkmdag.sys and amdgcf.dat found under " + extractedRoot);
        var binDir = binCandidates.FirstOrDefault(d => d.StartsWith(displayDir, StringComparison.OrdinalIgnoreCase)) ?? binCandidates[0];

        var bn = BuildNumber.Match(infText);
        if (!bn.Success)
            throw new PackageLayoutException("DriverBuildNumber not found in " + infPath);
        var raw = bn.Groups["v"].Value.Trim().Trim('"');
        int dash = raw.IndexOf('-');
        if (dash < 0 || dash == raw.Length - 1)
            throw new PackageLayoutException($"Unexpected DriverBuildNumber format: {raw}");
        var release = raw[(dash + 1)..];

        var dv = DriverVer.Match(infText);
        if (!dv.Success || !Version.TryParse(dv.Groups[1].Value, out var version))
            throw new PackageLayoutException("DriverVer not found or unparsable in " + infPath);

        var ccc2 = Path.Combine(binDir, "ccc2_install.exe");
        return new AmdPackage(displayDir, infPath, binDir, release, version, File.Exists(ccc2) ? ccc2 : null);
    }

    internal static string ReadInf(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        return Encoding.Latin1.GetString(b);
    }
}
