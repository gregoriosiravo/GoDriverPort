# Legion Go Driver Port Implementation Plan

> **Note:** this is the original implementation plan, kept as a design record. The code and README are authoritative where they differ; paths, task numbering and some decisions have changed since.

> The certificate subject `Legion Go Driver Converter (local self-signed)` and the data folder `C:\ProgramData\LegionGoDriverConverter` below are legacy identifiers from before the rename to Legion Go Driver Port, kept unchanged for compatibility.

**Goal:** A WPF app that takes the user's Lenovo (legacy) driver package and a newer AMD 780M package, converts the AMD driver so it installs on the Legion Go, and installs it (plus AMD Software) with restore point, logging and rollback.

**Architecture:** A UI-free core library holds every step behind small interfaces (process runner, device probe, trust store, restore point, driver installer) so the whole pipeline is tested with fakes; only the final stages mutate the system, behind one user confirmation. A thin WPF app drives the pipeline. The proven spike scripts in `tools/spike/` are the reference implementation to port.

**Tech Stack:** C# / .NET 8, WPF, CommunityToolkit.Mvvm, xUnit, Windows.Gaming.Input (controller), `makecat`/`signtool` (BuildTools NuGet), 7-Zip and innoextract executables.

**Spec:** `docs/design/design-spec.md` (see "Spike results")

## Global Constraints

- C# / .NET 8 / WPF, Windows 11 x64, single-file self-contained `.exe` with a `tools\` folder beside it; `app.manifest` requests administrator.
- Never enable Test Mode or change boot settings (Secure Boot, `bcdedit`); only read and display Secure Boot/HVCI state.
- INF files: preserve the encoding found (ASCII/ANSI without BOM, or UTF-16 LE with BOM); change nothing but the inserted line.
- Restore point before any install, original Lenovo package kept for rollback, every step logged to `C:\ProgramData\LegionGoDriverConverter\sessions\<timestamp>\`.
- No AMD or Lenovo files in the repo; the app downloads nothing; the user supplies both installers. Real-package tests read `LGO_AMD_ROOT` / `LGO_LENOVO_ROOT` (see CONTRIBUTING.md; no default paths) and skip when unset.
- Each installer unpacks into its own new empty folder; scripts and tools stay outside extracted folders.
- Ask before anything hard to reverse: driver install, certificate trust, AMD Software install, reboot.
- No test may trust a certificate, touch the driver store, create a restore point, run `msiexec`/`pnputil` for real, or change boot settings: system-touching code is exercised only through fakes. Tests that create a certificate use CurrentUser\My only and delete it afterwards. Commits end with the line `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- Target frameworks (ruling after Task 4): Core, App and Tests all target `net8.0-windows10.0.19041.0`; Core hosts Windows-only code (WMI, msi.dll, certificate store). Not a cross-platform library.
- UI: touch targets at least 64 px, full keyboard/D-pad/controller navigation, visible focus.
- App code lives in `app/`; namespaces `LegionGoConverter.Core`, `LegionGoConverter.App`; tests in `LegionGoConverter.Core.Tests`.

## Review Focus

1. **AMD release with a different layout** (renamed INF or binaries folder, changed models section, changed `amdgcf.dat` format): fail closed before any system change. Tests: Tasks 2, 3, 9.
2. **Wrong device** (Legion Go S, Go 2, non-Lenovo AMD GPU, same ID with another revision, no AMD GPU): refuse with a clear message, no override. Test: Task 4.
3. **Cancel or crash mid-run**: nothing touches the system before the confirmation gate; after it, a manifest on disk lets rollback work later. Tests: Tasks 7, 9.
4. **Wrong or swapped input files, paths with spaces/non-ASCII, low disk space**: detect by content, not name; preflight needs 12 GB free. Tests: Tasks 3, 5, 9.
5. **Re-run on an already converted system** (cert present, driver installed, Store app already removed, restore point made within 24 h): idempotent, no duplicate stores, no failure. Tests: Tasks 6, 7, 8.

---

### Task 1: Toolchain and solution scaffold

**Files:** Create `app/LegionGoConverter.sln`, `app/src/LegionGoConverter.Core/`, `app/src/LegionGoConverter.App/`, `app/tests/LegionGoConverter.Core.Tests/`, `app/src/LegionGoConverter.Core/ISessionLog.cs`.

