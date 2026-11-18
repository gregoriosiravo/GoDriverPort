namespace LegionGoConverter.Core.Tests;

public class TargetResolverTests
{
    const string Go1 = @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04";
    const string Go1Other = @"PCI\VEN_1002&DEV_15C8&SUBSYS_381217AA&REV_C9";

    static LegionGoProfile Profile() => new(
        new[] { Go1, Go1Other },
        new[] { new GcfRecord(0x15BF, 0x04), new GcfRecord(0x15C8, 0xC9) },
        "ext.inf", new Version(32, 0, 23044, 19), "disp.inf");

    static GpuDevice Dev(string instance, params string[] hwids) => new(instance, hwids, "1.0", "oem1.inf", 0);

    [Fact]
    public void Resolve_LegionGo1_ReturnsExactIdAndRecord()
    {
        var live = Dev(@"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04\4&1", Go1,
            @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA", @"PCI\VEN_1002&DEV_15BF&CC_030000");
        var r = TargetResolver.Resolve(new[] { live }, Profile());
        Assert.Equal(Go1, r.HardwareId);
        Assert.Equal(new GcfRecord(0x15BF, 0x04), r.GcfRecord);
        Assert.Same(live, r.Device);
    }

    [Fact]
    public void Resolve_SameIdOtherRevision_Throws()
    {
        var live = Dev("a", @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_05", @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA");
        Assert.Throws<UnsupportedDeviceException>(() => TargetResolver.Resolve(new[] { live }, Profile()));
    }

    [Fact]
    public void Resolve_LegionGoS_380C17AA_Throws()
    {
        var live = Dev("a", @"PCI\VEN_1002&DEV_15BF&SUBSYS_380C17AA&REV_04", @"PCI\VEN_1002&DEV_15BF&SUBSYS_380C17AA");
        Assert.Throws<UnsupportedDeviceException>(() => TargetResolver.Resolve(new[] { live }, Profile()));
    }

    [Fact]
    public void Resolve_NoAmdGpu_Throws()
    {
        Assert.Throws<UnsupportedDeviceException>(() => TargetResolver.Resolve(Array.Empty<GpuDevice>(), Profile()));
    }

    [Fact]
    public void Resolve_NonLenovoSubsystem_Throws()
    {
        var live = Dev("a", @"PCI\VEN_1002&DEV_15BF&SUBSYS_88881043&REV_04", @"PCI\VEN_1002&DEV_15BF&SUBSYS_88881043");
        Assert.Throws<UnsupportedDeviceException>(() => TargetResolver.Resolve(new[] { live }, Profile()));
    }

    [Fact]
    public void Resolve_StaleNonPresentNvidiaEntry_Ignored()
    {
        var nv = Dev(@"PCI\VEN_10DE&DEV_2206\1", @"PCI\VEN_10DE&DEV_2206&SUBSYS_38121043&REV_A1");
        var amd = Dev("amd", Go1);
        var r = TargetResolver.Resolve(new[] { nv, amd }, Profile());
        Assert.Same(amd, r.Device);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var live = Dev("a", Go1.ToLowerInvariant());
        var r = TargetResolver.Resolve(new[] { live }, Profile());
        Assert.Equal(Go1, r.HardwareId);
    }

    [RealPackageFact(RealPackage.Amd)]
    public void LenovoPackage_Load_OnAmdPackage_Throws()
    {
        Assert.Throws<PackageLayoutException>(() => LenovoPackage.Load(RealPackages.AmdRoot));
    }

    [RealPackageFact(RealPackage.Lenovo)]
    public void LenovoPackage_Load_OnLenovoPackage_YieldsLegionGoIds()
    {
        var p = LenovoPackage.Load(RealPackages.LenovoRoot);
        Assert.Equal(new[] { Go1, Go1Other }, p.HardwareIds.OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(new[] { new GcfRecord(0x15BF, 0x04), new GcfRecord(0x15C8, 0xC9) }, p.GcfRecords.OrderBy(r => r.Device));
        Assert.Equal(new Version(32, 0, 23044, 19), p.LenovoDriverVersion);
        Assert.EndsWith("u0202094.inf", p.LenovoDisplayInfPath);
        Assert.EndsWith("amduw23e.inf", p.ExtensionInfPath);
    }

    [Fact]
    public void LenovoPackage_Load_MissingFolder_Throws()
    {
        Assert.Throws<PackageLayoutException>(() => LenovoPackage.Load(Path.Combine(Path.GetTempPath(), "lgo-nonexistent-" + Guid.NewGuid())));
    }

    [Fact]
    public void Resolve_DeviceWithProblemCode43_StillResolves()
    {
        var live = new GpuDevice("a", new[] { Go1 }, null, null, 43);
        Assert.Same(live, TargetResolver.Resolve(new[] { live }, Profile()).Device);
    }

    [Fact]
    public void Resolve_IgpuLegionGoPlusOtherAmdDgpu_ResolvesTheLegionGoDevice()
    {
        var igpu = Dev("igpu", Go1);
        var dgpu = Dev(@"PCI\VEN_1002&DEV_73FF\1", @"PCI\VEN_1002&DEV_73FF&SUBSYS_00011002&REV_C1");
        Assert.Same(igpu, TargetResolver.Resolve(new[] { igpu, dgpu }, Profile()).Device);
        Assert.Same(igpu, TargetResolver.Resolve(new[] { dgpu, igpu }, Profile()).Device);
    }

    [Fact]
    public void Resolve_ProfileIdWithoutGcfRecord_Throws()
    {
        var profile = Profile() with { GcfRecords = new[] { new GcfRecord(0x15C8, 0xC9) } };
        var ex = Assert.Throws<UnsupportedDeviceException>(() => TargetResolver.Resolve(new[] { Dev("a", Go1) }, profile));
        Assert.Contains(Go1, ex.Message);
    }

    [Fact]
    public void Resolve_AmdGpuWithNoHardwareIds_GivesDistinctError()
    {
        var live = new GpuDevice(@"PCI\VEN_1002&DEV_15BF\1", Array.Empty<string>(), null, null, 0);
        var ex = Assert.Throws<UnsupportedDeviceException>(() => TargetResolver.Resolve(new[] { live }, Profile()));
        Assert.Contains("no hardware IDs", ex.Message);
    }

    [Fact]
    public void Probe_WmiFailure_IsWrappedInDeviceProbeException()
    {
        var inner = new System.Runtime.InteropServices.COMException("boom");
        var probe = new WmiDeviceProbe(() => throw inner);
        var ex = Assert.Throws<DeviceProbeException>(() => probe.GetAmdDisplayDevices());
        Assert.Same(inner, ex.InnerException);
    }

    // Intentionally hardware-agnostic: passes vacuously on machines without an AMD GPU.
    // Asserts on the same property the probe filters on (instance id).
    [Fact]
    public void RealProbe_DoesNotThrow_AndReturnsOnlyAmdDevices()
    {
        var devices = new WmiDeviceProbe().GetAmdDisplayDevices();
        Assert.All(devices, d => Assert.StartsWith(@"PCI\VEN_1002", d.InstanceId, StringComparison.OrdinalIgnoreCase));
    }
}
