using System.Text.RegularExpressions;

namespace LegionGoConverter.Core;

public sealed class UnsupportedDeviceException : Exception
{
    public UnsupportedDeviceException(string message) : base(message) { }
}

public sealed record ResolvedTarget(GpuDevice Device, string HardwareId, GcfRecord GcfRecord);

public static class TargetResolver
{
    static readonly Regex DevRev = new(@"&DEV_(?<d>[0-9A-F]{4})&.*&REV_(?<r>[0-9A-F]{2})$", RegexOptions.IgnoreCase);

    public static ResolvedTarget Resolve(IReadOnlyList<GpuDevice> live, LegionGoProfile profile)
    {
        // ProblemCode is deliberately ignored: we install over broken drivers (e.g. code 28 or 43),
        // so a device with an error code still resolves. Only an exact hardware-ID match counts,
        // so with an iGPU plus a dGPU the Legion Go device is picked regardless of list order.
        var amd = live.Where(d => d.InstanceId.StartsWith(@"PCI\VEN_1002", StringComparison.OrdinalIgnoreCase)
            || d.HardwareIds.Any(h => h.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))).ToList();
        if (amd.Count == 0)
            throw new UnsupportedDeviceException("No AMD display device found.");
        if (amd.All(d => d.HardwareIds.Count == 0))
            throw new UnsupportedDeviceException("An AMD GPU is present but Windows reports no hardware IDs for it.");
        foreach (var dev in amd)
            foreach (var id in profile.HardwareIds)
            {
                var hit = dev.HardwareIds.FirstOrDefault(h => string.Equals(h, id, StringComparison.OrdinalIgnoreCase));
                if (hit == null) continue;
                var m = DevRev.Match(hit); // parsed from the live ID (equal to the profile ID)
                if (!m.Success) continue;
                int d = Convert.ToInt32(m.Groups["d"].Value, 16), r = Convert.ToInt32(m.Groups["r"].Value, 16);
                var rec = profile.GcfRecords.FirstOrDefault(g => g.Device == d && g.Revision == r);
                if (rec == null)
                    throw new UnsupportedDeviceException($"Lenovo package has no amdgcf.dat record for {id}.");
                return new ResolvedTarget(dev, id, rec);
            }
        throw new UnsupportedDeviceException(
            "No AMD display device matches a Legion Go hardware ID exactly (subsystem and revision). Found: "
            + string.Join("; ", amd.Select(d => d.HardwareIds.FirstOrDefault() ?? d.InstanceId)));
    }
}
