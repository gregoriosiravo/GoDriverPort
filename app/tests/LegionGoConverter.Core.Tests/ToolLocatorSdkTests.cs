using System.Diagnostics;

namespace LegionGoConverter.Core.Tests;

[Collection("ProcessState")] // mutates the current directory and PATH in one test: must not run in parallel with other classes
public class ToolLocatorSdkTests : IDisposable
{
    readonly string _tmp = Path.Combine(Path.GetTempPath(), "lgc-loc-" + Guid.NewGuid().ToString("N"));
    string Tools => Path.Combine(_tmp, "app", "tools");

    public ToolLocatorSdkTests() { Directory.CreateDirectory(Tools); }
    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { } }

    sealed class Verifier(Func<string, string?> f) : IAuthenticodeVerifier
    {
        public List<string> Checked { get; } = new();
        public string? Check(string path) { Checked.Add(path); return f(path); }
    }
    static Verifier AcceptAll() => new(_ => null);

    static void Touch(string dir, params string[] names)
    {
        Directory.CreateDirectory(dir);
        foreach (var n in names) File.WriteAllText(Path.Combine(dir, n), "");
    }

    void Bundled(params string[] names) => Touch(Tools, names);
    string SdkBin(string root, string version) => Path.Combine(_tmp, root, "Windows Kits", "10", "bin", version, "x64");
    string Root(string root) => Path.Combine(_tmp, root, "Windows Kits", "10", "bin");

    [Fact]
    public void Sdk_IsPreferredOverBundled_ForSignToolAndMakecat()
    {
        Bundled("7z.exe", "innoextract.exe", "makecat.exe", "signtool.exe");
        Touch(SdkBin("pf", "10.0.26100.0"), "makecat.exe", "signtool.exe");
        var t = ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll());
        Assert.Equal(Path.Combine(SdkBin("pf", "10.0.26100.0"), "makecat.exe"), t.MakeCat);
        Assert.Equal(Path.Combine(SdkBin("pf", "10.0.26100.0"), "signtool.exe"), t.SignTool);
        Assert.Equal(Path.Combine(Tools, "7z.exe"), t.SevenZip);
        Assert.Equal(Path.Combine(Tools, "innoextract.exe"), t.InnoExtract);
    }

    [Fact]
    public void Bundled_IsTheFallback_WhenSdkLacksTheFile()
    {
        Bundled("7z.exe", "innoextract.exe", "makecat.exe", "signtool.exe");
        var t = ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll());
        Assert.Equal(Path.Combine(Tools, "makecat.exe"), t.MakeCat);
        Assert.Equal(Path.Combine(Tools, "signtool.exe"), t.SignTool);
    }

    [Fact]
    public void HighestVersionIsChosen_NumericNotLexical()
    {
        Bundled("7z.exe", "innoextract.exe", "signtool.exe");
        Touch(SdkBin("pf", "10.0.19041.0"), "makecat.exe");
        Touch(SdkBin("pf", "10.0.26100.0"), "makecat.exe");
        Touch(SdkBin("pf", "10.0.9000.0"), "makecat.exe"); // 9000 < 19041 numerically
        var t = ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll());
        Assert.Equal(Path.Combine(SdkBin("pf", "10.0.26100.0"), "makecat.exe"), t.MakeCat);
    }

    [Fact]
    public void HighestVersionThatActuallyContainsTheFile_IsChosen()
    {
        Bundled("7z.exe", "innoextract.exe", "signtool.exe");
        Touch(SdkBin("pf", "10.0.19041.0"), "makecat.exe");
        Touch(SdkBin("pf", "10.0.26100.0"), "signtool.exe"); // newest folder has no makecat
        var t = ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll());
        Assert.Equal(Path.Combine(SdkBin("pf", "10.0.19041.0"), "makecat.exe"), t.MakeCat);
        Assert.Equal(Path.Combine(SdkBin("pf", "10.0.26100.0"), "signtool.exe"), t.SignTool);
    }

    [Fact]
    public void BothSdkRoots_AreSearched_HighestVersionWinsAcrossThem()
    {
        Bundled("7z.exe", "innoextract.exe");
        Touch(SdkBin("x86", "10.0.19041.0"), "makecat.exe", "signtool.exe");
        Touch(SdkBin("pf", "10.0.22621.0"), "makecat.exe", "signtool.exe");
        var t = ToolLocator.Resolve(Tools, new[] { Root("x86"), Root("pf") }, AcceptAll());
        Assert.Equal(Path.Combine(SdkBin("pf", "10.0.22621.0"), "makecat.exe"), t.MakeCat);

        var only86 = ToolLocator.Resolve(Tools, new[] { Root("x86"), Root("missing-root") }, AcceptAll());
        Assert.Equal(Path.Combine(SdkBin("x86", "10.0.19041.0"), "signtool.exe"), only86.SignTool);
    }

    [Theory]
    [InlineData("x86")]
    [InlineData("zzz-latest")]
    [InlineData("10.0")]               // two parts
    [InlineData("10.0.99999999999.0")] // overflows Version
    [InlineData(" 10.0.26100.0")]      // leading whitespace
    [InlineData("10.0.26100.0.1")]     // five parts
    [InlineData("10.0.26100.x")]
    public void NonVersionFolders_AreIgnored_EvenWhenTheyHoldTheFile(string badName)
    {
        Bundled("7z.exe", "innoextract.exe", "signtool.exe");
        Touch(SdkBin("pf", badName), "makecat.exe");           // the decoy would win if the name were accepted
        Touch(SdkBin("pf", "10.0.19041.0"), "makecat.exe");
        var t = ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll());
        Assert.Equal(Path.Combine(SdkBin("pf", "10.0.19041.0"), "makecat.exe"), t.MakeCat);
    }

    [Fact]
    public void ThreePartVersionFolder_IsAccepted()
    {
        Bundled("7z.exe", "innoextract.exe", "signtool.exe");
        Touch(SdkBin("pf", "10.0.19041"), "makecat.exe");
        var t = ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll());
        Assert.Equal(Path.Combine(SdkBin("pf", "10.0.19041"), "makecat.exe"), t.MakeCat);
    }

    [Fact]
    public void UnversionedBinX64_IsAcceptedAsFallback_ButVersionedWins()
    {
        Bundled("7z.exe", "innoextract.exe", "signtool.exe");
        Touch(Path.Combine(Root("pf"), "x64"), "makecat.exe");
        Assert.Equal(Path.Combine(Root("pf"), "x64", "makecat.exe"), ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll()).MakeCat);
        Touch(SdkBin("pf", "10.0.19041.0"), "makecat.exe");
        Assert.Equal(Path.Combine(SdkBin("pf", "10.0.19041.0"), "makecat.exe"), ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll()).MakeCat);
    }

    [Fact]
    public void ExistingButEmptyRoot_MeansNotFound()
    {
        Bundled("7z.exe", "innoextract.exe", "signtool.exe");
        Directory.CreateDirectory(Root("pf"));
        var ex = Assert.Throws<ToolMissingException>(() => ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll()));
        Assert.Contains("makecat.exe", ex.Message);
    }

    [Fact]
    public void ADirectoryNamedMakecatExe_IsNotAFile()
    {
        Bundled("7z.exe", "innoextract.exe", "signtool.exe");
        Directory.CreateDirectory(Path.Combine(SdkBin("pf", "10.0.19041.0"), "makecat.exe"));
        Directory.CreateDirectory(Path.Combine(Tools, "makecat.exe"));
        Assert.Throws<ToolMissingException>(() => ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll()));
    }

    [Fact]
    public void CurrentDirectoryAndPath_AreNeverSearched()
    {
        Bundled("7z.exe", "innoextract.exe", "signtool.exe");
        var decoy = Path.Combine(_tmp, "decoy");
        Touch(decoy, "makecat.exe");
        var oldCwd = Directory.GetCurrentDirectory();
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Directory.SetCurrentDirectory(decoy);
            Environment.SetEnvironmentVariable("PATH", decoy + ";" + oldPath);
            Assert.Throws<ToolMissingException>(() => ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll()));
        }
        finally
        {
            Directory.SetCurrentDirectory(oldCwd);
            Environment.SetEnvironmentVariable("PATH", oldPath);
        }
    }

    [Fact]
    public void MissingMakecat_MessageNamesFileLocationsAndTheFix()
    {
        Bundled("7z.exe", "innoextract.exe", "signtool.exe");
        var ex = Assert.Throws<ToolMissingException>(() => ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll()));
        Assert.Contains("makecat.exe", ex.Message);
        Assert.Contains(Tools, ex.Message);
        Assert.Contains(Root("pf"), ex.Message);
        Assert.Contains("Signing Tools for Desktop Apps", ex.Message);
        Assert.Contains("winget install --interactive Microsoft.WindowsSDK.10.0.28000", ex.Message);
        Assert.Contains("https://developer.microsoft.com/windows/downloads/windows-sdk/", ex.Message);
        Assert.Contains("label may vary", ex.Message);
        Assert.Contains("restart", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MissingSignTool_MessageAlsoGivesSdkInstructions()
    {
        Bundled("7z.exe", "innoextract.exe", "makecat.exe");
        var ex = Assert.Throws<ToolMissingException>(() => ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll()).SignTool);
        Assert.Contains("signtool.exe", ex.Message);
    }

    [Fact]
    public void Missing7z_MessageDoesNotSuggestTheSdk_AndSdkIsNeverUsedForIt()
    {
        Bundled("innoextract.exe", "makecat.exe", "signtool.exe");
        Touch(SdkBin("pf", "10.0.19041.0"), "7z.exe");
        var ex = Assert.Throws<ToolMissingException>(() => ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll()));
        Assert.Contains("7z.exe", ex.Message);
        Assert.DoesNotContain("winget install", ex.Message);
    }

    [Fact]
    public void ResolvedPaths_AreAbsolute()
    {
        Bundled("7z.exe", "innoextract.exe");
        Touch(SdkBin("pf", "10.0.19041.0"), "makecat.exe", "signtool.exe");
        var t = ToolLocator.Resolve(Tools, new[] { Root("pf") }, AcceptAll());
        Assert.True(Path.IsPathFullyQualified(t.MakeCat));
        Assert.True(Path.IsPathFullyQualified(t.SignTool));
    }

    [Fact]
    public void UntrustedSdkFile_FailsClearly_AndIsNotSilentlyReplacedByTheBundledCopy()
    {
        Bundled("7z.exe", "innoextract.exe", "makecat.exe", "signtool.exe");
        Touch(SdkBin("pf", "10.0.26100.0"), "makecat.exe", "signtool.exe");
        var tampered = Path.Combine(SdkBin("pf", "10.0.26100.0"), "makecat.exe");
        var v = new Verifier(p => p == tampered ? "the signature is invalid" : null);
        var ex = Assert.Throws<ToolMissingException>(() => ToolLocator.Resolve(Tools, new[] { Root("pf") }, v));
        Assert.Contains("makecat.exe", ex.Message);
        Assert.Contains(tampered, ex.Message);
        Assert.Contains("the signature is invalid", ex.Message);
    }

    [Fact]
    public void ValidSignature_ViaInjectedVerifier_IsAccepted_AndOnlySignToolMakecatAreChecked()
    {
        Bundled("7z.exe", "innoextract.exe");
        Touch(SdkBin("pf", "10.0.26100.0"), "makecat.exe", "signtool.exe");
        var v = AcceptAll();
        ToolLocator.Resolve(Tools, new[] { Root("pf") }, v);
        Assert.Equal(2, v.Checked.Count);
        Assert.All(v.Checked, p => Assert.Contains("10.0.26100.0", p));
    }

    [Fact]
    public void BundledFallback_IsAlsoVerified()
    {
        Bundled("7z.exe", "innoextract.exe", "makecat.exe", "signtool.exe");
        var v = new Verifier(_ => "unsigned");
        var ex = Assert.Throws<ToolMissingException>(() => ToolLocator.Resolve(Tools, new[] { Root("pf") }, v));
        Assert.Contains("unsigned", ex.Message);
    }

    // ---- the real WinVerifyTrust verifier (uses signtool.exe from LGO_TOOLS_DIR or the developer's installed Windows SDK; skipped if absent) ----
    internal static string? RealSignTool()
    {
        var dir = Environment.GetEnvironmentVariable("LGO_TOOLS_DIR");
        if (!string.IsNullOrWhiteSpace(dir) && File.Exists(Path.Combine(dir, "signtool.exe"))) return Path.Combine(dir, "signtool.exe");
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            var bin = Path.Combine(pf, "Windows Kits", "10", "bin");
            if (!Directory.Exists(bin)) continue;
            foreach (var v in Directory.GetDirectories(bin).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var p = Path.Combine(v, "x64", "signtool.exe");
                if (File.Exists(p)) return p;
            }
        }
        return null;
    }

    [RealSignToolFact]
    public void RealVerifier_AcceptsMicrosoftSignedSignTool()
    {
        var p = RealSignTool()!;
        Assert.Null(new WinTrustMicrosoftVerifier().Check(p));
    }

    [Fact]
    public void RealVerifier_RejectsAnUnsignedFile()
    {
        var f = Path.Combine(_tmp, "fake.exe");
        File.WriteAllText(f, "not a PE");
        var reason = new WinTrustMicrosoftVerifier().Check(f);
        Assert.NotNull(reason);
        Assert.Contains("WinVerifyTrust 0x", reason);
        Assert.Equal("the file does not exist", new WinTrustMicrosoftVerifier().Check(Path.Combine(_tmp, "missing.exe")));
    }

    [RealSignToolFact]
    public void RealVerifier_RejectsATamperedCopy_WithBadDigest()
    {
        var p = RealSignTool()!;
        var copy = Path.Combine(_tmp, "signtool.exe");
        var bytes = File.ReadAllBytes(p);
        bytes[bytes.Length / 4] ^= 0xFF; // inside the signed body, not the certificate table
        File.WriteAllBytes(copy, bytes);
        var reason = new WinTrustMicrosoftVerifier().Check(copy);
        Assert.NotNull(reason);
        Assert.Contains("0x80096010", reason);
        Assert.Contains("TRUST_E_BAD_DIGEST", reason);
    }

    public static IEnumerable<object[]> Throwers()
    {
        yield return new object[] { new DllNotFoundException("wintrust.dll") };
        yield return new object[] { new EntryPointNotFoundException("WinVerifyTrust") };
        yield return new object[] { new UnauthorizedAccessException("denied") };
        yield return new object[] { new ArgumentException("path too long") };
        yield return new object[] { new PathTooLongException() };
        yield return new object[] { new System.ComponentModel.Win32Exception(5) };
        yield return new object[] { new InvalidOperationException("boom") };
    }

    [Theory, MemberData(nameof(Throwers))]
    public void VerifierThatThrows_FailsClosed_AsToolMissing(Exception thrown)
    {
        Bundled("7z.exe", "innoextract.exe", "makecat.exe", "signtool.exe");
        var v = new Verifier(_ => throw thrown);
        var ex = Assert.Throws<ToolMissingException>(() => ToolLocator.Resolve(Tools, new[] { Root("pf") }, v));
        Assert.Contains("makecat.exe", ex.Message);
        Assert.Contains(Path.Combine(Tools, "makecat.exe"), ex.Message);
        Assert.Contains(thrown.GetType().Name, ex.Message);
    }

    [Fact]
    public void OperationCanceled_IsNotSwallowed()
    {
        Bundled("7z.exe", "innoextract.exe", "makecat.exe", "signtool.exe");
        var v = new Verifier(_ => throw new OperationCanceledException());
        Assert.Throws<OperationCanceledException>(() => ToolLocator.Resolve(Tools, new[] { Root("pf") }, v));
    }
}

internal sealed class AcceptAllVerifier : IAuthenticodeVerifier
{
    public string? Check(string path) => null;
}

[CollectionDefinition("ProcessState", DisableParallelization = true)]
public sealed class ProcessStateCollection { }

/// <summary>A fact that is reported as skipped when no signtool.exe is found (LGO_TOOLS_DIR or the installed Windows SDK).</summary>
public sealed class RealSignToolFactAttribute : FactAttribute
{
    public RealSignToolFactAttribute()
    {
        if (ToolLocatorSdkTests.RealSignTool() is null)
            Skip = @"signtool.exe not found (set LGO_TOOLS_DIR or install the Windows SDK signing tools)";
    }
}
