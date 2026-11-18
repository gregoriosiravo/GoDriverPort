using LegionGoConverter.Core;
using System.Windows.Threading;
using LegionGoConverter.App;

namespace LegionGoConverter.Core.Tests;

public class FailureLogTests
{
    [Fact]
    public void LogsEachDistinctMessageOnce_AndCountsAll()
    {
        var lines = new List<string>();
        var log = new FailureLog(lines.Add);
        for (int i = 0; i < 100; i++) log.Report("same");
        log.Report("other");
        Assert.Equal(new[] { "same", "other" }, lines);
        Assert.Equal(101, log.Count);
    }

    [Fact]
    public void ThrowingSink_DoesNotThrow()
    {
        var log = new FailureLog(_ => throw new InvalidOperationException());
        log.Report("x");
        Assert.Equal(1, log.Count);
    }
}

public class GamepadPollTests
{
    static readonly GamepadState Idle = new(false, false, false, false, 0, 0, false, false);
    static readonly GamepadState PressA = Idle with { A = true };

    static GamepadNavigator Nav(Func<bool> active, List<string> log) =>
        new(Dispatcher.CurrentDispatcher, () => { }, active, log.Add);

    [Fact]
    public void Inactive_ReadsNothingAndAppliesNothing()
    {
        var nav = Nav(() => false, new());
        int reads = 0; var applied = new List<NavIntent>();
        nav.Poll(() => { reads++; return PressA; }, applied.Add);
        Assert.Equal(0, reads); Assert.Empty(applied);
    }

    [Fact]
    public void HeldButton_DoesNotFireOnReactivation()
    {
        bool active = true; var nav = Nav(() => active, new()); var applied = new List<NavIntent>();
        nav.Poll(() => Idle, applied.Add);          // first active poll primes
        active = false; nav.Poll(() => PressA, applied.Add);
        active = true; nav.Poll(() => PressA, applied.Add);   // A still held: re-synced, no Activate
        Assert.Empty(applied);
        nav.Poll(() => Idle, applied.Add);
        nav.Poll(() => PressA, applied.Add);
        Assert.Equal(NavIntentKind.Activate, Assert.Single(applied).Kind);
    }

    [Fact]
    public void FailingApply_IsLoggedOnce_CountedAndNotThrown()
    {
        var log = new List<string>(); var nav = Nav(() => true, log);
        nav.Poll(() => Idle, _ => { });
        for (int i = 0; i < 5; i++)
        {
            nav.Poll(() => Idle, _ => { });
            nav.Poll(() => PressA, _ => throw new InvalidOperationException("boom"));
        }
        Assert.Equal(5, nav.FailureCount);
        Assert.Single(log);
        Assert.Contains("boom", log[0]);
    }

    [Fact]
    public void FailingRead_IsLogged()
    {
        var log = new List<string>(); var nav = Nav(() => true, log);
        nav.Poll(() => throw new IOException("gone"), _ => { });
        Assert.Equal(1, nav.FailureCount); Assert.Contains("gone", log[0]);
    }
}

public class StartupGuardTests
{
    [Fact]
    public void Success_ReturnsZero_NothingShown()
    {
        var shown = new List<string>();
        Assert.Equal(0, StartupGuard.Run(() => { }, shown.Add, (_, _) => { }));
        Assert.Empty(shown);
    }

    [Fact]
    public void Failure_ShowsMessageWithErrorAndLogLocation_LogsAndReturnsOne()
    {
        var shown = new List<string>(); var logged = new List<(string, Exception)>();
        int code = StartupGuard.Run(() => throw new InvalidOperationException("kaput"), shown.Add, (p, e) => logged.Add((p, e)));
        Assert.Equal(1, code);
        Assert.Contains("kaput", Assert.Single(shown));
        Assert.Contains(StartupGuard.LogPath, shown[0]);
        Assert.Equal(StartupGuard.LogPath, Assert.Single(logged).Item1);
    }

