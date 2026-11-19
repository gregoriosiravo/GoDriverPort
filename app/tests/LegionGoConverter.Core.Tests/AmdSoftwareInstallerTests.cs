namespace LegionGoConverter.Core.Tests;

public class AmdSoftwareInstallerTests
{
    const string SettingsCode = "{5E5E4309-86A8-4B2A-84E1-A686D2B185EA}";
    const string DvrCode = "{9CE31951-23D9-40F7-B738-63B49B9B552A}";
    const string SettingsUpgrade = "{A837E2B7-5BA6-4286-BC21-A002FCDD9A85}";
    const string OldSettingsCode = "{11111111-2222-3333-4444-555555555555}";
    const string SevenZip = @"C:\tools\7z.exe";
    const string AmdSubject = "CN=Advanced Micro Devices, O=Advanced Micro Devices, S=California, C=US";

    static readonly ToolPaths Tools = new(SevenZip, "inno.exe", "makecat.exe", "signtool.exe");

    sealed class FakeQuery : IMsiProductQuery
    {
        public HashSet<string> Installed { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<(string Code, string Version)>> Related { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool IsInstalled(string productCode) => Installed.Contains(productCode);
        public IReadOnlyList<(string ProductCode, string Version)> RelatedProducts(string upgradeCode) =>
            Related.TryGetValue(upgradeCode, out var l) ? l.ToArray() : Array.Empty<(string, string)>();
    }

    sealed class Rig : IDisposable
    {
        public string Work = Path.Combine(Path.GetTempPath(), "lgo amd ü " + Guid.NewGuid().ToString("N"));
        public QueueRunner Runner = new();
        public FakeQuery Query = new();
        public RecordingLog Log = new();
        public string SigStatus = "Valid";
        public string SigSubject = AmdSubject;
        public string StoreCount = "0";
        public Func<string, int> MsiExit = _ => 0;
        public bool CreateMsis = true;
        public int UninstallExit = 0;
        public string SettingsVersion = "2026.1023.2327.2112";
        public string CccPath = Path.Combine(Path.GetTempPath(), "lgo ccc " + Guid.NewGuid().ToString("N") + ".exe");

        public Rig()
        {
            Directory.CreateDirectory(Work);
            Log.Path = Path.Combine(Work, "session.log");
            File.WriteAllText(CccPath, "x");
            Runner.Respond = (exe, args) =>
            {
                if (exe == SevenZip)
                {
                    if (CreateMsis)
                    {
                        Directory.CreateDirectory(Path.Combine(Work, @"CN\cnext\cnext64"));
                        Directory.CreateDirectory(Path.Combine(Work, @"CN\amddvr\amddvr64"));
                        File.WriteAllText(Path.Combine(Work, @"CN\cnext\cnext64\ccc-next64.msi"), "x");
                        File.WriteAllText(Path.Combine(Work, @"CN\amddvr\amddvr64\amddvr64.msi"), "x");
                    }
                    return (0, "");
                }
                if (exe.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase))
                {
                    var script = args[^1];
                    if (script.Contains("Get-AuthenticodeSignature")) return (0, $"{SigStatus}|{SigSubject}");
                    if (script.Contains("Remove-AppxPackage")) return (0, "");
                    if (script.Contains("Get-AppxPackage")) return (0, StoreCount);
                }
                if (exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase))
                {
                    if (args[0] == "/x")
                    {
                        if (UninstallExit is 0 or 1605 or 1641 or 3010)
                        {
                            foreach (var l in Query.Related.Values) l.RemoveAll(r => r.Code == args[1]);
                            Query.Installed.Remove(args[1]);
                        }
                        return (UninstallExit, "");
                    }
                    var msi = args[1];
                    var code = msi.Contains("amddvr") ? 1 : 0;
                    var exit = MsiExit(msi);
                    if (exit is 0 or 1641 or 3010) Query.Installed.Add(code == 1 ? DvrCode : SettingsCode);
                    return (exit, "");
                }
                return (0, "");
            };
        }

        public AmdSoftwareInstaller Make() => new(Runner, Tools, Log, Query, p =>
            p.Contains("amddvr") ? new MsiInfo("AMD DVR", "26.20.26296.2323", DvrCode)
                                 : new MsiInfo("AMD Settings", SettingsVersion, SettingsCode, SettingsUpgrade));

