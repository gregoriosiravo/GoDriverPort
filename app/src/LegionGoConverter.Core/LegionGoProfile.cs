using System.Text.RegularExpressions;

namespace LegionGoConverter.Core;

public sealed record LegionGoProfile(
    IReadOnlyList<string> HardwareIds,
    IReadOnlyList<GcfRecord> GcfRecords,
    string ExtensionInfPath,
    Version LenovoDriverVersion,
    string LenovoDisplayInfPath);

public static class LenovoPackage
{
    static readonly Regex ClassExtension = new(@"^\s*Class\s*=\s*Extension\s*(;.*)?$", RegexOptions.IgnoreCase | RegexOptions.Multiline);
    static readonly Regex LenovoId = new(
        @"PCI\\VEN_1002&DEV_15[0-9A-F]{2}&SUBSYS_[0-9A-F]{4}17AA&REV_[0-9A-F]{2}", RegexOptions.IgnoreCase);

    public static LegionGoProfile Load(string extractedRoot)
    {
        if (!Directory.Exists(extractedRoot))
            throw new PackageLayoutException($"Package folder not found: {extractedRoot}");

        string? extPath = null;
        List<string>? ids = null;
        foreach (var f in Directory.EnumerateFiles(extractedRoot, "*.inf", AmdPackageLocator.Recurse).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            string text;
            try { text = AmdPackageLocator.ReadInf(f); } catch (IOException) { continue; } catch (UnauthorizedAccessException) { continue; }
            if (!ClassExtension.IsMatch(text)) continue;
            var found = new List<string>();
            foreach (var line in text.Split('\n'))
            {
                var code = line.Split(';')[0];
                foreach (Match m in LenovoId.Matches(code))
                    if (!found.Contains(m.Value, StringComparer.OrdinalIgnoreCase)) found.Add(m.Value.ToUpperInvariant());
            }
            if (found.Count == 0) continue;
            if (extPath != null)
                throw new PackageLayoutException($"More than one Lenovo extension INF: {extPath} and {f}");
            extPath = f; ids = found;
        }
        if (extPath == null || ids == null)
            throw new PackageLayoutException(
                "No Lenovo extension INF (Class=Extension listing PCI\\VEN_1002&DEV_15xx&SUBSYS_xxxx17AA&REV_xx) found under " + extractedRoot);

        var dir = Path.GetDirectoryName(extPath)!;
        string? displayInf = null; string? displayText = null;
        foreach (var f in Directory.EnumerateFiles(dir, "*.inf").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var t = AmdPackageLocator.ReadInf(f);
            if (!AmdPackageLocator.ClassDisplay.IsMatch(t)) continue;
            if (displayInf != null)
                throw new PackageLayoutException($"More than one Class=Display INF in {dir}");
            displayInf = f; displayText = t;
        }
        if (displayInf == null || displayText == null)
            throw new PackageLayoutException("No Class=Display INF next to extension INF " + extPath);

        var dv = AmdPackageLocator.DriverVer.Match(displayText);
        if (!dv.Success || !Version.TryParse(dv.Groups[1].Value, out var version))
            throw new PackageLayoutException("DriverVer not found or unparsable in " + displayInf);
        var bn = AmdPackageLocator.BuildNumber.Match(displayText);
        if (!bn.Success)
            throw new PackageLayoutException("DriverBuildNumber not found in " + displayInf);
        var raw = bn.Groups["v"].Value.Trim().Trim('"');
        int dash = raw.IndexOf('-');
        if (dash < 0 || dash == raw.Length - 1)
            throw new PackageLayoutException($"Unexpected DriverBuildNumber format: {raw}");
        var release = raw[(dash + 1)..];

        var binDirs = Directory.EnumerateFiles(extractedRoot, "amdkmdag.sys", AmdPackageLocator.Recurse)
            .Select(p => Path.GetDirectoryName(p)!)
            .Where(d => File.Exists(Path.Combine(d, "amdgcf.dat")))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (binDirs.Count == 0)
            throw new PackageLayoutException("No folder containing both amdkmdag.sys and amdgcf.dat found under " + extractedRoot);
        var binDir = binDirs.FirstOrDefault(d => d.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) ?? binDirs[0];

        IReadOnlyList<GcfRecord> records;
        try { records = AmdGcf.Read(File.ReadAllBytes(Path.Combine(binDir, "amdgcf.dat")), release); }
        catch (AmdGcfFormatException e) { throw new PackageLayoutException("Lenovo amdgcf.dat unreadable: " + e.Message); }

        return new LegionGoProfile(ids, records, extPath, version, displayInf);
    }
}
