# Legion Go Driver Port: Design

Status: approved design, updated 2026-11-16 with the results of the on-device spike (see "Spike results"). The converter runs on the Legion Go itself. This is a historical design document; the implementation and the README are authoritative where they differ.

## Intent and why

The target device is the Lenovo Legion Go (Ryzen Z1 Extreme, Radeon 780M, same Phoenix silicon as the Ryzen 7840U). Lenovo's graphics driver for it is stuck at Adrenalin 25.30.44 (package `AMD_gfx 1.4.9.3`, driver `32.0.23044.19`, dated 2026-06-29). AMD keeps shipping newer drivers for the 7840U (e.g. Adrenalin 26.10.2, driver `32.0.32015.12012`, INF dated 2026-10-28), but AMD's installer will not install on the Legion Go.

Goal: a Windows app with a UI, installed on the Legion Go, that takes a newer AMD driver package and turns it into something that installs on the Legion Go cleanly, with every Legion Go feature that works today still working. One run, no manual INF extraction.

## Goals

- GUI app (touch and controller friendly, large controls) that runs on the Legion Go.
- Input: AMD's driver installer (`.exe`), as downloaded from amd.com.
- Output: a patched package installed on the device, with a visible log of each step.
- Reusable for each new AMD release, not a one-off.
- Reversible: create a restore point first; keep the original package so rolling back is possible.

## Non-goals

- A genuinely WHQL or Microsoft-signed driver. Nobody outside Microsoft's signing program can produce one.
- Supporting other devices (ROG Ally, other Z1 handhelds). Possible later, not now.
- Redistributing AMD's or Lenovo's files. The app downloads nothing; the user supplies the installer.

## Anti-goals (a failure even if it technically works)

- Leaves the machine in Windows "Test Mode" with a desktop watermark, unless the user explicitly accepts that after seeing the trade-off. Some anti-cheat games refuse to run in test mode.
- Leaves the user with a black screen or no display driver and no way back.
- Breaks Legion Go handheld features (power, TDP, controller, display modes).

## What we found (verified from the two packages)

Lenovo package, unpacked with `innoextract` (it is an Inno Setup installer; 7-Zip cannot open it):

- Legion Go hardware IDs, present in Lenovo's INF and **absent from AMD 26.10.2**:
  - `PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04`
  - `PCI\VEN_1002&DEV_15C8&SUBSYS_381217AA&REV_C9`
- AMD's generic 780M entries run `REV_00..03, 05, 06, C1...`. `REV_04` is the gap, and subsystem `3812` appears nowhere in AMD's INF. This is why the AMD driver does not bind.
- Lenovo's display files: `u0202094.inf` (main, 2257 lines), `amduw23e.inf` (152 lines, contains only the two IDs above; purpose not yet read), plus the usual `amdwin`, `amdocl`, `amdogl`, `amdvlk`, `amdfdans`, `amdfendr`, `UWPPair`.
- Lenovo installs with a plain `Setup.bat` that calls `pnputil /add-driver <inf> /install` for each INF.
- AMD's 26.10.2 package has files Lenovo does not (`amdxe`, `amdafd`, `amdpcibridgeextension`, `ccc2_install.exe`, `amdinstallmanager.msi`). Lenovo has `amd-smi.exe` and `amdhsail*.dll` that AMD's does not.
- Nothing in Lenovo's package looks like TDP, controller or power-profile software. Those features probably live in Lenovo's separate tools. **Not yet confirmed.**

## Constraints and the main risk: driver signing

Every INF is covered by a `.cat` signature. Adding the Legion Go IDs to AMD's INF invalidates it, so the package needs a new catalog. Windows 11 x64 also enforces kernel driver signing: a catalog signed with a self-signed certificate may satisfy the INF install step but **may not satisfy kernel code integrity for the display driver itself** (`amdkmdag.sys`). Unverified at design time.

The options, in order of preference:

1. Self-signed certificate installed once (Trusted Root and Trusted Publishers), catalog re-signed. Cleanest result if Windows accepts it.
2. Test-signing mode. Reliable, but shows a watermark and can break anti-cheat.
3. Disable signature enforcement per boot. Fiddly and not repeatable.

Tooling: `inf2cat` and `signtool` come from the Windows Driver Kit and need a one-time install. PowerShell `New-FileCatalog` is a possible lighter path; whether Windows accepts its output for a driver package is untested.

**Run a spike before any UI work**: on the Legion Go, make a one-line edit to the AMD INF, build and sign a catalog, install with `pnputil`, and see whether the display driver loads with Secure Boot on. The result decides which option the app implements. If option 1 fails, the user chooses between 2 and 3 with the facts in hand.

## Approaches considered