    [Fact]
    public void WriteLogFile_SecuresTheFolderFirst_AndWritesNothingWhenThatFails()
    {
        // final review finding 3: the startup log is written only into a protected data folder
        var dir = Path.Combine(Path.GetTempPath(), "lgc-sg-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "startup-error.log");
        try
        {
            Assert.Throws<DataRootSecurityException>(() =>
                StartupGuard.WriteLogFile(path, new Exception("x"), () => throw new DataRootSecurityException("owned by someone")));
            Assert.False(File.Exists(path));

            var secured = false;
            StartupGuard.WriteLogFile(path, new Exception("boom"), () => { secured = true; Directory.CreateDirectory(dir); return "ok"; });
            Assert.True(secured);
            Assert.Contains("boom", File.ReadAllText(path));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void FailingLogAndMessage_StillReturnsOne()
    {
        Assert.Equal(1, StartupGuard.Run(() => throw new Exception("x"), _ => throw new Exception(), (_, _) => throw new Exception()));
    }
}

public class DeviceInfoProviderTests
{
    sealed class Probe(Func<IReadOnlyList<GpuDevice>> f) : IDeviceProbe { public IReadOnlyList<GpuDevice> GetAmdDisplayDevices() => f(); }
    sealed class Env(Func<SecurityState> f) : IEnvironmentProbe
    {
        public bool IsAdministrator() => false; public long FreeBytes(string path) => 0; public SecurityState GetSecurityState() => f();
    }
    static readonly SecurityState Sec = new(true, false, false);
    static GpuDevice Dev(string id) => new(@"PCI\" + id, new[] { id, "second" }, "1", "oem1.inf", 0);

    [Fact]
    public void ProbeThrows_ReturnsErrorMessage()
    {
        var i = new DeviceInfoProvider(new Probe(() => throw new DeviceProbeException("wmi down", new Exception())), new Env(() => Sec)).Get();
        Assert.Null(i.HardwareId); Assert.Contains("wmi down", i.Error); Assert.Equal(Sec, i.Security);
    }

    [Fact]
    public void EnvironmentThrows_ReturnsErrorMessage()
    {
        var i = new DeviceInfoProvider(new Probe(() => new[] { Dev("A") }), new Env(() => throw new InvalidOperationException("no sb"))).Get();
        Assert.Contains("no sb", i.Error); Assert.Null(i.HardwareId);
        Assert.Null(i.Security); // final review finding 12: unknown (the review screen shows "Unknown"), never "off"
    }

    [Fact]
    public void NoDevice_ClearMessage()
    {
        var i = new DeviceInfoProvider(new Probe(() => Array.Empty<GpuDevice>()), new Env(() => Sec)).Get();
        Assert.Null(i.HardwareId); Assert.Equal("No AMD display device was found.", i.Error);
    }

    [Fact]
    public void MultipleDevices_CurrentBehaviorUsesFirstDevicesFirstHardwareId()
    {
        var i = new DeviceInfoProvider(new Probe(() => new[] { Dev("FIRST"), Dev("SECOND") }), new Env(() => Sec)).Get();
        Assert.Equal("FIRST", i.HardwareId); Assert.Null(i.Error);
    }
}

public class AppCompositionTests
{
    [Fact]
    public void MissingTools_WarnsWithFileAndFolder_AndDoesNotThrow()
    {
        // temp data root, no protection call: the real C:\ProgramData\LegionGoDriverConverter is never touched by tests
        var c = new AppComposition(Dispatcher.CurrentDispatcher, () => ToolLocator.Resolve(@"C:\definitely\missing\tools"),
            Path.Combine(Path.GetTempPath(), "lgc-comp-" + Guid.NewGuid().ToString("N")), null);
        Assert.Contains("7z.exe", c.ViewModel.StartupWarning);
        Assert.Contains(@"C:\definitely\missing\tools", c.ViewModel.StartupWarning);
        Assert.Contains("rolling back", c.ViewModel.StartupWarning);
        // finding 4: the test binaries live in a user-writable folder, so the location warning is shown too
        Assert.Contains(InstallLocationCheck.RecommendedFolder, c.ViewModel.StartupWarning);
    }

    [Fact]
    public void MissingMakecat_WarningShowsSdkInstallInstructions_AndRollbackStillOffered()
    {
        var root = Path.Combine(Path.GetTempPath(), "lgc-comp-" + Guid.NewGuid().ToString("N"));
        var tools = Path.Combine(root, "tools");
        Directory.CreateDirectory(tools);
        foreach (var n in new[] { "7z.exe", "innoextract.exe", "signtool.exe" }) File.WriteAllText(Path.Combine(tools, n), "");
        try
        {
            var c = new AppComposition(Dispatcher.CurrentDispatcher, () => ToolLocator.Resolve(tools, new[] { Path.Combine(root, "no-sdk") }, new AcceptAllVerifier()),
                Path.Combine(root, "data"), null);
            var w = c.ViewModel.StartupWarning!;
            Assert.Contains("makecat.exe", w);
            Assert.Contains("winget install --interactive Microsoft.WindowsSDK.10.0.28000", w);
            Assert.Contains("Signing Tools for Desktop Apps", w);
            Assert.DoesNotContain("Reinstall the app", w);
            Assert.Contains("rolling back", w);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task UnavailableRunner_ReportsProblemWhenStarted()
    {
        var r = await new UnavailableConversionRunner("tools missing").RunAsync(null!, null!, null!, default);
        Assert.False(r.Success); Assert.Equal("tools missing", r.Error);
    }
}

public class RollbackRunnerTests
{
    [Fact]
    public async Task NoSessions_HasNoPreviousSession_AndRunThrowsClearly()
    {
        var root = Path.Combine(Path.GetTempPath(), "lgc-" + Guid.NewGuid().ToString("N"));
        var r = new RollbackRunner(root, _ => throw new InvalidOperationException("must not be built"), null!);
        Assert.False(r.HasPreviousSession);
        var ex = await Assert.ThrowsAsync<RollbackException>(() => r.RunAsync(default));
        Assert.Contains("no earlier conversion", ex.Message);
    }
}