        public IEnumerable<string> Msiexec => Runner.Calls.Where(c => c.Exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase)).Select(c => string.Join(" ", c.Args));
        public bool RemovedStoreApp => Runner.Calls.Any(c => c.Args.Any(a => a.Contains("Remove-AppxPackage")));
        public void Dispose() { try { Directory.Delete(Work, true); } catch { } try { File.Delete(CccPath); } catch { } }
    }

    [Fact]
    public async Task Install_UnsignedMsi_Throws_NothingRemoved()
    {
        using var rig = new Rig { SigStatus = "NotSigned", SigSubject = "" };
        await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None));
        Assert.False(rig.RemovedStoreApp);
        Assert.Empty(rig.Msiexec);
    }

    [Fact]
    public async Task Install_WrongSigner_Throws_NothingRemoved()
    {
        using var rig = new Rig { SigSubject = "CN=Advanced Micro Devices Evil Ltd" };
        await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None));
        Assert.False(rig.RemovedStoreApp);
        Assert.Empty(rig.Msiexec);
    }

    [Fact]
    public async Task Install_StoreAppStillRegistered_StopsBeforeMsi()
    {
        using var rig = new Rig { StoreCount = "1" };
        var ex = await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None));
        Assert.Contains("still registered", ex.Message);
        Assert.True(rig.RemovedStoreApp);
        Assert.Empty(rig.Msiexec);
    }

    [Fact]
    public async Task Install_ProductAlreadyInstalled_SkipsIt()
    {
        using var rig = new Rig();
        rig.Query.Installed.Add(SettingsCode);
        var codes = await rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None);
        Assert.Equal(new[] { SettingsCode, DvrCode }, codes);
        var only = Assert.Single(rig.Msiexec);
        Assert.Contains("amddvr64.msi", only);
    }

    // Real failure (2026-11-19): AMD Settings 2026.1023 installed, the package's 2026.1109 has a new ProductCode and the same
    // UpgradeCode. AMD's versions overflow Windows Installer's 255 limit per field, so msiexec /i reports "a newer version is
    // already installed" (1603). The older product must be uninstalled first, as AMD's own setup does.
    [Fact]
    public async Task Install_OlderProductWithSameUpgradeCode_IsUninstalledFirst()
    {
        using var rig = new Rig { SettingsVersion = "2026.1109.2249.2112" };
        rig.Query.Installed.Add(OldSettingsCode);
        rig.Query.Related[SettingsUpgrade] = new() { (OldSettingsCode, "2026.1023.2327.2112") };
        var r = await rig.Make().InstallDetailedAsync(rig.CccPath, rig.Work, CancellationToken.None);
        var calls = rig.Msiexec.ToList();
        Assert.Equal(3, calls.Count);
        Assert.StartsWith("/x " + OldSettingsCode, calls[0]);
        Assert.Contains("ccc-next64.msi", calls[1]);
        Assert.Contains("amddvr64.msi", calls[2]);
        Assert.Equal(new[] { SettingsCode, DvrCode }, r.InstalledByThisRun);
        Assert.DoesNotContain(OldSettingsCode, r.ProductCodes);
        Assert.Contains(rig.Log.Lines, l => l.Contains(OldSettingsCode) && l.Contains("older"));
    }

    [Fact]
    public async Task Install_NewerProductWithSameUpgradeCode_IsKept_NotDowngraded()
    {
        using var rig = new Rig { SettingsVersion = "2026.1023.2327.2112" };
        rig.Query.Installed.Add(OldSettingsCode);
        rig.Query.Related[SettingsUpgrade] = new() { (OldSettingsCode, "2026.1109.2249.2112") };
        var r = await rig.Make().InstallDetailedAsync(rig.CccPath, rig.Work, CancellationToken.None);
        var only = Assert.Single(rig.Msiexec);
        Assert.Contains("amddvr64.msi", only);
        Assert.Contains(OldSettingsCode, r.AlreadyPresent);
        Assert.Contains(OldSettingsCode, r.ProductCodes);
        Assert.DoesNotContain(OldSettingsCode, r.InstalledByThisRun);
    }

    [Fact]
    public async Task Install_UninstallOfOlderProductFails_StopsBeforeInstalling()
    {
        using var rig = new Rig { SettingsVersion = "2026.1109.2249.2112", UninstallExit = 1603 };
        rig.Query.Installed.Add(OldSettingsCode);
        rig.Query.Related[SettingsUpgrade] = new() { (OldSettingsCode, "2026.1023.2327.2112") };
        var ex = await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().InstallDetailedAsync(rig.CccPath, rig.Work, CancellationToken.None));
        Assert.Contains(OldSettingsCode, ex.Message);
        Assert.Single(rig.Msiexec);
        Assert.Empty(ex.InstalledProductCodes);
    }

    [Theory]
    [InlineData("2026.1109.2249.2112", "2026.1023.2327.2112", 1)]
    [InlineData("26.20.26296.2323", "26.20.26313.2245", -1)]
    [InlineData("1.2", "1.2.0", 0)]
    [InlineData("10.0", "9.99", 1)]
    public void CompareVersions_IsNumericPerField(string a, string b, int expected)
        => Assert.Equal(expected, Math.Sign(AmdSoftwareInstaller.CompareVersions(a, b)));

    [Fact]
    public async Task Install_ExactArguments_InOrder_AndExtractsOnlyTheTwoFolders()
    {
        using var rig = new Rig();
        var r = await rig.Make().InstallDetailedAsync(rig.CccPath, rig.Work, CancellationToken.None);
        Assert.False(r.RebootRequired);
        Assert.Equal(new[] { SettingsCode, DvrCode }, r.ProductCodes);

        var seven = rig.Runner.Calls.First(c => c.Exe == SevenZip);
        Assert.Contains(@"CN\cnext\cnext64\*", seven.Args);
        Assert.Contains(@"CN\amddvr\amddvr64\*", seven.Args);
        Assert.Contains("-o" + rig.Work, seven.Args);
        Assert.Contains(rig.CccPath, seven.Args);

        var msi = rig.Runner.Calls.Where(c => c.Exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal(2, msi.Count);
        var settingsMsi = Path.Combine(rig.Work, @"CN\cnext\cnext64\ccc-next64.msi");
        var dvrMsi = Path.Combine(rig.Work, @"CN\amddvr\amddvr64\amddvr64.msi");
        Assert.Equal(new[] { "/i", settingsMsi, "/qn", "/norestart", "REBOOT=ReallySuppress", "/L*v", msi[0].Args[^1] }, msi[0].Args);
        Assert.Equal(new[] { "/i", dvrMsi, "LAUNCHED_FROM_CIM=1", "/qn", "/norestart", "REBOOT=ReallySuppress", "/L*v", msi[1].Args[^1] }, msi[1].Args);
        Assert.StartsWith(rig.Work, msi[0].Args[^1]);
        Assert.EndsWith(".log", msi[0].Args[^1]);
    }

    [Theory]
    [InlineData(3010)]
    [InlineData(1641)]
    public async Task Install_RebootExitCodes_SucceedWithRebootFlag(int exit)
    {
        using var rig = new Rig { MsiExit = _ => exit };
        var r = await rig.Make().InstallDetailedAsync(rig.CccPath, rig.Work, CancellationToken.None);
        Assert.True(r.RebootRequired);
        Assert.Equal(2, r.ProductCodes.Count);
    }

    [Fact]
    public async Task Install_ExitCode3010_Succeeds()
    {
        using var rig = new Rig { MsiExit = _ => 3010 };
        var codes = await rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None);
        Assert.Equal(2, codes.Count);
    }

    [Fact]
    public async Task Install_ExitCode1603_ThrowsWithLogPath()
    {
        using var rig = new Rig { MsiExit = _ => 1603 };
        var ex = await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None));
        Assert.Contains("1603", ex.Message);
        Assert.Contains(".log", ex.Message);
        Assert.Contains(rig.Work, ex.Message);
        Assert.Empty(ex.InstalledProductCodes);
    }

    [Fact]
    public async Task Install_SecondMsiFails_ExceptionReportsFirstAsInstalled()
    {
        using var rig = new Rig { MsiExit = m => m.Contains("amddvr") ? 1603 : 0 };
        var ex = await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None));
        Assert.Equal(new[] { SettingsCode }, ex.InstalledProductCodes);
    }

    [Fact]
    public async Task Install_FirstAlreadyInstalled_SecondFails_NothingListedAsInstalledByThisRun()
    {
        using var rig = new Rig { MsiExit = _ => 1603 };
        rig.Query.Installed.Add(SettingsCode);
        var ex = await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None));
        Assert.Empty(ex.InstalledProductCodes);
        Assert.Equal(new[] { SettingsCode }, ex.PreExistingProductCodes);
    }

    [Fact]
    public async Task Install_Result_SeparatesInstalledByThisRunFromAlreadyPresent()
    {
        using var rig = new Rig();
        rig.Query.Installed.Add(SettingsCode);
        var r = await rig.Make().InstallDetailedAsync(rig.CccPath, rig.Work, CancellationToken.None);
        Assert.Equal(new[] { SettingsCode, DvrCode }, r.ProductCodes);
        Assert.Equal(new[] { DvrCode }, r.InstalledByThisRun);
        Assert.Equal(new[] { SettingsCode }, r.AlreadyPresent);
    }

    [Fact]
    public async Task Install_CancelWhileFirstMsiexecRuns_FinishesIt_SkipsSecond_ReportsFirst()
    {
        using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var inner = rig.Runner.Respond;
        rig.Runner.Respond = (e, a) =>
        {
            if (e.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase)) cts.Cancel();
            return inner(e, a);
        };
        var ex = await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, cts.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(ex.InnerException);
        Assert.Equal(new[] { SettingsCode }, ex.InstalledProductCodes);
        var only = Assert.Single(rig.Runner.Calls, c => c.Exe.EndsWith("msiexec.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(CancellationToken.None, only.Ct);
        Assert.Contains("ccc-next64.msi", only.Args[1]);
    }

    [Fact]
    public async Task Install_CancelBeforeStoreRemoval_RemovesNothing()
    {
        using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var inner = rig.Runner.Respond;
        rig.Runner.Respond = (e, a) =>
        {
            if (a.Length > 0 && a[^1].Contains("Get-AuthenticodeSignature")) cts.Cancel();
            return inner(e, a);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, cts.Token));
        Assert.False(rig.RemovedStoreApp);
        Assert.Empty(rig.Msiexec);
    }

    [Fact]
    public async Task Uninstall_RunsMsiexecWithNoneToken_CancelChecksOnlyBetweenProducts()
    {
        using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        var inner = rig.Runner.Respond;
        rig.Runner.Respond = (e, a) => { cts.Cancel(); return inner(e, a); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Make().UninstallAsync(new[] { SettingsCode, DvrCode }, cts.Token));
        var c = Assert.Single(rig.Runner.Calls);
        Assert.Equal(CancellationToken.None, c.Ct);
    }

    [Fact]
    public async Task Install_NotRegisteredAfterSuccessfulMsi_Throws()
    {
        using var rig = new Rig();
        var inner = rig.Runner.Respond;
        rig.Runner.Respond = (e, a) => { var r = inner(e, a); rig.Query.Installed.Clear(); return r; };
        await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None));
    }

    [Fact]
    public async Task Install_WorkDirNotEmpty_ThrowsBeforeRunningAnything()
    {
        using var rig = new Rig();
        File.WriteAllText(Path.Combine(rig.Work, "x.txt"), "x");
        await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None));
        Assert.Empty(rig.Runner.Calls);
    }

    [Fact]
    public async Task Install_MsiMissingAfterExtraction_Throws()
    {
        using var rig = new Rig { CreateMsis = false };
        await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None));
        Assert.False(rig.RemovedStoreApp);
    }

    [Fact]
    public async Task Install_Cancelled_PropagatesAndInstallsNothing()
    {
        using var rig = new Rig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Make().InstallAsync(rig.CccPath, rig.Work, cts.Token));
        Assert.Empty(rig.Msiexec);
    }

    [Fact]
    public async Task Install_PathWithQuotes_IsEscapedInSignatureScript()
    {
        using var rig = new Rig();
        rig.Work += "'it's";
        Directory.CreateDirectory(rig.Work);
        await rig.Make().InstallAsync(rig.CccPath, rig.Work, CancellationToken.None);
        var sig = rig.Runner.Calls.First(c => c.Args.Any(a => a.Contains("Get-AuthenticodeSignature"))).Args[^1];
        Assert.Contains("it''s", sig);
        try { Directory.Delete(rig.Work, true); } catch { }
    }

    [Fact]
    public async Task Uninstall_RunsMsiexecX_PerCode_InReverse()
    {
        using var rig = new Rig();
        await rig.Make().UninstallAsync(new[] { SettingsCode, DvrCode }, CancellationToken.None);
        var calls = rig.Runner.Calls.Select(c => c.Args).ToList();
        Assert.Equal(2, calls.Count);
        Assert.Equal(new[] { "/x", DvrCode, "/qn", "/norestart", "REBOOT=ReallySuppress" }, calls[0].Take(5));
        Assert.Equal(new[] { "/x", SettingsCode, "/qn", "/norestart", "REBOOT=ReallySuppress" }, calls[1].Take(5));
        Assert.Equal("/L*v", calls[0][5]);
    }

    [Fact]
    public async Task Uninstall_WritesMsiexecLogIntoTheSessionFolder_NotUserTemp()
    {
        // final review finding 12: an elevated writer must not use a fixed name in the user's %TEMP%
        using var rig = new Rig();
        var session = Path.Combine(rig.Work, "session");
        rig.Log.Path = Path.Combine(session, "session.log");
        await rig.Make().UninstallAsync(new[] { SettingsCode }, CancellationToken.None);
        var args = Assert.Single(rig.Runner.Calls).Args;
        var logArg = args[Array.IndexOf(args, "/L*v") + 1];
        Assert.Equal(session, Path.GetDirectoryName(logArg));
        Assert.DoesNotContain("lgo-msi-uninstall", Path.GetDirectoryName(logArg)!);
    }

    [Fact]
    public async Task Uninstaller_NeedsNoBundledTools_RunsSystemMsiexec()
    {
        // final review finding 5: rollback uninstalls AMD Software without the tools folder
        var runner = new QueueRunner();
        var log = new RecordingLog { Path = Path.Combine(Path.GetTempPath(), "lgc-s-" + Guid.NewGuid().ToString("N"), "session.log") };
        await new AmdSoftwareUninstaller(runner, log).UninstallAsync(new[] { SettingsCode, DvrCode }, CancellationToken.None);
        Assert.Equal(2, runner.Calls.Count);
        Assert.All(runner.Calls, c => Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), c.Exe));
        Assert.Equal(DvrCode, runner.Calls[0].Args[1]);
    }

    [Fact]
    public async Task Uninstall_BadProductCode_ThrowsWithoutRunning()
    {
        using var rig = new Rig();
        await Assert.ThrowsAsync<ArgumentException>(() => rig.Make().UninstallAsync(new[] { "{x} /q" }, CancellationToken.None));
        Assert.Empty(rig.Runner.Calls);
    }

    [Fact]
    public async Task Uninstall_Failure_Throws()
    {
        using var rig = new Rig { MsiExit = _ => 1603 };
        rig.Runner.Respond = (_, _) => (1603, "");
        await Assert.ThrowsAsync<AmdSoftwareException>(() => rig.Make().UninstallAsync(new[] { SettingsCode }, CancellationToken.None));
    }

    [Fact]
    public async Task Uninstall_NotInstalled1605_IsSuccess()
    {
        using var rig = new Rig();
        rig.Runner.Respond = (_, _) => (1605, "");
        await rig.Make().UninstallAsync(new[] { SettingsCode }, CancellationToken.None);
    }

    static readonly string RealDir = Environment.GetEnvironmentVariable("LGO_AMD_SOFTWARE_ROOT") ?? "";

    public sealed class RealAmdSoftwareFactAttribute : FactAttribute
    {
        public RealAmdSoftwareFactAttribute()
        {
            if (string.IsNullOrEmpty(RealDir) || !File.Exists(Path.Combine(RealDir, @"cnext\cnext64\ccc-next64.msi")) || !File.Exists(Path.Combine(RealDir, @"amddvr\amddvr64\amddvr64.msi")))
                Skip = $"Real AMD Software MSIs not found (set LGO_AMD_SOFTWARE_ROOT to the folder containing cnext and amddvr): '{RealDir}'";
        }
    }

    [RealAmdSoftwareFact]
    public void MsiInfo_Read_RealPackages()
    {
        var a = Path.Combine(RealDir, @"cnext\cnext64\ccc-next64.msi");
        var b = Path.Combine(RealDir, @"amddvr\amddvr64\amddvr64.msi");
        var s = MsiInfo.Read(a);
        Assert.Equal("2026.1023.2327.2112", s.ProductVersion);
        Assert.Equal(SettingsCode, s.ProductCode, ignoreCase: true);
        var d = MsiInfo.Read(b);
        Assert.Equal("26.20.26296.2323", d.ProductVersion);
        Assert.Equal(DvrCode, d.ProductCode, ignoreCase: true);
    }
}