- **A. Windows GUI app on the device, C# / .NET 8 / WPF (chosen).** Native access to elevation, certificate store, `pnputil`, signing tools; single-file self-contained `.exe`. Trade-off: needs the .NET SDK to build (on the Legion Go).
- **B. Rust + egui, cross-built from another machine.** One toolchain for both build and target and safer parsing, but the Windows-specific parts (elevation, cert stores, shelling to signing tools) are more awkward, and signing still has to happen on Windows.
- **C. Converter on another machine, finished package copied to the Legion Go.** Rejected: the catalog cannot be built or signed on non-Windows systems, so a non-Windows machine cannot finish the job.

Decision: the converter runs on the Legion Go as a GUI app (chosen over C), implemented as A.

## What the app does (flow)

1. Ask for the AMD installer; extract it (it is a 7-Zip-readable self-extractor).
2. Read the device's hardware IDs from Windows and show them. Do not rely only on the two IDs above, in case another Legion Go variant reports different ones.
3. Patch the display INF: add the Legion Go IDs, using the driver section AMD uses for Phoenix (`ati2mtag_Phoenix`) and matching strings entries. INF files are UTF-16 LE with BOM: preserve the encoding.
4. Add or reproduce the effect of Lenovo's `amduw23e.inf` (after reading what it does).
5. Rebuild the catalog, sign it, install the certificate if option 1 works.
6. Create a restore point, then install with `pnputil /add-driver ... /install`, then report success or failure.
7. Show a clear way back (uninstall and roll back to Lenovo's driver).

## Spike results (2026-11-15, on the Legion Go, AMD 26.10.2 over Lenovo 25.30.44)

- **Two edits are needed, not one.** (1) Add the device's exact hardware ID to the display INF; (2) add the device/revision record to `B026716\amdgcf.dat` (the kernel driver's device-to-revision allowlist, a 12-byte header plus 3-byte records, header = MetroHash64 over the records plus the release string). With only the INF edit the driver loads but fails to start (Code 43, install-time status 0xC00000E5). Lenovo's own `amdgcf.dat` holds exactly two records (15BF/04, 15C8/C9).
- **Signing:** a new catalog (`makecat`, SHA256, per-member `OSAttr 2:10.0`) signed with a self-signed certificate trusted in LocalMachine Root and TrustedPublisher was accepted with Secure Boot ON and Memory Integrity (HVCI) ON. No Test Mode, no boot changes. `makecat`/`signtool` come from the `Microsoft.Windows.SDK.BuildTools` NuGet package; the full WDK is not needed.
- **Result:** driver 32.0.32015.12012 running, Problem 0, Lenovo extension tuning still applied, USB-C external monitor works, Legion Space correct after a restart (it caches the display list while another monitor is active).
- **AMD Software (Adrenalin)** is installed separately from `B026716\ccc2_install.exe` (7-Zip can open it): `CN\cnext\cnext64\ccc-next64.msi` and `CN\amddvr\amddvr64\amddvr64.msi`, AMD-signed, installed with `msiexec /i ... /qn` after removing Lenovo's Store app `AdvancedMicroDevicesInc-2.AMDRadeonSoftware`. This skips AMD's Setup.exe device check.
- **Facts that differ from the first draft:** the INFs are plain ASCII/CRLF (no BOM), not UTF-16 (the app still preserves whatever encoding it finds); published names (`oemNN.inf`) change between installs, so rollback matches by original name plus version; file and folder names change per AMD release (`u0205499.inf`, `B026716`), so the app discovers them.
- **Inputs:** the Lenovo ("legacy") package supplies the Legion Go hardware IDs, the `amdgcf.dat` records and the extension INF, and is the rollback target. The AMD package is converted.
- **Output:** the app installs the converted driver (and optionally AMD Software) directly. It can also export the converted package as a folder for the user's own machine. It does not produce a redistributable `.exe`: that would redistribute AMD files (non-goal).

## Left to the builder

- UI layout, theme, controller navigation details.
- Exact INF patching approach, logging format, error handling.
- Whether `New-FileCatalog` or the WDK is used, depending on the spike.
- Whether the app also offers an "install from original Lenovo package" rollback path.

## Resolved questions

- RESOLVED: `amduw23e.inf` is a Microsoft-signed Extension INF with Lenovo's display tuning for the two Legion Go IDs. It stays installed unchanged and layers on top of the AMD driver. The app never rewrites it.
- RESOLVED: Test Mode is not needed (option 1 worked with Secure Boot and HVCI on). It remains forbidden without explicit user approval.

## Test inputs

`driver_amd7840U.exe` (AMD 26.10.2), `driver_lenovo.exe` (Lenovo 25.30.44), unpacked copies under `extracted/`. These are not in the repository; developers supply them locally (see CONTRIBUTING.md).