**Interfaces:**
- Produces: `public interface ISessionLog { void Info(string message); void Warn(string message); void Error(string message, Exception? ex = null); string Path { get; } }`; `FileSessionLog(string sessionDir) : ISessionLog` (UTF-8, timestamped lines, flushes every line).

- [ ] **Step 1:** The controller already installed the .NET 8 SDK and created the git repo, branch `converter` and `.gitignore`; do not run `git init`. Confirm `dotnet --list-sdks` shows 8.x.
- [ ] **Step 2:** Create the solution under `app/`: Core (net8.0), App (net8.0-windows10.0.19041.0, WPF), Tests (xUnit, net8.0-windows10.0.19041.0).
- [ ] **Step 3: Failing test** `FileSessionLog_WritesTimestampedLinesAndFlushes`: write two lines, read the file while the log is still open, assert both present and each starts with an ISO timestamp.
- [ ] **Step 4:** Run `dotnet test app` → FAIL; implement `FileSessionLog`; run → PASS.
- [ ] **Step 5:** Commit `chore: scaffold solution and session log`.

### Task 2: `amdgcf.dat` reader/writer

**Files:** Create `Core/AmdGcf.cs`, `Core/MetroHash64.cs`; Test `Tests/AmdGcfTests.cs`. Port from `tools/spike/AmdGcf.ps1`.

**Interfaces:**
- Produces: `public sealed record GcfRecord(int Device, int Revision);` `public static class AmdGcf { IReadOnlyList<GcfRecord> Read(byte[] dat, string release); byte[] Write(IEnumerable<GcfRecord> records, string release); byte[] AddRecords(byte[] dat, string release, IEnumerable<GcfRecord> add); }` `public sealed class AmdGcfFormatException : Exception`.
- `Write` sorts by (Device, Revision), stores `Device + recordPosition` as uint16 LE, header = big-endian MetroHash64(seed 0) over record bytes + ASCII release. `Read` throws `AmdGcfFormatException` on length ≠ `12 + 3*count` or header mismatch. `AddRecords` returns identical bytes when every record already exists.

- [ ] **Step 1: Failing tests:**
  - `Write_LenovoRecords_MatchesLenovoFile`: `AmdGcf.Write([(0x15BF,0x04),(0x15C8,0xC9)], "25.30.44-260512a-202094C-Lenovo")` equals bytes `02000000 D9531DB7616978D8 CB1504 D715C9`.
  - `Read_Write_RoundTrips`: generate 227 records, write with release `"26.20.15.12-261023a1-205499C-AMD-Software-Adrenalin-Edition"`, read, write again → identical bytes.
  - `Read_WrongRelease_Throws` and `Read_TruncatedFile_Throws` (`AmdGcfFormatException`).
  - `AddRecords_ExistingRecord_ReturnsSameBytes`.
  - Real-package (skipped if absent): AMD 26.10.2 DAT read/write round-trips byte-identical (SHA-256 `DF7D6CB0F0AE380B58F36EAB4E2F41D36085F14680733F018A3DACA1696BA022`), and `AddRecords(..., [(0x15BF,0x04)])` gives SHA-256 `808CB0956B5D67337CC5ADA7DB4F041A6092678751564B0F1F7790EEA0B18A20`, 696 bytes.
- [ ] **Step 2:** Run → FAIL; implement (hash constants `0xD6D018F5, 0xA2AA033B, 0x62992FC1, 0x30BC5B29` as in the spike); run → PASS.
- [ ] **Step 3:** Commit `feat: amdgcf.dat reader/writer`.

### Task 3: AMD package locator and INF patcher

**Files:** Create `Core/AmdPackage.cs`, `Core/InfPatcher.cs`; Test `Tests/InfPatcherTests.cs`, `Tests/AmdPackageTests.cs`.

