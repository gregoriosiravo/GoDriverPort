using System.Security.Cryptography;

namespace LegionGoConverter.Core.Tests;

public class AmdGcfTests
{
    const string AmdRelease = "26.20.15.12-261023a1-205499C-AMD-Software-Adrenalin-Edition";

    [Fact]
    public void Write_LenovoRecords_MatchesLenovoFile()
    {
        var bytes = AmdGcf.Write(
            new[] { new GcfRecord(0x15BF, 0x04), new GcfRecord(0x15C8, 0xC9) },
            "25.30.44-260512a-202094C-Lenovo");
        Assert.Equal(Convert.FromHexString("02000000D9531DB7616978D8CB1504D715C9"), bytes);
    }

    [Fact]
    public void Read_Write_RoundTrips()
    {
        var recs = Enumerable.Range(0, 227).Select(i => new GcfRecord(0x1000 + i * 7, i % 256)).ToList();
        var dat = AmdGcf.Write(recs, AmdRelease);
        var read = AmdGcf.Read(dat, AmdRelease);
        Assert.Equal(recs, read);
        Assert.Equal(dat, AmdGcf.Write(read, AmdRelease));
    }

    [Fact]
    public void Read_WrongRelease_Throws()
    {
        var dat = AmdGcf.Write(new[] { new GcfRecord(0x15BF, 4) }, AmdRelease);
        Assert.Throws<AmdGcfFormatException>(() => AmdGcf.Read(dat, "wrong"));
    }

    [Fact]
    public void Read_TruncatedFile_Throws()
    {
        var dat = AmdGcf.Write(new[] { new GcfRecord(0x15BF, 4), new GcfRecord(0x15C8, 1) }, AmdRelease);
        Assert.Throws<AmdGcfFormatException>(() => AmdGcf.Read(dat[..^1], AmdRelease));
        Assert.Throws<AmdGcfFormatException>(() => AmdGcf.Read(new byte[5], AmdRelease));
    }

    [Fact]
    public void AddRecords_ExistingRecord_ReturnsSameBytes()
    {
        var dat = AmdGcf.Write(new[] { new GcfRecord(0x15BF, 4), new GcfRecord(0x15C8, 1) }, AmdRelease);
        Assert.Equal(dat, AmdGcf.AddRecords(dat, AmdRelease, new[] { new GcfRecord(0x15BF, 4) }));
    }

    static string AmdDatPath() =>
        Path.Combine(RealPackages.AmdRoot, "Packages", "Drivers", "Display", "WT6A_INF", "B026716", "amdgcf.dat");

    [RealPackageFact]
    public void RealAmdDat_RoundTripsByteIdentical()
    {
        var dat = File.ReadAllBytes(AmdDatPath());
        Assert.Equal("DF7D6CB0F0AE380B58F36EAB4E2F41D36085F14680733F018A3DACA1696BA022",
            Convert.ToHexString(SHA256.HashData(dat)));
        var recs = AmdGcf.Read(dat, AmdRelease);
        Assert.Equal(dat, AmdGcf.Write(recs, AmdRelease));
    }

    [RealPackageFact]
    public void RealAmdDat_AddLenovoRecord_MatchesKnownHash()
    {
        var dat = File.ReadAllBytes(AmdDatPath());
        var added = AmdGcf.AddRecords(dat, AmdRelease, new[] { new GcfRecord(0x15BF, 0x04) });
        Assert.Equal(696, added.Length);
        Assert.Equal("808CB0956B5D67337CC5ADA7DB4F041A6092678751564B0F1F7790EEA0B18A20",
            Convert.ToHexString(SHA256.HashData(added)));
    }
}
