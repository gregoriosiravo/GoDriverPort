using System.Text;

namespace LegionGoConverter.Core.Tests;

public class InfPatcherEdgeTests
{
    const string NewId = @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04";
    const string Generic = @"""%AMD15BF.781%"" = ati2mtag_Phoenix, PCI\VEN_1002&DEV_15BF&REV_00";
    const string Last = @"""%AMD15BF.815%"" = ati2mtag_Phoenix, PCI\VEN_1002&DEV_15BF&REV_F0";
    const string Inserted = @"""%AMD15BF.781%"" = ati2mtag_Phoenix, " + NewId;

    static byte[] L1(string s) => Encoding.Latin1.GetBytes(s);

    [Theory]
    [InlineData("PCI@VEN_1002&DEV_15BF&REV_04\r\n[Evil]")]
    [InlineData("PCI@VEN_1002&DEV_15BF&REV_04\n")]
    [InlineData("PCI@VEN_1002&DEV_15BF\"&REV_04")]
    [InlineData("PCI@VEN_1002&DEV_15BF,&REV_04")]
    [InlineData("PCI@VEN_1002&DEV_15BF;&REV_04")]
    [InlineData("PCI@VEN_1002&DEV_15BF&REV_0\u0416")]
    [InlineData("PCI@VEN_1002&DEV_15bf&REV_04")]
    [InlineData("pci@VEN_1002&DEV_15BF&REV_04")]
    [InlineData("")]
    public void AddDeviceId_InvalidHardwareId_ThrowsBeforeAnyWork(string id)
    {
        Assert.Throws<ArgumentException>(() => InfPatcher.AddDeviceId(L1("garbage"), id.Replace((char)64, (char)92)));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void AddDeviceId_LineEndings_Preserved(string eol)
    {
        var src = "[Version]" + eol + "[ATI.Mfg.NTamd64.10.0.1..19044]" + eol + Generic + eol + Last + eol + "[Strings]" + eol;
        var exp = "[Version]" + eol + "[ATI.Mfg.NTamd64.10.0.1..19044]" + eol + Generic + eol + Last + eol + Inserted + eol + "[Strings]" + eol;
        Assert.Equal(L1(exp), InfPatcher.AddDeviceId(L1(src), NewId).Bytes);
    }

    [Theory]
    [InlineData("\r\n")]
    [InlineData("\n")]
    public void AddDeviceId_LastLineWithoutTrailingNewline_ExactBytes(string eol)
    {
        var src = "[ATI.Mfg.NTamd64.10.0.1..19044]" + eol + Generic + eol + Last;
        var exp = src + eol + Inserted;
        Assert.Equal(L1(exp), InfPatcher.AddDeviceId(L1(src), NewId).Bytes);
    }

    [Fact]
    public void AddDeviceId_SectionAtEndOfFile_WithTrailingNewline()
    {
        var src = "[Version]\r\nx=1\r\n[ATI.Mfg.NTamd64.10.0.1..19044]\r\n" + Generic + "\r\n" + Last + "\r\n";
        var exp = src + Inserted + "\r\n";
        Assert.Equal(L1(exp), InfPatcher.AddDeviceId(L1(src), NewId).Bytes);
    }

    [Fact]
    public void AddDeviceId_SectionNameIsCaseInsensitive()
    {
        var src = "[ati.mfg.ntamd64.10.0.1..19044]\r\n" + Generic + "\r\n" + Last + "\r\n[S]\r\n";
        var r = InfPatcher.AddDeviceId(L1(src), NewId);
        Assert.Equal(L1(src.Replace(Last + "\r\n", Last + "\r\n" + Inserted + "\r\n")), r.Bytes);
        Assert.Equal("ati.mfg.ntamd64.10.0.1..19044", r.Section);
    }
}

public class AmdPackageEdgeTests
{
    sealed class TempDir : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lgo-" + Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
    }

    static string Inf(string buildLine) =>
        "[Version]\r\nClass=Display\r\nDriverVer=10/28/2026, 32.0.32015.12012\r\n" +
        "[ATI.Mfg.NTamd64.10.0.1..19044]\r\n" +
        @"""%A.1%"" = ati2mtag_Phoenix, PCI\VEN_1002&DEV_15BF&REV_00" + "\r\n[x]\r\n" + buildLine;

    const string GoodBuild = "HKR,,DriverBuildNumber,%REG_SZ%,2610280849-26.20.15.12-abc-AMD\r\n";

    static void Bins(string dir, bool sys = true, bool dat = true)
    {
        Directory.CreateDirectory(dir);
        if (sys) File.WriteAllText(Path.Combine(dir, "amdkmdag.sys"), "x");
        if (dat) File.WriteAllText(Path.Combine(dir, "amdgcf.dat"), "x");
    }

    [Theory]
    [InlineData(true, false, "amdgcf.dat")]
    [InlineData(false, true, "amdkmdag.sys")]
    [InlineData(false, false, "amdkmdag.sys")]
    public void Locate_BinariesMissing_Throws(bool sys, bool dat, string named)
    {
        using var d = new TempDir();
        File.WriteAllText(Path.Combine(d.Path, "a.inf"), Inf(GoodBuild));
        Bins(Path.Combine(d.Path, "bin"), sys, dat);
        var ex = Assert.Throws<PackageLayoutException>(() => AmdPackageLocator.Locate(d.Path));
        Assert.Contains("amdkmdag.sys", ex.Message);
        Assert.Contains("amdgcf.dat", ex.Message);
    }

    [Fact]
    public void Locate_TwoQualifyingDisplayInfs_Throws()
    {
        using var d = new TempDir();
        Bins(Path.Combine(d.Path, "bin"));
        File.WriteAllText(Path.Combine(d.Path, "a.inf"), Inf(GoodBuild));
        File.WriteAllText(Path.Combine(d.Path, "b.inf"), Inf(GoodBuild));
        var ex = Assert.Throws<PackageLayoutException>(() => AmdPackageLocator.Locate(d.Path));
        Assert.Contains("More than one", ex.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("HKR,,DriverBuildNumber,%REG_SZ%,nodash\r\n")]
    [InlineData("HKR,,DriverBuildNumber,%REG_SZ%,trailing-\r\n")]
    public void Locate_BadOrMissingDriverBuildNumber_Throws(string buildLine)
    {
        using var d = new TempDir();
        Bins(Path.Combine(d.Path, "bin"));
        File.WriteAllText(Path.Combine(d.Path, "a.inf"), Inf(buildLine));
        var ex = Assert.Throws<PackageLayoutException>(() => AmdPackageLocator.Locate(d.Path));
        Assert.Contains("DriverBuildNumber", ex.Message);
    }
}