**Interfaces:**
- Produces: `public sealed record AmdPackage(string DisplayDir, string InfPath, string BinariesDir, string ReleaseString, Version DriverVersion, string? Ccc2Path); static AmdPackage AmdPackageLocator.Locate(string extractedRoot)` — finds the display INF by content (`Class=Display` plus at least one generic `DEV_15BF&REV_xx` line with no SUBSYS; the Lenovo package has none, so it is rejected, while AMD INFs also contain Lenovo `17AA` subsystem lines, so vendor IDs are never used to identify a package), the binaries folder (the one holding `amdkmdag.sys` and `amdgcf.dat`), release string = `DriverBuildNumber` minus its first `-` segment, `ccc2_install.exe` if present. Throws `PackageLayoutException` (message names what was not found).
- `public sealed record InfPatchResult(byte[] Bytes, string Section, string InsertedLine, bool AlreadyPresent); static InfPatchResult InfPatcher.AddDeviceId(byte[] inf, string hardwareId)` — inserts `"%<key>%" = <driverSection>, <hardwareId>` after the last 15BF line in the one `[ATI.Mfg.NTamd64.10.0*]` section containing 15BF lines, reusing the driver section and display-name key of the first generic (no SUBSYS) line for that device. Requires exactly one such section. Handles ASCII/ANSI (byte-preserving) and UTF-16 LE BOM files; keeps CRLF.

- [ ] **Step 1: Failing tests** (synthetic INFs): `AddDeviceId_InsertsOneLine_AfterLastMatchingLine_AllOtherBytesUnchanged`; `AddDeviceId_Utf16WithBom_PreservesEncoding`; `AddDeviceId_AlreadyPresent_ReturnsUnchanged`; `AddDeviceId_NoModelsSection_Throws`; `AddDeviceId_TwoCandidateSections_Throws`. Real-package (skipped if absent): patching AMD 26.10.2 for `PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04` inserts `"%AMD15BF.781%" = ati2mtag_Phoenix, PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04` after the `%AMD15BF.815%` line; result 1119446 bytes, SHA-256 `827E87CAA6703F4405D4948864A0B4E43F898E6AB91E4408A20D7501E2D2E3F4`. `Locate` on a folder with renamed INF/binaries still succeeds; on the Lenovo package or an empty folder throws `PackageLayoutException`; on a folder whose DAT format is incompatible fails later in Task 9 preflight.
- [ ] **Step 2:** Run → FAIL; implement; run → PASS.
- [ ] **Step 3:** Commit `feat: AMD package locator and INF patcher`.

### Task 4: Lenovo profile, device probe, target resolver

**Files:** Create `Core/LegionGoProfile.cs`, `Core/DeviceProbe.cs`, `Core/TargetResolver.cs`; Test `Tests/TargetResolverTests.cs`.

