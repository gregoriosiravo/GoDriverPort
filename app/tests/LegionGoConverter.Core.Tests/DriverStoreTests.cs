namespace LegionGoConverter.Core.Tests;

/// <summary>Scripted fake process runner: never starts a real process. Each call is answered by
/// <see cref="Respond"/>; the answer's lines are reported through the progress sink (like the real runner)
/// and the returned Output can be truncated independently to mimic the real runner's bounded tail.</summary>
internal sealed class QueueRunner : IProcessRunner
{
    public List<(string Exe, string[] Args, CancellationToken Ct)> Calls { get; } = new();
    public Func<string, string[], (int Exit, string Text)> Respond { get; set; } = (_, _) => (0, "");
    /// <summary>When set, Output carries only the last N lines (the real runner keeps a bounded tail).</summary>
    public int? OutputTailLines { get; set; }

    public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? lines, CancellationToken ct)
    {
        var a = args.ToArray();
        Calls.Add((exe, a, ct));
        var (exit, text) = Respond(exe, a);
        var all = text.Replace("\r\n", "\n").Split('\n');
        foreach (var l in all) lines?.Report(l);
        var tail = OutputTailLines is int n ? all.Skip(Math.Max(0, all.Length - n)) : all;
        return Task.FromResult(new ProcessResult(exit, string.Join(Environment.NewLine, tail)));
    }
}

/// <summary>Session log that records every line in memory.</summary>
internal sealed class RecordingLog : ISessionLog
{
    public List<string> Lines { get; } = new();
    public void Info(string m) => Lines.Add("I " + m);
    public void Warn(string m) => Lines.Add("W " + m);
    public void Error(string m, Exception? ex = null) => Lines.Add("E " + m + (ex != null ? " | " + ex.Message : ""));
    public string Path { get; set; } = "";
    public string All => string.Join("\n", Lines);
}

public class DriverStoreTests
{
    // Blocks copied from real `pnputil /enum-drivers` output on the Legion Go (English Windows 11).
    internal const string EnumOutput =
        "Microsoft PnP Utility\r\n" +
        "\r\n" +
        "Published Name:     oem10.inf\r\n" +
        "Original Name:      u0205499.inf\r\n" +
        "Provider Name:      Advanced Micro Devices, Inc.\r\n" +
        "Class Name:         Display\r\n" +
        "Class GUID:         {4d36e968-e325-11ce-bfc1-08002be10318}\r\n" +
        "Driver Version:     10/28/2026 32.0.32015.12012\r\n" +
        "Signer Name:        Legion Go Driver Converter (local self-signed)\r\n" +
        "WHCP Version:       Unknown\r\n" +
        "\r\n" +
        "Published Name:     oem54.inf\r\n" +
        "Original Name:      amduw23e.inf\r\n" +
        "Provider Name:      Advanced Micro Devices, Inc.\r\n" +
        "Class Name:         Extension\r\n" +
        "Class GUID:         {e2f84ce7-8efa-411c-aa69-97454ca4cb57}\r\n" +
        "Extension ID:       {07a2a561-d001-4503-b239-ef2fe0379efb}\r\n" +
        "Driver Version:     06/29/2026 32.0.23044.19\r\n" +
        "Signer Name:        Microsoft Windows Hardware Compatibility Publisher\r\n" +
        "\r\n" +
        "Published Name:     oem55.inf\r\n" +
        "Original Name:      u0202094.inf\r\n" +
        "Provider Name:      Advanced Micro Devices, Inc.\r\n" +
        "Class Name:         Display\r\n" +
        "Class GUID:         {4d36e968-e325-11ce-bfc1-08002be10318}\r\n" +
        "Driver Version:     06/29/2026 32.0.23044.19\r\n" +
        "Signer Name:        Microsoft Windows Hardware Compatibility Publisher\r\n" +
        "Attributes:         Declarative\r\n" +
        "\r\n" +
        "Published Name:     oem27.inf\r\n" +
        "Original Name:      amdxe.inf\r\n" +
        "Provider Name:      AMD\r\n" +
        "Class Name:         System\r\n" +
        "Class GUID:         {4d36e97d-e325-11ce-bfc1-08002be10318}\r\n" +
        "Driver Version:     03/13/2026 26.10.0.1\r\n" +
        "Signer Name:        Microsoft Windows Hardware Compatibility Publisher\r\n" +
        "Attributes:         Universal\r\n" +
        "WHCP Version:       Unknown\r\n" +
        "\r\n";

