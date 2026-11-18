using LegionGoConverter.App;

namespace LegionGoConverter.Core.Tests;

/// <summary>Finding 1 and 8: the newest manifest that does not load (torn by a power loss, or an export-only one without a
/// Lenovo driver path) must never hide the newest valid install manifest from the in-app Rollback.</summary>
public class SessionLocatorTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "lgc-loc-" + Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    static SessionManifest Valid(string lenovoInf = @"C:\ProgramData\LegionGoDriverConverter\lenovo-original\32.0.23044.19\u0202094.inf") => new(
        "32.0.32015.12012", "777C7D479879BCB167901701A72CABF29DD9BEB9", SigningCertificate.DefaultSubject,
        new[] { new PackageRef("u0205499.inf", "32.0.32015.12012", IsDisplay: true) }, lenovoInf,
        new[] { "oem54.inf" }, Array.Empty<string>());

    string Session(string name, string? json, DateTime writeTimeUtc)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        if (json != null)
        {
            var p = Path.Combine(dir, "manifest.json");
            File.WriteAllText(p, json);
            File.SetLastWriteTimeUtc(p, writeTimeUtc);
        }
        return dir;
    }

    string SessionWith(string name, SessionManifest m, DateTime writeTimeUtc)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        var p = Path.Combine(dir, "manifest.json");
        m.Save(p);
        File.SetLastWriteTimeUtc(p, writeTimeUtc);
        return dir;
    }

    static readonly DateTime T0 = new(2026, 11, 16, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NewestValidManifest_IsChosen()
    {
        SessionWith("a", Valid(), T0);
        var b = SessionWith("b", Valid(), T0.AddHours(1));
        Assert.Equal(b, SessionLocator.FindLatestSessionDir(_root));
    }

    [Fact]
    public void NewerTornManifest_IsSkipped_FallsBackToNewestValid()
    {
        var good = SessionWith("a", Valid(), T0);
        Session("b", "", T0.AddHours(1));          // power loss: empty file
        Session("c", "{\"Amd", T0.AddHours(2));    // power loss: truncated JSON
        Assert.Equal(good, SessionLocator.FindLatestSessionDir(_root));
    }

    [Fact]
    public void NewerExportOnlyManifest_IsSkipped_FallsBackToNewestValid()
    {
        var good = SessionWith("a", Valid(), T0);
        SessionWith("b", Valid(lenovoInf: "x") with { LenovoInfPath = "" }, T0.AddHours(1));
        Assert.Equal(good, SessionLocator.FindLatestSessionDir(_root));
    }

    [Fact]
    public void OnlyInvalidManifests_NoSession()
    {
        Session("a", "", T0);
        Session("b", null, T0);
        Assert.Null(SessionLocator.FindLatestSessionDir(_root));
    }

    [Fact]
    public void RollbackRunner_SkipsInvalidNewerManifest()
    {
        var good = SessionWith("a", Valid(), T0);
        Session("b", "", T0.AddHours(1));
        var r = new RollbackRunner(_root, _ => throw new InvalidOperationException("not used"), null!);
        Assert.True(r.HasPreviousSession);
        Assert.Equal(good, r.LatestSessionDir);
    }

    [Fact]
    public async Task RollbackRunner_UntrustedDataFolder_NoPreviousSession_AndRunRefuses()
    {
        // final review finding 3: a data folder that is not admin-only is never read
        SessionWith("a", Valid(), T0);
        var r = new RollbackRunner(_root, _ => throw new InvalidOperationException("not used"), null!,
            secureDataRoot: () => throw new DataRootSecurityException("The data folder X is owned by EVIL, not by Administrators or SYSTEM."));
        Assert.False(r.HasPreviousSession);
        Assert.Null(r.LatestSessionDir);
        var ex = await Assert.ThrowsAsync<RollbackException>(() => r.RunAsync(CancellationToken.None));
        Assert.Contains("owned by EVIL", ex.Message);
        Assert.Contains("nothing was changed", ex.Message);
    }

    [Fact]
    public void RollbackRunner_SecuresTheDataFolderBeforeLookingForSessions()
    {
        bool secured = false;
        var r = new RollbackRunner(_root, _ => throw new InvalidOperationException("not used"), null!,
            secureDataRoot: () => { secured = true; return "ok"; });
        _ = r.LatestSessionDir;
        Assert.True(secured);
    }

    [Fact]
    public void Save_LeavesNoTempFile_AndRoundTrips()
    {
        Directory.CreateDirectory(_root);
        var p = Path.Combine(_root, "manifest.json");
        Valid().Save(p);
        Valid().Save(p);
        Assert.False(File.Exists(p + ".tmp"));
        Assert.Equal(Valid().LenovoInfPath, SessionManifest.Load(p).LenovoInfPath);
    }
}