**Interfaces:**
- Produces: `public sealed record LegionGoProfile(IReadOnlyList<string> HardwareIds, IReadOnlyList<GcfRecord> GcfRecords, string ExtensionInfPath, Version LenovoDriverVersion, string LenovoDisplayInfPath); static LegionGoProfile LenovoPackage.Load(string extractedRoot)` (HWIDs = the `PCI\VEN_1002&DEV_15xx&SUBSYS_xxxx17AA&REV_xx` IDs in the models section of the Lenovo extension INF, i.e. the `Class=Extension` INF listing such IDs, found by content; display INF = the `Class=Display` INF in the same folder; records from the `amdgcf.dat` in that tree's binaries folder (the folder holding `amdkmdag.sys`)). Throws `PackageLayoutException` if no such extension INF exists (an AMD package has none).
- `public sealed record GpuDevice(string InstanceId, IReadOnlyList<string> HardwareIds, string? DriverVersion, string? DriverInf, int ProblemCode); public interface IDeviceProbe { IReadOnlyList<GpuDevice> GetAmdDisplayDevices(); }` (real implementation queries PnP via `Get-PnpDevice`-equivalent WMI `Win32_PnPEntity`, class Display, VEN_1002).
- `public static class TargetResolver { static ResolvedTarget Resolve(IReadOnlyList<GpuDevice> live, LegionGoProfile profile); } public sealed record ResolvedTarget(GpuDevice Device, string HardwareId, GcfRecord GcfRecord);` Throws `UnsupportedDeviceException`.

- [ ] **Step 1: Failing tests:** `Resolve_LegionGo1_ReturnsExactIdAndRecord` (live `PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04` → record (0x15BF,0x04)); `Resolve_SameIdOtherRevision_Throws`; `Resolve_LegionGoS_380C17AA_Throws`; `Resolve_NoAmdGpu_Throws`; `Resolve_NonLenovoSubsystem_Throws`; `Resolve_StaleNonPresentNvidiaEntry_Ignored`. Real-package tests (skipped if absent): `LenovoPackage.Load` on the AMD package folder throws `PackageLayoutException`; the Lenovo package yields IDs `…DEV_15BF&SUBSYS_381217AA&REV_04` and `…DEV_15C8&SUBSYS_381217AA&REV_C9`.
- [ ] **Step 2:** Run → FAIL; implement; run → PASS.
- [ ] **Step 3:** Commit `feat: Lenovo profile and target resolution`.

### Task 5: Process runner and extractors

**Files:** Create `Core/ProcessRunner.cs`, `Core/Extractors.cs`, `Core/ToolLocator.cs`; Test `Tests/ExtractorTests.cs`.

**Interfaces:**
- Produces: `public sealed record ProcessResult(int ExitCode, string Output); public interface IProcessRunner { Task<ProcessResult> RunAsync(string exe, IReadOnlyList<string> args, IProgress<string>? lines, CancellationToken ct); }` (args passed as an argument list, never a joined string; kills the process on cancel).
- `public interface IExtractor { Task ExtractAsync(string installer, string targetDir, IProgress<string>? lines, CancellationToken ct); }` with `SevenZipExtractor(IProcessRunner, string sevenZipExe)` (AMD installer) and `InnoExtractor(IProcessRunner, string innoextractExe)` (Lenovo installer). Both throw `ExtractionException` if `targetDir` exists and is not empty, or the tool exits non-zero. `public sealed record ToolPaths(string SevenZip, string InnoExtract, string MakeCat, string SignTool); static ToolPaths ToolLocator.Resolve()` looks for all four in `<app>\tools\` (`7z.exe`, `innoextract.exe`, `makecat.exe`, `signtool.exe`) and throws `ToolMissingException` naming the missing file; later tasks consume `ToolPaths`.

- [ ] **Step 1: Failing tests** (fake runner records calls): `Extract_NonEmptyTarget_ThrowsBeforeRunning`; `Extract_PathWithSpacesAndNonAscii_PassedAsSingleArgument`; `Extract_ToolFails_ThrowsWithToolOutput`; `Cancel_KillsProcess` (real `ProcessRunner` with a sleeping process).
- [ ] **Step 2:** Run → FAIL; implement; run → PASS. Real 7z/innoextract smoke test skipped if tools absent.
- [ ] **Step 3:** Commit `feat: process runner and extractors`.

### Task 6: Catalog builder, certificate, trust store

**Files:** Create `Core/CatalogBuilder.cs`, `Core/SigningCertificate.cs`, `Core/TrustStore.cs`; Test `Tests/CatalogBuilderTests.cs`, `Tests/SigningCertificateTests.cs`. Port from `tools/spike/build-patched-package.ps1` steps 4-5.

**Interfaces:**
- Produces: `static string CdfWriter.Build(string catalogName, string resultDir, IEnumerable<string> memberFiles)` (header `HashAlgorithms=SHA256`, `CatalogVersion=2`, `EncodingType=0x00010001`, `CATATTR1=0x10010001:OSAttr:2:10.0`; per member `<hash>NAME=path`, `<hash>NAMEATTR1=0x10010001:OSAttr:2:10.0`, `<hash>NAMEATTR2=0x10010001:File:NAME`).
- `public sealed class CatalogBuilder(IProcessRunner runner, ToolPaths tools) { Task BuildAndSignAsync(string displayDir, string infName, string binariesDir, string catName, X509Certificate2 cert, CancellationToken ct); }` (catalog members = the INF plus every file in `binariesDir`; the catalog is written into `displayDir`). runs `makecat -v`, then `signtool sign /fd SHA256 /sha1 <thumb> /s My`.
- `static X509Certificate2 SigningCertificate.GetOrCreate()` (subject `CN=Legion Go Driver Converter (local self-signed)`, RSA 3072, SHA256, CurrentUser\My, reuse if valid); `public interface ITrustStore { bool IsTrusted(string thumbprint); void Trust(X509Certificate2 cert); void Untrust(string thumbprint); }` (LocalMachine Root + TrustedPublisher; `Trust` is a no-op when already trusted).

- [ ] **Step 1: Failing tests:** `CdfWriter_ContainsAttributesForEveryMember`; `GetOrCreate_CalledTwice_ReturnsSameThumbprint` (cleans up test certs); `Trust_AlreadyTrusted_IsNoOp` (fake store); real-package test (skipped if absent): build and sign the patched AMD 26.10.2 package, then `certutil -dump` shows 255 `OSAttr` and 254 `File` entries (127 members), signed by our subject.
- [ ] **Step 2:** Run → FAIL; implement; run → PASS.
- [ ] **Step 3:** Commit `feat: catalog build, signing, trust store`.

### Task 7: Restore point, driver install, inventory, rollback

**Files:** Create `Core/RestorePoint.cs`, `Core/DriverStore.cs`, `Core/SessionManifest.cs`, `Core/Rollback.cs`; Test `Tests/DriverStoreTests.cs`, `Tests/RollbackTests.cs`. Port from `tools/spike/rollback-spike.ps1`.

**Interfaces:**
- Produces: `public interface IRestorePoint { Task<RestorePointOutcome> CreateAsync(string description, CancellationToken ct); } public enum RestorePointOutcome { Created, RecentOneExists }` (the Windows 24-hour warning is `RecentOneExists`, not an error).
- `public sealed record StoredDriver(string Published, string Original, Version Version, string Signer); static IReadOnlyList<StoredDriver> DriverStoreParser.Parse(string pnputilEnumDriversOutput)` (version = last token of `Driver Version:`, compared as `System.Version`, so `26.10.0.0001` equals `26.10.0.1`).
- `public interface IDriverInstaller { Task AddAndInstallAsync(string infGlobRoot, CancellationToken ct); Task DeleteAsync(string publishedName, CancellationToken ct); Task<IReadOnlyList<StoredDriver>> ListAsync(CancellationToken ct); }` (`pnputil /add-driver <root>\*.inf /subdirs /install`; `/delete-driver oemNN.inf /uninstall`).
- `public sealed record PackageRef(string InfName, string Version); public sealed record SessionManifest(string AmdVersion, string CertThumbprint, IReadOnlyList<PackageRef> Packages, string LenovoInfPath, IReadOnlyList<string> AmdSoftwareProductCodes)` with `Save(string path)` / `static Load(string path)` (System.Text.Json); saved in the session folder before the first mutation and rewritten after AMD Software is installed.
- `public static class RollbackPlanner { static IReadOnlyList<StoredDriver> Select(IEnumerable<StoredDriver> store, SessionManifest m); }` (match original name + version, display INF first). `RollbackService.RunAsync(SessionManifest, ct)` deletes selected packages, untrusts the cert, re-adds Lenovo's package if its display driver is missing from the store.

- [ ] **Step 1: Failing tests:** parser against the captured `pnputil /enum-drivers` blocks (published `oem10.inf`/`u0205499.inf`/`32.0.32015.12012`/signer `Legion Go Driver Converter (local self-signed)`); `Select_MatchesByNameAndVersion_NotByOemNumber` (store renumbered, Lenovo packages with other versions untouched, `26.10.0.1` matches `26.10.0.0001`); `Select_DisplayInfFirst`; `Rollback_LenovoDriverMissing_ReAddsFromLenovoPackage` (fake installer); `Rollback_RunTwice_SecondRunIsNoOp`; `RestorePoint_RecentExists_ReturnsOutcomeNotException`.
- [ ] **Step 2:** Run → FAIL; implement (`CreateAsync` runs `Checkpoint-Computer` through `powershell.exe -NoProfile` and maps the 1440-minute warning); run → PASS.
- [ ] **Step 3:** Commit `feat: restore point, driver store, rollback`.

### Task 8: AMD Software (Adrenalin) installer

**Files:** Create `Core/AmdSoftwareInstaller.cs`, `Core/MsiInfo.cs`; Test `Tests/AmdSoftwareInstallerTests.cs`. Port from `tools/spike/install-amd-software.ps1`.

**Interfaces:**
- Produces: `static MsiInfo MsiInfo.Read(string msiPath)` (ProductName, ProductVersion, ProductCode via `msi.dll` `MsiOpenPackageEx` with ignore-machine-state, `MsiGetProperty`; nothing hard-coded). `public sealed class AmdSoftwareInstaller(IProcessRunner runner, ToolPaths tools, ISessionLog log) { Task<IReadOnlyList<string>> InstallAsync(string ccc2Path, string workDir, CancellationToken ct);` (returns the product codes now installed, including ones already present); Task UninstallAsync(IReadOnlyList<string> productCodes, CancellationToken ct); }`.
- `InstallAsync`: extract `CN\cnext\cnext64\*` and `CN\amddvr\amddvr64\*` from `ccc2_install.exe` with 7-Zip into an empty `workDir`; require valid Authenticode signature with subject starting `CN=Advanced Micro Devices`; remove `AdvancedMicroDevicesInc-2.AMDRadeonSoftware` (all users + provisioned) and stop if still registered; `msiexec /i <msi> [LAUNCHED_FROM_CIM=1 for DVR] /qn /norestart REBOOT=ReallySuppress /L*v`; accept exit 0, 1641, 3010; verify product registered.

- [ ] **Step 1: Failing tests** (fake runner): `Install_UnsignedMsi_Throws_NothingRemoved`; `Install_StoreAppStillRegistered_StopsBeforeMsi`; `Install_ProductAlreadyInstalled_SkipsIt`; `Install_ExitCode3010_Succeeds`; `Install_ExitCode1603_ThrowsWithLogPath`; real-package test (skipped if absent): `MsiInfo.Read` returns AMD Settings `2026.1023.2327.2112` and AMD DVR `26.20.26296.2323`.
- [ ] **Step 2:** Run → FAIL; implement; run → PASS.
- [ ] **Step 3:** Commit `feat: AMD Software installer`.

### Task 9: Conversion pipeline

**Files:** Create `Core/ConversionPipeline.cs`, `Core/Preflight.cs`; Test `Tests/ConversionPipelineTests.cs`.

**Interfaces:**
- Consumes: everything above. Also defines, in this task: `public interface IEnvironmentProbe { bool IsAdministrator(); long FreeBytes(string path); SecurityState GetSecurityState(); }` (`SecurityState` = SecureBoot, Hvci, TestSigning booleans, read-only) and `public sealed record PipelineServices(...)` bundling the injected interfaces (process runner, device probe, trust store, restore point, driver installer, environment probe, tools); `ConversionPipeline` takes `PipelineServices` in its constructor.
- Before `InstallDriver` the pipeline ensures Lenovo's base display driver is in the driver store (adds it from the extracted Lenovo package with the driver installer if missing) so rollback never depends on the extracted folder.
- Produces: `public sealed record ConversionRequest(string AmdInstaller, string LenovoInstaller, string WorkRoot, bool InstallDriver, bool InstallAmdSoftware, string? ExportFolder);` `public enum Stage { Preflight, ExtractLenovo, ExtractAmd, Analyze, Build, Sign, Confirm, RestorePoint, TrustCertificate, InstallDriver, VerifyDriver, InstallAmdSoftware, Export, Done }` `public sealed record StageProgress(Stage Stage, string Message, double? Fraction);` `public interface IUserPrompt { Task<bool> ConfirmAsync(string title, string message, CancellationToken ct); }` `public sealed record ConversionResult(bool Success, bool RebootRequired, string SessionDir, string? Error);` `public sealed class ConversionPipeline { Task<ConversionResult> RunAsync(ConversionRequest r, IProgress<StageProgress> progress, IUserPrompt prompt, CancellationToken ct); }`
- Stages up to `Sign` change nothing on the system except files under `WorkRoot` and a certificate in CurrentUser\My. `Confirm` shows the exact list of changes; "no" stops with `Success=false` and zero mutations. Preflight checks: administrator, 12 GB free on `WorkRoot`'s drive, both installers exist and are identified by content (a swapped or random `.exe` is rejected with a message), device resolves (Task 4), Secure Boot/HVCI state logged. `Analyze` runs the DAT self-test and INF patch in memory and fails closed. `VerifyDriver` requires the device to report the new version with Problem 0 after install (a reboot may still be required) and on failure offers rollback instead of leaving a broken state.

- [ ] **Step 1: Failing tests** (all fakes): `Run_HappyPath_StagesInOrder_AndWritesManifestBeforeInstall`; `Run_UserDeclinesConfirm_NoMutatingCalls` (fake system records zero restore-point/trust/install calls); `Run_CancelledDuringBuild_NoMutatingCalls`; `Run_SwappedInputs_FailsInPreflight`; `Run_UnsupportedDevice_FailsInPreflight`; `Run_LowDisk_FailsInPreflight`; `Run_IncompatibleDat_FailsInAnalyze_BeforeAnyInstall`; `Run_AlreadyConverted_CertTrustedStoreHasPackage_Succeeds`; `Run_ExportOnly_WritesFolderWithPatchedTree_Cer_AndManifest_NoInstall`; `Run_InstallFails_OffersRollback`. Real-package build test (skipped if absent): Build stage output has exactly two changed files vs the AMD source (INF and DAT, hashes from Tasks 2 and 3).
- [ ] **Step 2:** Run → FAIL; implement; run → PASS.
- [ ] **Step 3:** Commit `feat: conversion pipeline`.

### Task 10: WPF UI (use the frontend-design skill)

**Files:** Create `App/Views/*`, `App/ViewModels/MainViewModel.cs`, `App/GamepadNavigator.cs`, `App/Services/WpfUserPrompt.cs`, `App/app.manifest` (requireAdministrator); Test `Tests/MainViewModelTests.cs`.

**Interfaces:**
- Consumes: `ConversionPipeline`, `IUserPrompt`, `LenovoPackage`, `AmdPackageLocator`, `RollbackService`.
- Produces: `MainViewModel` with `Step` (Select, Review, Progress, Result), `LenovoInstallerPath`, `AmdInstallerPath`, `LenovoInfo`/`AmdInfo` (detected versions after validation), `InstallDriver`, `InstallAmdSoftware`, `ExportFolder`, commands `BrowseLenovo`, `BrowseAmd`, `Next`, `Back`, `Start`, `Cancel`, `RollbackCommand`, `RebootCommand`. Four screens: pick both installers (large pickers showing detected version and a pass/fail mark), review (device ID, versions, Secure Boot/HVCI, list of what will change, option toggles), progress (stage list and live log), result (reboot now / rollback / open log). `GamepadNavigator`: D-pad/left stick move focus, A clicks, B goes back, using `Windows.Gaming.Input`.

- [ ] **Step 1: Failing tests** (fake pipeline): `Next_Disabled_UntilBothInstallersValid`; `Start_PassesOptionsToPipeline`; `Cancel_DuringBuild_ReturnsToReview`; `PipelineFailure_ShowsErrorAndRollbackOption`; `Result_RebootRequired_ShowsRebootButton`.
- [ ] **Step 2:** Run → FAIL; implement view model; run → PASS. Then invoke the `frontend-design` skill and build the four views (touch targets at least 64 px, visible focus, light and dark themes).
- [ ] **Step 3:** Launch the app (`dotnet run --project app/src/LegionGoConverter.App`) and walk the screens with fake inputs on the review screen; confirm keyboard and controller navigation. Do not click Start.
- [ ] **Step 4:** Commit `feat: WPF UI with controller navigation`.

### Task 11: Packaging and on-device validation

**Files:** Create `app/tools/fetch-tools.ps1`, `app/README.md`, `app/publish.ps1`.

- [ ] **Step 1:** `fetch-tools.ps1` (developer-time only; the app itself downloads nothing) places `makecat.exe`, `signtool.exe` (pinned `Microsoft.Windows.SDK.BuildTools 10.0.28000.2526`, Microsoft-signed), `7z.exe`/`7z.dll` and `innoextract.exe` into `app/tools/`, verifying signatures where present. Record the license of each tool in the README and confirm each may ship alongside the app before any distribution.
- [ ] **Step 2:** `publish.ps1`: `dotnet publish -r win-x64 --self-contained -p:PublishSingleFile=true`, copy `tools\`; verify the output starts and requests elevation.
- [ ] **Step 3:** **On-device validation (the user runs it, with the restore point and BitLocker key ready):** from the current Lenovo state, run the app end to end with the real installers and "Install AMD Software" on; check device status, version, Problem 0, Secure Boot/HVCI unchanged, Legion Space after restart, USB-C monitor, Adrenalin opens. Then run Rollback from the app and confirm Lenovo 25.30.44 returns with Problem 0.
- [ ] **Step 4:** Commit `chore: packaging and validation notes`.
