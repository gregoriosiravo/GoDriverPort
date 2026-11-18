using System.Security.Cryptography;
using System.Text;

namespace LegionGoConverter.Core.Tests;

public class InfPatcherTests
{
    const string NewId = @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04";

    const string Inf =
        "[Version]\r\nClass=Display\r\n\r\n" +
        "[ATI.Mfg.NTamd64.10.0.1..19044]\r\n" +
        "\"%AMD15C8.1%\" = ati2mtag_Other, PCI\\VEN_1002&DEV_15C8&REV_C1\r\n" +
        "\"%AMD15BF.1%\" = ati2mtag_Phoenix, PCI\\VEN_1002&DEV_15BF&SUBSYS_123417AA&REV_00\r\n" +
        "\"%AMD15BF.781%\" = ati2mtag_Phoenix, PCI\\VEN_1002&DEV_15BF&REV_00\r\n" +
        "\"%AMD15BF.815%\" = ati2mtag_Phoenix, PCI\\VEN_1002&DEV_15BF&REV_F0\r\n" +
        "\"%AMD1638.1%\" = ati2mtag_Other, PCI\\VEN_1002&DEV_1638&REV_C1\r\n" +
        "\r\n[Strings]\r\nx = \"caf\u00e9\"\r\n";

    static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);

    [Fact]
    public void AddDeviceId_InsertsOneLine_AfterLastMatchingLine_AllOtherBytesUnchanged()
    {
        var src = Latin1(Inf);
        var r = InfPatcher.AddDeviceId(src, NewId);
        var expectedLine = "\"%AMD15BF.781%\" = ati2mtag_Phoenix, " + NewId;
        Assert.False(r.AlreadyPresent);
        Assert.Equal(expectedLine, r.InsertedLine);
        Assert.Equal("ATI.Mfg.NTamd64.10.0.1..19044", r.Section);
        var anchor = "DEV_15BF&REV_F0\r\n";
        var expected = Inf.Replace(anchor, anchor + expectedLine + "\r\n");
        Assert.Equal(Latin1(expected), r.Bytes);
        Assert.Equal(src.Length + Latin1(expectedLine + "\r\n").Length, r.Bytes.Length);
    }

    [Fact]
    public void AddDeviceId_Utf16WithBom_PreservesEncoding()
    {
        var src = new UnicodeEncoding(false, true).GetPreamble()
            .Concat(Encoding.Unicode.GetBytes(Inf)).ToArray();
        var r = InfPatcher.AddDeviceId(src, NewId);
        Assert.Equal(new byte[] { 0xFF, 0xFE }, r.Bytes[..2]);
        var text = Encoding.Unicode.GetString(r.Bytes, 2, r.Bytes.Length - 2);
        var line = "\"%AMD15BF.781%\" = ati2mtag_Phoenix, " + NewId;
        Assert.Equal(Inf.Replace("DEV_15BF&REV_F0\r\n", "DEV_15BF&REV_F0\r\n" + line + "\r\n"), text);
    }

    [Fact]
    public void AddDeviceId_AlreadyPresent_ReturnsUnchanged()
    {
        var src = Latin1(Inf);
        var once = InfPatcher.AddDeviceId(src, NewId);
        var twice = InfPatcher.AddDeviceId(once.Bytes, NewId);
        Assert.True(twice.AlreadyPresent);
        Assert.Equal(once.Bytes, twice.Bytes);
    }

    [Fact]
    public void AddDeviceId_NoModelsSection_Throws()
    {
        var inf = Latin1("[Version]\r\nClass=Display\r\n[Strings]\r\nx=1\r\n");
        Assert.Throws<PackageLayoutException>(() => InfPatcher.AddDeviceId(inf, NewId));
    }

    [Fact]
    public void AddDeviceId_TwoCandidateSections_Throws()
    {
        var inf = Latin1(Inf + "[ATI.Mfg.NTamd64.10.0...22000]\r\n" +
            "\"%AMD15BF.9%\" = ati2mtag_Phoenix, PCI\\VEN_1002&DEV_15BF&REV_00\r\n");
        Assert.Throws<PackageLayoutException>(() => InfPatcher.AddDeviceId(inf, NewId));
    }

    [RealPackageFact]
    public void AddDeviceId_RealAmdInf_MatchesExpectedBytes()
    {
        var inf = Directory.EnumerateFiles(RealPackages.AmdRoot, "u0205499.inf", SearchOption.AllDirectories).Single();
        var r = InfPatcher.AddDeviceId(File.ReadAllBytes(inf), NewId);
        Assert.Equal("\"%AMD15BF.781%\" = ati2mtag_Phoenix, " + NewId, r.InsertedLine);
        Assert.Equal(1119446, r.Bytes.Length);
        Assert.Equal("827E87CAA6703F4405D4948864A0B4E43F898E6AB91E4408A20D7501E2D2E3F4",
            Convert.ToHexString(SHA256.HashData(r.Bytes)));
    }
}
