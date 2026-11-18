using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace LegionGoConverter.Core.Tests;

/// <summary>A fact skipped unless the real extracted AMD package and the real makecat/signtool are present.</summary>
public sealed class RealAmdBuildFactAttribute : FactAttribute
{
    public RealAmdBuildFactAttribute()
    {
        if (!Directory.Exists(RealPackages.AmdRoot)) Skip = $"Real package folder not found: {RealPackages.AmdRoot}";
        else if (!File.Exists(RealToolsLocator.MakeCat) || !File.Exists(RealToolsLocator.SignTool))
            Skip = $"makecat/signtool not found in {RealToolsLocator.Dir}";
    }
}

/// <summary>SLOW (copies and hashes the 2.2 GB AMD display tree): the Analyze/Build/Sign steps the pipeline runs, against
/// the real AMD 26.10.2 package. Touches only a temp folder and a throwaway test certificate in CurrentUser\My (removed
/// with its key afterwards). The hardware ID comes from a fake device list, never from the real hardware.</summary>
public class RealPackageBuildTests
{
    const string Go1 = @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04";
    const string InfSha256 = "827E87CAA6703F4405D4948864A0B4E43F898E6AB91E4408A20D7501E2D2E3F4";
    const string DatSha256 = "808CB0956B5D67337CC5ADA7DB4F041A6092678751564B0F1F7790EEA0B18A20";

    [RealAmdBuildFact]
    [Trait("Category", "Slow")]
    public async Task RealAmd_Slow_BuildAndSign_ChangesOnlyInfAndDat_AndCatalogHas127Members()
    {
        var subject = TestCerts.NewSubject();
        var work = Path.Combine(Path.GetTempPath(), "lgo-realbuild-" + Guid.NewGuid().ToString("N"));
        try
        {
            var amd = AmdPackageLocator.Locate(RealPackages.AmdRoot);
            var devices = new[]
            {
                new GpuDevice(Go1 + @"\4&2F3A1B&0&0041", new[] { Go1, @"PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA" }, "32.0.23044.19", "oem55.inf", 0),
            };
            var profile = new LegionGoProfile(
                new[] { Go1, @"PCI\VEN_1002&DEV_15C8&SUBSYS_381217AA&REV_C9" },
                new[] { new GcfRecord(0x15BF, 0x04), new GcfRecord(0x15C8, 0xC9) },
                "amduw23e.inf", new Version(32, 0, 23044, 19), "u0202094.inf");
            var target = TargetResolver.Resolve(devices, profile);
            var log = new RecordingLog();

            var analysis = ConversionSteps.Analyze(amd, target, log);
            Assert.Equal("u0205499.cat", analysis.CatalogName);
            var converted = Path.Combine(work, "converted", Path.GetFileName(amd.DisplayDir));
            await ConversionSteps.BuildAsync(amd, analysis, converted, null, CancellationToken.None);

            using (var cert = SigningCertificate.GetOrCreate(subject))
            {
                var builder = new CatalogBuilder(new ProcessRunner(),
                    new ToolPaths("7z.exe", "innoextract.exe", RealToolsLocator.MakeCat, RealToolsLocator.SignTool));
                await ConversionSteps.SignAsync(builder, converted, analysis, cert, CancellationToken.None);
            }

            // ---- exactly two changed files vs the AMD source, plus the replaced catalog
            var src = Files(amd.DisplayDir);
            var dst = Files(converted);
            Assert.Equal(src.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase), dst.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase));
            var changed = new List<string>();
            foreach (var rel in src.Keys)
            {
                if (string.Equals(rel, "u0205499.cat", StringComparison.OrdinalIgnoreCase)) continue;
                if (src[rel] != dst[rel] || Sha(Path.Combine(amd.DisplayDir, rel)) != Sha(Path.Combine(converted, rel)))
                    changed.Add(rel);
            }
            Assert.Equal(new[] { @"B026716\amdgcf.dat", "u0205499.inf" }, changed.OrderBy(c => c, StringComparer.OrdinalIgnoreCase));

            var inf = Path.Combine(converted, "u0205499.inf");
            Assert.Equal(1119446, new FileInfo(inf).Length);
            Assert.Equal(InfSha256, Sha(inf));
            Assert.Contains("\"%AMD15BF.781%\" = ati2mtag_Phoenix, " + Go1 + "\r\n", File.ReadAllText(inf));
            var dat = Path.Combine(converted, "B026716", "amdgcf.dat");
            Assert.Equal(696, new FileInfo(dat).Length);
            Assert.Equal(DatSha256, Sha(dat));
            Assert.Contains(new GcfRecord(0x15BF, 0x04), AmdGcf.Read(File.ReadAllBytes(dat), amd.ReleaseString));

            // ---- the new catalog: 127 members (INF + every file in B026716), each with OSAttr and File attributes
            var cat = Path.Combine(converted, "u0205499.cat");
            Assert.NotEqual(Sha(Path.Combine(amd.DisplayDir, "u0205499.cat")), Sha(cat));
            Assert.Equal(126, Directory.GetFiles(Path.Combine(converted, "B026716")).Length);
            var dump = await CertutilDump(cat);
            Assert.Equal(255, Regex.Matches(dump, "OSAttr").Count);
            Assert.Equal(254, Regex.Matches(dump, @"\bFile\b").Count);
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(cat));
            Assert.Equal(subject, signer.Subject);
        }
        finally
        {
            TestCerts.RemoveFromCurrentUserMy(subject);
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static Dictionary<string, long> Files(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(root, f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);

    static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    static async Task<string> CertutilDump(string cat)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("certutil.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-dump"); psi.ArgumentList.Add(cat);
        using var proc = System.Diagnostics.Process.Start(psi)!;
        var text = await proc.StandardOutput.ReadToEndAsync();
        await proc.WaitForExitAsync();
        Assert.Equal(0, proc.ExitCode);
        return text;
    }
}
