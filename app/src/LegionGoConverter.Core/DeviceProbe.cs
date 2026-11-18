using System.Management;

namespace LegionGoConverter.Core;

public sealed record GpuDevice(string InstanceId, IReadOnlyList<string> HardwareIds, string? DriverVersion, string? DriverInf, int ProblemCode);

public interface IDeviceProbe
{
    IReadOnlyList<GpuDevice> GetAmdDisplayDevices();
}

/// <summary>Read-only WMI query of present AMD display devices.</summary>
public sealed class DeviceProbeException : Exception
{
    public DeviceProbeException(string message, Exception inner) : base(message, inner) { }
}

public sealed class WmiDeviceProbe : IDeviceProbe
{
    readonly Func<IReadOnlyList<GpuDevice>> _query;

    public WmiDeviceProbe() : this(QueryWmi) { }

    /// <summary>Test seam: supply a replacement for the WMI query.</summary>
    public WmiDeviceProbe(Func<IReadOnlyList<GpuDevice>> query) { _query = query; }

    public IReadOnlyList<GpuDevice> GetAmdDisplayDevices()
    {
        try { return _query(); }
        catch (Exception e) when (e is ManagementException or System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            throw new DeviceProbeException(
                "Could not query the system for display devices (WMI failed: " + e.Message + "). Try running as administrator.", e);
        }
    }

    static IReadOnlyList<GpuDevice> QueryWmi()
    {
        var result = new List<GpuDevice>();
        using var pnp = new ManagementObjectSearcher(
            "SELECT DeviceID, HardwareID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE PNPClass = 'Display'");
        foreach (ManagementObject o in pnp.Get())
        {
            using (o)
            {
                var id = o["DeviceID"] as string;
                var hw = (o["HardwareID"] as string[]) ?? Array.Empty<string>();
                if (id == null || !id.StartsWith(@"PCI\VEN_1002", StringComparison.OrdinalIgnoreCase)) continue;
                int problem = o["ConfigManagerErrorCode"] is uint u ? (int)u : 0;
                string? ver = null, inf = null;
                var escaped = id.Replace(@"\", @"\\").Replace("'", @"\'");
                using var drv = new ManagementObjectSearcher(
                    $"SELECT DriverVersion, InfName FROM Win32_PnPSignedDriver WHERE DeviceID = '{escaped}'");
                foreach (ManagementObject d in drv.Get())
                {
                    using (d) { ver = d["DriverVersion"] as string; inf = d["InfName"] as string; }
                    break;
                }
                result.Add(new GpuDevice(id, hw, ver, inf, problem));
            }
        }
        return result;
    }
}
