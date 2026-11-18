using LegionGoConverter.Core;

namespace LegionGoConverter.App;

/// <summary>Read-only: the first present AMD display device's hardware ID (the first one the INF can match is chosen by
/// the pipeline itself) and the Secure Boot / HVCI / test-signing state. Failures become a message; never throws.</summary>
public sealed class DeviceInfoProvider(IDeviceProbe probe, IEnvironmentProbe env) : IDeviceInfoProvider
{
    public DeviceInfo Get()
    {
        SecurityState security;
        try { security = env.GetSecurityState(); }
        catch (Exception e) { return Fail("Could not read the Secure Boot / HVCI state: " + e.Message, null); }
        try
        {
            var dev = probe.GetAmdDisplayDevices().FirstOrDefault();
            var id = dev?.HardwareIds.FirstOrDefault();
            return id is null
                ? new DeviceInfo(null, security, "No AMD display device was found.")
                : new DeviceInfo(id, security);
        }
        catch (Exception e) { return Fail("Could not read the display device: " + e.Message, security); }
    }

    static DeviceInfo Fail(string message, SecurityState? s) => new(null, s, message);
}
