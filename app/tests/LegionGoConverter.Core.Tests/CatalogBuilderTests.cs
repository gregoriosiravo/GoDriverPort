using System.Security.Cryptography.X509Certificates;
using LegionGoConverter.Core;

namespace LegionGoConverter.Core.Tests;

public static class RealToolsLocator
{
    public static string Dir { get; } =
        Environment.GetEnvironmentVariable("LGO_TOOLS_DIR") ?? "";
    public static string MakeCat => Path.Combine(Dir, "makecat.exe");
    public static string SignTool => Path.Combine(Dir, "signtool.exe");
}

/// <summary>A fact skipped when real makecat/signtool are absent.</summary>
public sealed class RealSigningToolsFactAttribute : FactAttribute
{
    public RealSigningToolsFactAttribute()
    {
        if (string.IsNullOrEmpty(RealToolsLocator.Dir) || !File.Exists(RealToolsLocator.MakeCat) || !File.Exists(RealToolsLocator.SignTool))
            Skip = $"makecat/signtool not found (set LGO_TOOLS_DIR to a folder with both): '{RealToolsLocator.Dir}'";
    }
}

public class CatalogBuilderTests
{
    [Fact]
    public void CdfWriter_ContainsAttributesForEveryMember()
    {
        var cdf = CdfWriter.Build("a.cat", @"C:\out", new[] { @"C:\out\a.inf", @"C:\out\bin\x.dll", @"C:\out\bin\y.sys" });
        var lines = cdf.Split(new[] { "\r\n" }, StringSplitOptions.None);
        Assert.Equal("[CatalogHeader]", lines[0]);
        Assert.Contains("Name=a.cat", lines);
        Assert.Contains(@"ResultDir=C:\out", lines);
        Assert.Contains("PublicVersion=0x0000001", lines);
        Assert.Contains("CatalogVersion=2", lines);
        Assert.Contains("HashAlgorithms=SHA256", lines);
        Assert.Contains("EncodingType=0x00010001", lines);
        Assert.Contains("CATATTR1=0x10010001:OSAttr:2:10.0", lines);
        Assert.Contains("[CatalogFiles]", lines);
        foreach (var (name, path) in new[] { ("a.inf", @"C:\out\a.inf"), ("x.dll", @"C:\out\bin\x.dll"), ("y.sys", @"C:\out\bin\y.sys") })
        {
            Assert.Contains($"<hash>{name}={path}", lines);
            Assert.Contains($"<hash>{name}ATTR1=0x10010001:OSAttr:2:10.0", lines);
            Assert.Contains($"<hash>{name}ATTR2=0x10010001:File:{name}", lines);
        }
    }

    [Fact]
    public void CdfWriter_DuplicateFileNames_Throws() =>
        Assert.Throws<ArgumentException>(() => CdfWriter.Build("a.cat", @"C:\o", new[] { @"C:\o\a\x.dll", @"C:\o\b\x.dll" }));

    [Fact]
    public void CdfWriter_UnrepresentablePath_Throws() =>
        Assert.Throws<ArgumentException>(() => CdfWriter.Build("a.cat", @"C:\o", new[] { "C:\\o\\\U0001F600.dll" }));

