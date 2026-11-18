namespace LegionGoConverter.Core.Tests;

public class AmdPackageTests
{
    [RealPackageFact]
    public void Locate_RealAmd_FindsEverything()
    {
        var p = AmdPackageLocator.Locate(RealPackages.AmdRoot);
        Assert.EndsWith("u0205499.inf", p.InfPath);
        Assert.True(File.Exists(Path.Combine(p.BinariesDir, "amdkmdag.sys")));
        Assert.True(File.Exists(Path.Combine(p.BinariesDir, "amdgcf.dat")));
        Assert.Equal("26.20.15.12-261023a1-205499C-AMD-Software-Adrenalin-Edition", p.ReleaseString);
        Assert.Equal(new Version(32, 0, 32015, 12012), p.DriverVersion);
        Assert.NotNull(p.Ccc2Path);
        Assert.EndsWith("ccc2_install.exe", p.Ccc2Path);
    }

    [RealPackageFact(RealPackage.Lenovo)]
    public void Locate_RealLenovo_Throws()
    {
        Assert.Throws<PackageLayoutException>(() => AmdPackageLocator.Locate(RealPackages.LenovoRoot));
    }

    [Fact]
    public void Locate_EmptyFolder_Throws()
    {
        using var d = new TempDir();
        Assert.Throws<PackageLayoutException>(() => AmdPackageLocator.Locate(d.Path));
    }

    [Fact]
    public void Locate_RenamedFilesAndFolders_Succeeds()
    {
        using var d = new TempDir();
        var disp = Directory.CreateDirectory(Path.Combine(d.Path, "x$y$", "Whatever")).FullName;
        var bin = Directory.CreateDirectory(Path.Combine(disp, "BinDir")).FullName;
        File.WriteAllText(Path.Combine(bin, "amdkmdag.sys"), "x");
        File.WriteAllText(Path.Combine(bin, "amdgcf.dat"), "x");
        File.WriteAllText(Path.Combine(bin, "ccc2_install.exe"), "x");
        File.WriteAllText(Path.Combine(disp, "other.inf"), "[Version]\r\nClass=Media\r\n");
        File.WriteAllText(Path.Combine(disp, "renamed.inf"),
            "[Version]\r\nClass=Display\r\nDriverVer=10/28/2026, 32.0.32015.12012\r\n" +
            "[ATI.Mfg.NTamd64.10.0.1..19044]\r\n" +
            "\"%A.1%\" = ati2mtag_Phoenix, PCI\\VEN_1002&DEV_15BF&REV_00\r\n" +
            "[x]\r\nHKR,,DriverBuildNumber,%REG_SZ%,2610280849-26.20.15.12-abc-AMD\r\n");
        var p = AmdPackageLocator.Locate(d.Path);
        Assert.Equal(bin, p.BinariesDir);
        Assert.Equal(disp, p.DisplayDir);
        Assert.Equal("26.20.15.12-abc-AMD", p.ReleaseString);
        Assert.Equal(Path.Combine(bin, "ccc2_install.exe"), p.Ccc2Path);
    }

    [Fact]
    public void Locate_DisplayInfWithOnlySubsysLines_Throws()
    {
        using var d = new TempDir();
        File.WriteAllText(Path.Combine(d.Path, "a.inf"),
            "[Version]\r\nClass=Display\r\n[M]\r\n" +
            "\"%A.1%\" = s, PCI\\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04\r\n");
        Assert.Throws<PackageLayoutException>(() => AmdPackageLocator.Locate(d.Path));
    }

    sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lgo-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }
}
