using System.Text;
using System.Text.RegularExpressions;

namespace LegionGoConverter.Core;

public sealed record InfPatchResult(byte[] Bytes, string Section, string InsertedLine, bool AlreadyPresent);

public static class InfPatcher
{
    static readonly Regex ModelsSection = new(@"^\[(ATI\.Mfg\.NTamd64\.10\.0[^\]]*)\]\s*$", RegexOptions.IgnoreCase);
    static readonly Regex StrictHwId = new(
        @"\APCI\\VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}(&SUBSYS_[0-9A-F]{8})?(&REV_[0-9A-F]{2})?\z", RegexOptions.CultureInvariant);
    static readonly Regex DeviceInHwId = new(@"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase);
    static readonly Regex ModelLine = new(
        @"^\s*""%(?<key>[^%""]+)%""\s*=\s*(?<drv>[^,\s]+)\s*,\s*(?<hwid>[^\s,;]+)", RegexOptions.IgnoreCase);

    /// <summary>
    /// Inserts a model line for <paramref name="hardwareId"/> after the last line for the same device in the single
    /// ATI.Mfg.NTamd64.10.0* section, copying driver section and display-name key from the first generic line.
    /// </summary>
    public static InfPatchResult AddDeviceId(byte[] inf, string hardwareId)
    {
        if (hardwareId is null || !StrictHwId.IsMatch(hardwareId))
            throw new ArgumentException("Hardware ID must look like PCI\\VEN_xxxx&DEV_xxxx[&SUBSYS_xxxxxxxx][&REV_xx] (uppercase hex).", nameof(hardwareId));
        var dev = DeviceInHwId.Match(hardwareId);
        var devToken = "DEV_" + dev.Groups[1].Value.ToUpperInvariant();

        bool utf16 = inf.Length >= 2 && inf[0] == 0xFF && inf[1] == 0xFE;
        Encoding enc = utf16 ? new UnicodeEncoding(false, false) : Encoding.Latin1;
        int bom = utf16 ? 2 : 0;
        string text = enc.GetString(inf, bom, inf.Length - bom);

        // Split into lines keeping terminators.
        var lines = new List<string>();
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') { lines.Add(text.Substring(start, i - start + 1)); start = i + 1; }
        }
        if (start < text.Length) lines.Add(text.Substring(start));

        // Find candidate sections: models sections containing lines for this device.
        string? currentSection = null;
        var hits = new List<(string Section, int Index)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var t = lines[i].Trim();
            if (t.StartsWith('['))
            {
                var m = ModelsSection.Match(t);
                currentSection = m.Success ? m.Groups[1].Value : null;
                continue;
            }
            if (currentSection == null || t.StartsWith(';')) continue;
            var ml = ModelLine.Match(lines[i]);
            if (ml.Success && ml.Groups["hwid"].Value.Contains(devToken, StringComparison.OrdinalIgnoreCase))
                hits.Add((currentSection, i));
        }

        var sections = hits.Select(h => h.Section).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (sections.Count == 0)
            throw new PackageLayoutException($"No [ATI.Mfg.NTamd64.10.0*] models section with {devToken} lines found in the INF.");
        if (sections.Count > 1)
            throw new PackageLayoutException(
                $"Expected exactly one models section with {devToken} lines, found {sections.Count}: {string.Join(", ", sections)}.");
        var section = sections[0];

        // Already present?
        foreach (var h in hits)
        {
            var ml = ModelLine.Match(lines[h.Index]);
            if (string.Equals(ml.Groups["hwid"].Value, hardwareId, StringComparison.OrdinalIgnoreCase))
                return new InfPatchResult(inf, section, lines[h.Index].TrimEnd('\r', '\n'), true);
        }

        // Template: first generic (no SUBSYS) line for the device.
        Match? template = null;
        foreach (var h in hits)
        {
            var ml = ModelLine.Match(lines[h.Index]);
            if (!ml.Groups["hwid"].Value.Contains("SUBSYS_", StringComparison.OrdinalIgnoreCase)) { template = ml; break; }
        }
        if (template == null)
            throw new PackageLayoutException($"No generic (no SUBSYS) {devToken} line found to copy the driver section from.");

        var inserted = $"\"%{template.Groups["key"].Value}%\" = {template.Groups["drv"].Value}, {hardwareId}";

        int last = hits[^1].Index;
        var lastLine = lines[last];
        bool unterminated = !lastLine.EndsWith('\n');
        // An unterminated final line borrows the previous line's terminator (CRLF if there is none).
        string eol = unterminated
            ? (last > 0 && lines[last - 1].EndsWith("\r\n") ? "\r\n" : last > 0 && lines[last - 1].EndsWith('\n') ? "\n" : "\r\n")
            : lastLine.EndsWith("\r\n") ? "\r\n" : "\n";
        if (unterminated) lines[last] = lastLine + eol;
        lines.Insert(last + 1, inserted + (unterminated ? "" : eol));

        var body = enc.GetBytes(string.Concat(lines));
        var result = new byte[bom + body.Length];
        if (utf16) { result[0] = 0xFF; result[1] = 0xFE; }
        body.CopyTo(result, bom);
        return new InfPatchResult(result, section, inserted, false);
    }
}