    sealed class RecordingRunner : IProcessRunner
    {
        public List<(string Exe, string[] Args)> Calls = new();
        public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? lines, CancellationToken ct)
        {
            Calls.Add((exe, args.ToArray()));
            return Task.FromResult(new ProcessResult(7, "boom"));
        }
    }

    [Fact]
    public async Task BuildAndSign_MakecatFailure_ThrowsWithExitCode()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lgo-cat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "x.inf"), "; x");
            File.WriteAllBytes(Path.Combine(dir, "bin", "a.dll"), new byte[] { 1 });
            var runner = new RecordingRunner();
            var b = new CatalogBuilder(runner, new ToolPaths("7z", "ino", "makecat.exe", "signtool.exe"));
            using var cert = Ephemeral();
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                b.BuildAndSignAsync(dir, "x.inf", Path.Combine(dir, "bin"), "x.cat", cert, CancellationToken.None));
            Assert.Contains("7", ex.Message);
            Assert.Single(runner.Calls); // signtool never ran
            Assert.Equal("-v", runner.Calls[0].Args[0]);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData(@"C:\o\a=b.dll")]
    [InlineData(@"C:\o\a b.dll")]
    [InlineData("C:\\o\\a\nb.dll")]
    public void CdfWriter_BadMemberName_Throws(string path)
    {
        var ex = Assert.Throws<ArgumentException>(() => CdfWriter.Build("a.cat", @"C:\o", new[] { path }));
        Assert.Contains(Path.GetFileName(path), ex.Message);
    }

    [Fact]
    public void CdfWriter_AnsiRepresentablePath_IsAcceptedAndEncodedInAnsi()
    {
        // e-acute exists in the common Western ANSI code pages; only assert on code page 1252
        var cp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.ANSICodePage;
        if (cp != 1252) return;
        var path = "C:\\Users\\Ren\u00e9\\a.dll";
        var cdf = CdfWriter.Build("a.cat", @"C:\o", new[] { path });
        var bytes = CdfWriter.Encode(cdf);
        Assert.Contains((byte)0xE9, bytes);
        Assert.Equal(cdf, CdfWriter.AnsiEncoding.GetString(bytes));
    }

    [Fact]
    public void CdfWriter_UnrepresentablePath_NamesPath()
    {
        var path = "C:\\o\\\U0001F600\\a.dll";
        var ex = Assert.Throws<ArgumentException>(() => CdfWriter.Build("a.cat", @"C:\o", new[] { path }));
        Assert.Contains(path, ex.Message);
        Assert.Contains("work folder", ex.Message);
    }

    sealed class ScriptedRunner(string displayDir, string catName, Func<string, ProcessResult> signtool) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? lines, CancellationToken ct)
        {
            if (exe == "makecat.exe")
            {
                File.WriteAllBytes(Path.Combine(displayDir, catName), new byte[] { 1 });
                return Task.FromResult(new ProcessResult(0, "ok"));
            }
            return Task.FromResult(signtool(exe));
        }
    }

    static async Task WithTree(Func<string, string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "lgo-cat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "bin"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "x.inf"), "; x");
            File.WriteAllBytes(Path.Combine(dir, "bin", "a.dll"), new byte[] { 1 });
            await body(dir, Path.Combine(dir, "bin"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public Task BuildAndSign_SigntoolFails_RemovesCatAndReportsOutput() => WithTree(async (dir, bin) =>
    {
        var runner = new ScriptedRunner(dir, "x.cat", _ => new ProcessResult(1, "SignTool Error: no cert"));
        var b = new CatalogBuilder(runner, new ToolPaths("7z", "ino", "makecat.exe", "signtool.exe"));
        using var cert = Ephemeral();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            b.BuildAndSignAsync(dir, "x.inf", bin, "x.cat", cert, CancellationToken.None));
        Assert.Contains("SignTool Error: no cert", ex.Message);
        Assert.False(File.Exists(Path.Combine(dir, "x.cat")));
    });

    [Fact]
    public Task BuildAndSign_CancelledDuringSigntool_RemovesCatAndPropagates() => WithTree(async (dir, bin) =>
    {
        var runner = new ScriptedRunner(dir, "x.cat", _ => throw new OperationCanceledException());
        var b = new CatalogBuilder(runner, new ToolPaths("7z", "ino", "makecat.exe", "signtool.exe"));
        using var cert = Ephemeral();
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            b.BuildAndSignAsync(dir, "x.inf", bin, "x.cat", cert, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(dir, "x.cat")));
    });

    static X509Certificate2 Ephemeral()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new CertificateRequest("CN=eph", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [RealSigningToolsFact]
    public async Task RealTools_SyntheticTree_ProducesSignedCatalog()
    {
        var subject = TestCerts.NewSubject();
        var dir = Path.Combine(Path.GetTempPath(), "lgo-cat-" + Guid.NewGuid().ToString("N"));
        var bin = Path.Combine(dir, "B1");
        Directory.CreateDirectory(bin);
        try
        {
            File.WriteAllText(Path.Combine(dir, "x.inf"), "; fake inf\r\n");
            File.WriteAllBytes(Path.Combine(bin, "a.dll"), new byte[] { 1, 2, 3 });
            File.WriteAllBytes(Path.Combine(bin, "b.sys"), new byte[] { 4, 5, 6, 7 });
            File.WriteAllBytes(Path.Combine(bin, "c.bin"), new byte[] { 8 });
            const int members = 4;

            using var cert = SigningCertificate.GetOrCreate(subject);
            var runner = new ProcessRunner();
            var b = new CatalogBuilder(runner, new ToolPaths("7z", "ino", RealToolsLocator.MakeCat, RealToolsLocator.SignTool));
            await b.BuildAndSignAsync(dir, "x.inf", bin, "x.cat", cert, CancellationToken.None);

            var cat = Path.Combine(dir, "x.cat");
            Assert.True(File.Exists(cat));
            var psi = new System.Diagnostics.ProcessStartInfo("certutil.exe") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-dump"); psi.ArgumentList.Add(cat);
            using var proc = System.Diagnostics.Process.Start(psi)!;
            var text = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            Assert.Equal(members * 2 + 1, System.Text.RegularExpressions.Regex.Matches(text, "OSAttr").Count);
            Assert.Equal(members * 2, System.Text.RegularExpressions.Regex.Matches(text, @"\bFile\b").Count);

            using var signed = new X509Certificate2(X509Certificate.CreateFromSignedFile(cat));
            Assert.Equal(subject, signed.Subject);
        }
        finally
        {
            TestCerts.RemoveFromCurrentUserMy(subject);
            Directory.Delete(dir, true);
        }
    }
}