    [Fact]
    public void Parse_RealEnumDriversBlocks_ExtractsPublishedOriginalVersionSigner()
    {
        var list = DriverStoreParser.Parse(EnumOutput);
        Assert.Equal(4, list.Count);

        var d = list[0];
        Assert.Equal("oem10.inf", d.Published);
        Assert.Equal("u0205499.inf", d.Original);
        Assert.Equal(new Version(32, 0, 32015, 12012), d.Version);
        Assert.Equal("Legion Go Driver Converter (local self-signed)", d.Signer);
        Assert.Equal("Display", d.ClassName);

        Assert.Equal(("oem54.inf", "amduw23e.inf", "Extension"), (list[1].Published, list[1].Original, list[1].ClassName));
        Assert.Equal(new Version(32, 0, 23044, 19), list[1].Version);
        Assert.Equal("Microsoft Windows Hardware Compatibility Publisher", list[1].Signer);
        Assert.Equal(("oem55.inf", "u0202094.inf"), (list[2].Published, list[2].Original));
    }

    [Fact]
    public void Parse_VersionQuirk_PnputilShortFormEqualsInfLeadingZeroForm()
    {
        var amdxe = DriverStoreParser.Parse(EnumOutput).Single(d => d.Original == "amdxe.inf");
        Assert.Equal(Version.Parse("26.10.0.0001"), amdxe.Version);
    }

    [Fact]
    public void Parse_LfOnlyAndIncompleteBlocks_SkipsBlocksWithoutUsableData()
    {
        var text = "Microsoft PnP Utility\n\n" +
                   "Published Name:     oem1.inf\nOriginal Name:      a.inf\nDriver Version:     01/01/2026 1.2.3.4\nSigner Name:        S\n\n" +
                   "Published Name:     oem2.inf\nOriginal Name:      b.inf\nDriver Version:     garbage\n\n" +
                   "Published Name:     oem3.inf\nDriver Version:     01/01/2026 1.0.0.0\n";
        var list = DriverStoreParser.Parse(text);
        var only = Assert.Single(list);
        Assert.Equal(("oem1.inf", "a.inf", "S"), (only.Published, only.Original, only.Signer));
        Assert.Null(only.ClassName);
    }

    const string Pnp = @"C:\Windows\System32\pnputil.exe";

    [Fact]
    public async Task AddAndInstall_RunsPnputilWithExactArguments()
    {
        var root = Path.Combine(Path.GetTempPath(), "lgc ds " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var r = new QueueRunner { Respond = (_, _) => (0, "Driver package added successfully.") };
            await new PnpUtilDriverInstaller(r, Pnp).AddAndInstallAsync(root, CancellationToken.None);
            var c = Assert.Single(r.Calls);
            Assert.Equal(Pnp, c.Exe);
            Assert.Equal(new[] { "/add-driver", root + @"\*.inf", "/subdirs", "/install" }, c.Args);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AddAndInstall_Exit259_CountsAsAdded_AndIsLoggedProminently()
    {
        // final review finding 7 (unverified on the device): ERROR_NO_MORE_ITEMS, packages added but no device updated.
        // Verification decides whether the GPU really runs the new driver.
        var root = Path.Combine(Path.GetTempPath(), "lgc ds " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var r = new QueueRunner { Respond = (_, _) => (259, "Driver package added successfully.\r\nTotal driver packages: 10") };
            var log = new RecordingLog();
            var res = await new PnpUtilDriverInstaller(r, Pnp, log).AddAndInstallAsync(root, CancellationToken.None);
            Assert.False(res.RebootRequired);
            Assert.Contains("Total driver packages: 10", res.Output);
            Assert.Contains(log.Lines, l => l.StartsWith("W ") && l.Contains("259") && l.Contains("Total driver packages: 10"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Stage_And_Delete_Exit259_StillFail()
    {
        var inf = Path.Combine(Path.GetTempPath(), "lgc-" + Guid.NewGuid().ToString("N") + ".inf");
        File.WriteAllText(inf, "; x");
        try
        {
            var r = new QueueRunner { Respond = (_, _) => (259, "No more data is available.") };
            await Assert.ThrowsAsync<DriverStoreException>(() => new PnpUtilDriverInstaller(r, Pnp).StageAsync(inf, CancellationToken.None));
            await Assert.ThrowsAsync<DriverStoreException>(() => new PnpUtilDriverInstaller(r, Pnp).DeleteAsync("oem10.inf", CancellationToken.None));
        }
        finally { File.Delete(inf); }
    }

    [Fact]
    public async Task AddAndInstall_MissingFolder_ThrowsWithoutRunning()
    {
        var r = new QueueRunner();
        await Assert.ThrowsAsync<DriverStoreException>(() =>
            new PnpUtilDriverInstaller(r, Pnp).AddAndInstallAsync(@"C:\does\not\exist\" + Guid.NewGuid(), CancellationToken.None));
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task Delete_RunsPnputilWithExactArguments()
    {
        var r = new QueueRunner { Respond = (_, _) => (0, "Driver package deleted successfully.") };
        await new PnpUtilDriverInstaller(r, Pnp).DeleteAsync("oem10.inf", CancellationToken.None);
        var c = Assert.Single(r.Calls);
        Assert.Equal(Pnp, c.Exe);
        Assert.Equal(new[] { "/delete-driver", "oem10.inf", "/uninstall" }, c.Args);
    }

    [Theory]
    [InlineData("u0205499.inf")]
    [InlineData("oem10.inf /force")]
    [InlineData("C:\\x\\oem10.inf")]
    [InlineData("oem.inf")]
    [InlineData("oem10.inf\n")]
    [InlineData("")]
    public async Task Delete_NonPublishedName_ThrowsWithoutRunning(string name)
    {
        var r = new QueueRunner();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => new PnpUtilDriverInstaller(r, Pnp).DeleteAsync(name, CancellationToken.None));
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task Delete_NonZeroExit_ThrowsWithToolOutput()
    {
        var r = new QueueRunner { Respond = (_, _) => (5, "Failed to delete driver package: Access is denied.") };
        var ex = await Assert.ThrowsAsync<DriverStoreException>(() =>
            new PnpUtilDriverInstaller(r, Pnp).DeleteAsync("oem10.inf", CancellationToken.None));
        Assert.Contains("Access is denied", ex.Message);
        Assert.Contains("5", ex.Message);
    }

    [Fact]
    public async Task Delete_RebootRequiredExit_IsSuccessWithRebootFlag_AndOutputIsLogged()
    {
        var r = new QueueRunner { Respond = (_, _) => (3010, "System reboot is needed to complete uninstall operations!") };
        var log = new RecordingLog();
        var res = await new PnpUtilDriverInstaller(r, Pnp, log).DeleteAsync("oem10.inf", CancellationToken.None);
        Assert.True(res.RebootRequired);
        Assert.Contains("System reboot is needed", res.Output);
        Assert.Contains("System reboot is needed", log.All);
    }

    [Fact]
    public async Task Delete_ExitZero_NoRebootFlag_AndOutputIsLogged()
    {
        var r = new QueueRunner { Respond = (_, _) => (0, "Driver package deleted successfully.") };
        var log = new RecordingLog();
        var res = await new PnpUtilDriverInstaller(r, Pnp, log).DeleteAsync("oem10.inf", CancellationToken.None);
        Assert.False(res.RebootRequired);
        Assert.Contains("Driver package deleted successfully.", log.All);
    }

    [Fact]
    public async Task Stage_RunsAddDriverWithoutInstallOrSubdirs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lgc stage " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var inf = Path.Combine(dir, "u0202094.inf");
        File.WriteAllText(inf, "; x");
        try
        {
            var r = new QueueRunner { Respond = (_, _) => (0, "Driver package added successfully.") };
            var log = new RecordingLog();
            var res = await new PnpUtilDriverInstaller(r, Pnp, log).StageAsync(inf, CancellationToken.None);
            var c = Assert.Single(r.Calls);
            Assert.Equal(Pnp, c.Exe);
            Assert.Equal(new[] { "/add-driver", inf }, c.Args);
            Assert.False(c.Ct.CanBeCanceled);
            Assert.False(res.RebootRequired);
            Assert.Contains("Driver package added successfully.", log.All);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Stage_MissingOrNonInfFile_ThrowsWithoutRunning()
    {
        var r = new QueueRunner();
        var i = new PnpUtilDriverInstaller(r, Pnp);
        await Assert.ThrowsAsync<DriverStoreException>(() => i.StageAsync(@"C:\does\not\exist\" + Guid.NewGuid() + ".inf", CancellationToken.None));
        var notInf = Path.GetTempFileName();
        try { await Assert.ThrowsAsync<DriverStoreException>(() => i.StageAsync(notInf, CancellationToken.None)); }
        finally { File.Delete(notInf); }
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task Stage_NonZeroExit_Throws()
    {
        var inf = Path.Combine(Path.GetTempPath(), "lgc-" + Guid.NewGuid().ToString("N") + ".inf");
        File.WriteAllText(inf, "; x");
        try
        {
            var r = new QueueRunner { Respond = (_, _) => (2, "Failed to add driver package: The system cannot find the file specified.") };
            var ex = await Assert.ThrowsAsync<DriverStoreException>(() => new PnpUtilDriverInstaller(r, Pnp).StageAsync(inf, CancellationToken.None));
            Assert.Contains("cannot find the file", ex.Message);
        }
        finally { File.Delete(inf); }
    }

    [Fact]
    public async Task Delete_CancelledBeforeStart_DoesNotRun()
    {
        var r = new QueueRunner();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new PnpUtilDriverInstaller(r, Pnp).DeleteAsync("oem10.inf", cts.Token));
        Assert.Empty(r.Calls);
    }

    [Fact]
    public async Task Delete_OnceStarted_IsNotKilledByCancellation()
    {
        var r = new QueueRunner();
        using var cts = new CancellationTokenSource();
        await new PnpUtilDriverInstaller(r, Pnp).DeleteAsync("oem10.inf", cts.Token);
        Assert.False(Assert.Single(r.Calls).Ct.CanBeCanceled);
    }

    [Fact]
    public async Task List_RunsEnumDrivers_AndParsesFullOutputBeyondRunnerTail()
    {
        // 300 filler drivers first, then ours: the real runner's Output keeps only a 200-line tail,
        // so the installer must collect the streamed lines, not rely on Output.
        var filler = string.Concat(Enumerable.Range(100, 300).Select(i =>
            $"Published Name:     oem{i}.inf\r\nOriginal Name:      f{i}.inf\r\nDriver Version:     01/01/2026 1.0.0.{i}\r\nSigner Name:        X\r\n\r\n"));
        var text = EnumOutput.Replace("\r\n\r\nPublished Name:     oem10.inf", "\r\n\r\n" + filler + "Published Name:     oem10.inf");
        var r = new QueueRunner { Respond = (_, _) => (0, text), OutputTailLines = 200 };
        var list = await new PnpUtilDriverInstaller(r, Pnp).ListAsync(CancellationToken.None);
        var c = Assert.Single(r.Calls);
        Assert.Equal(new[] { "/enum-drivers" }, c.Args);
        Assert.Equal(304, list.Count);
        Assert.Contains(list, d => d.Published == "oem100.inf");
    }

    [Fact]
    public async Task List_NonZeroExit_ThrowsWithOutput()
    {
        var r = new QueueRunner { Respond = (_, _) => (1, "Some failure") };
        var ex = await Assert.ThrowsAsync<DriverStoreException>(() => new PnpUtilDriverInstaller(r, Pnp).ListAsync(CancellationToken.None));
        Assert.Contains("Some failure", ex.Message);
    }

    [Fact]
    public async Task List_UnrecognizedOutput_ThrowsInsteadOfReportingEmptyStore()
    {
        // e.g. localized (non-English) pnputil output: an empty list would make rollback think nothing is installed
        var r = new QueueRunner { Respond = (_, _) => (0, "Utilitaire PnP Microsoft\r\n\r\nNom publié :     oem10.inf\r\n") };
        await Assert.ThrowsAsync<DriverStoreException>(() => new PnpUtilDriverInstaller(r, Pnp).ListAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Runner_StartFailure_IsWrappedAsDriverStoreException()
    {
        var r = new ThrowingRunner();
        await Assert.ThrowsAsync<DriverStoreException>(() => new PnpUtilDriverInstaller(r, Pnp).ListAsync(CancellationToken.None));
    }

    sealed class ThrowingRunner : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? lines, CancellationToken ct) =>
            throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified");
    }

    [Fact]
    public void Manifest_SaveLoad_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "lgc-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var m = new SessionManifest("26.10.2", "777C7D479879BCB167901701A72CABF29DD9BEB9", SigningCertificate.DefaultSubject,
                new[] { new PackageRef("u0205499.inf", "32.0.32015.12012", IsDisplay: true), new PackageRef("amdxe.inf", "26.10.0.0001") },
                @"C:\Sessions\x\lenovo\Display\u0202094.inf",
                new[] { "oem3.inf", "oem55.inf" },
                new[] { "{11111111-2222-3333-4444-555555555555}" });
            m.Save(path);
            m.Save(path); // rewrite in place (after AMD Software install)
            var l = SessionManifest.Load(path);
            Assert.Equal(m.AmdVersion, l.AmdVersion);
            Assert.Equal(m.CertThumbprint, l.CertThumbprint);
            Assert.Equal(m.CertSubject, l.CertSubject);
            Assert.Equal(m.Packages, l.Packages);
            Assert.Equal(m.LenovoInfPath, l.LenovoInfPath);
            Assert.Equal(m.PreExistingPublishedNames, l.PreExistingPublishedNames);
            Assert.Equal(m.AmdSoftwareProductCodes, l.AmdSoftwareProductCodes);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Manifest_LoadWithoutIsDisplay_DefaultsToFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), "lgc-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "{\"AmdVersion\":\"1\",\"CertThumbprint\":\"AB\",\"CertSubject\":\"CN=x\",\"Packages\":[{\"InfName\":\"a.inf\",\"Version\":\"1.0.0.0\"}]," +
                                    "\"LenovoInfPath\":\"C:\\\\x\\\\u0202094.inf\",\"PreExistingPublishedNames\":[],\"AmdSoftwareProductCodes\":[]}");
            Assert.False(Assert.Single(SessionManifest.Load(path).Packages).IsDisplay);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("")] // power loss before the data reached the disk
    [InlineData("{\"AmdVersion\":\"1\"}")]
    // an export-only manifest has no Lenovo driver path: it can never drive a rollback
    [InlineData("{\"AmdVersion\":\"1\",\"CertThumbprint\":\"AB\",\"CertSubject\":\"CN=x\",\"Packages\":[],\"LenovoInfPath\":\"\",\"PreExistingPublishedNames\":[],\"AmdSoftwareProductCodes\":[]}")]
    [InlineData("{\"AmdVersion\":\"1\",\"CertThumbprint\":\"AB\",\"CertSubject\":\"CN=x\",\"Packages\":[],\"LenovoInfPath\":\"  \",\"PreExistingPublishedNames\":[],\"AmdSoftwareProductCodes\":[]}")]
    public void Manifest_LoadInvalid_ThrowsInvalidData(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), "lgc-manifest-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, json);
            Assert.Throws<InvalidDataException>(() => SessionManifest.Load(path));
        }
        finally { File.Delete(path); }
    }
}
