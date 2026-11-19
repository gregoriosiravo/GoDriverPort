# Legion Go Driver Port

A Windows desktop app (C# / .NET 8 / WPF) that takes AMD's current Radeon 780M graphics driver and converts it so that it installs on the **Lenovo Legion Go**, whose GPU is otherwise locked to Lenovo's older OEM driver. You supply the two installers; the app extracts them, patches the AMD driver package for the Legion Go, creates and signs a new catalog with a local self-signed certificate that it adds to this PC's machine-wide trust stores (see Safety), creates a restore point, installs the result, verifies it, and can optionally install AMD Software (Adrenalin).

## Who it is for

- Lenovo Legion Go (the first model), Ryzen Z1 Extreme, Radeon 780M.
- Tested hardware ID: `PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04`.
- **The GPU must be one the Lenovo package itself supports.** Before it changes anything, the app requires an AMD GPU with a Lenovo (17AA) subsystem ID, and then the exact hardware ID of this PC's GPU must be one of the IDs listed in the Lenovo package's extension INF (that list also contains the Z1 variant, `DEV_15C8`, which has not been tested). Any other device is refused.

## Requirements

- Windows 11, 64-bit.
- Administrator rights (the app asks for them at launch).
- Two installers that you provide yourself:
  1. Lenovo's legacy Legion Go display driver installer (tested: Lenovo 25.30.44).
  2. AMD's Radeon 780M driver installer (tested: AMD 26.10.2).
- **The free Windows SDK signing tools** (see below). Microsoft does not allow redistributing `makecat.exe` or `signtool.exe`, so **no download (neither Setup.exe nor the portable zip) contains any Microsoft SDK file**: you need the SDK for both tools.
- **The app downloads nothing. It makes no network connections.**

### Installing `makecat.exe` and `signtool.exe` (Windows SDK "Signing Tools for Desktop Apps")

The app needs `makecat.exe` and `signtool.exe` to build and sign the driver catalog. They are not shipped with the app, so it looks for them in your installed Windows SDK (`Program Files (x86)\Windows Kits\10\bin\<version>\x64\` and `Program Files\Windows Kits\10\bin\<version>\x64\`, the highest version that has the file; those folders can only be changed by administrators). It checks that the file it picked carries a valid Authenticode signature by Microsoft Corporation and refuses it otherwise (the signature must be EMBEDDED in the file: a file that is only catalog-signed is refused; the check uses the local certificate cache only and makes no network connection, so it also fails closed if an intermediate certificate is missing and not cached on this PC; any failure of the check is shown as the startup warning, never a crash). It never searches `PATH` or the current folder. (`7z.exe` and `innoextract.exe` are taken from `tools\` by location: that is why the app must be installed in `Program Files`.) To install the SDK tools, either:

- run `winget install --interactive Microsoft.WindowsSDK.10.0.28000` (the newest Windows SDK in winget at the time of writing; `Microsoft.WindowsSDK.10.0.26100` also works). Use `--interactive` so you can choose features: a silent winget install may install every feature, or
- download the installer from https://developer.microsoft.com/windows/downloads/windows-sdk/

and in the SDK installer choose only the signing tools: the feature is called "Windows SDK Signing Tools for Desktop Apps" in the installer's feature list, but the exact label may vary by SDK version (not verified for every SDK version); the files you need are `makecat.exe` and `signtool.exe`. Then restart the app.

If it is missing, the app still starts, and shows a startup warning that names `makecat.exe`, lists where it looked, and gives these install steps. Conversion is unavailable until you install it; **rolling back an earlier conversion still works** because it does not use these tools.

## How it works

1. Extracts both installers (7-Zip and innoextract, shipped in `tools\`).
2. Reads the device ID from the Lenovo package and confirms it matches this machine's GPU.
3. Patches the AMD INF and `amdgcf.dat` so the Legion Go hardware ID is accepted.
4. Builds a new catalog (`makecat`, from your Windows SDK) and signs it (`signtool`) with a local self-signed certificate (kept in your user's certificate store; nothing is trusted yet).
5. Shows exactly what it is about to change and waits for your confirmation. Nothing on the system changes before this. **Once you confirm, the run cannot be cancelled** (the result is always checked and a rollback is offered if it fails).
6. Creates a System Restore point (turning System Protection on for the system drive if it is off).
7. Makes sure Lenovo's original display driver is in the Windows driver store (from the retained copy), so there is always a way back.
8. Adds the certificate to the LocalMachine Trusted Root Certification Authorities and Trusted Publishers stores of this PC.
9. Installs the converted driver with `pnputil`.
10. Verifies the result (driver version and device problem code).
11. Optionally installs AMD Software (Adrenalin). An older AMD Software already on the PC is uninstalled first (Windows Installer cannot compare AMD's version numbers, such as 2026.1109, so it would refuse the upgrade); a newer one is kept. Rollback does not reinstall an older AMD Software that was replaced.
12. Tells you whether a restart is needed.

## What it does NOT do

- It does not enable Test Mode.
- It does not change boot settings or Secure Boot, and it does not turn off Memory Integrity.
- The result is not a WHQL-certified driver. It is AMD's driver with a locally signed catalog.
- It does not touch any other device.
- It does not redistribute AMD or Lenovo files: **nothing from AMD or Lenovo is bundled** in the app folder. You bring your own installers.

## Safety

- **Install it where only administrators can change it.** The app runs as administrator and starts the programs in its `tools\` folder, so copy the whole published folder to `C:\Program Files\LegionGoDriverPort\` (as administrator) and start it from there, not from `Documents` or `Downloads`. If its folder or `tools\` can be changed without administrator rights, the app shows a warning at startup saying so.
- A restore point is created before anything is installed.
- The app's data folder `C:\ProgramData\LegionGoDriverConverter\` is made writable by administrators only (users can read it). If that folder already exists and is owned by someone other than Administrators or SYSTEM, or is a link, the app refuses to use it and tells you to rename it as administrator; it never "repairs" a folder it cannot trust. This folder name, and the certificate subject below, are legacy identifiers kept unchanged from before the project was renamed, so existing installs, sessions, rollback data and the trusted certificate keep working.
- Lenovo's original display package is kept for rollback in `C:\ProgramData\LegionGoDriverConverter\lenovo-original\<version>\`, where `<version>` is the Lenovo INF's DriverVer (for Lenovo 25.30.44 that is `32.0.23044.19`) and the INF sits directly in that folder.
- Uninstalling with the installer asks whether to also delete this data folder (default No); a silent uninstall always keeps it. Keep it if you may still want to roll back.
- Every install run writes a log and a manifest to `C:\ProgramData\LegionGoDriverConverter\sessions\<timestamp>\` (an export-only run writes only a log there).
- The app has a Rollback button. It removes the converted driver packages of the last install (all packages of that AMD release, also ones that were already there from an earlier conversion or from the manual spike), puts the GPU back on Lenovo's driver from the retained package, removes the certificate trust when nothing signed by it is left, and then uninstalls the AMD Software that the run installed (a failure there is reported but does not undo the driver rollback). **It does not restore Lenovo's Microsoft Store app "AMD Radeon Software"**: reinstall it from the Microsoft Store if you want it. After a rollback the app checks what the GPU actually runs and tells you.
- **Rollback has not been tested on real hardware yet.** Best case: the GPU returns to Lenovo 25.30.44 with no error code after a restart. Worst case: Windows does not rebind the Lenovo driver by itself, and the GPU stays on the Microsoft Basic Display Adapter until you install the retained package by hand (Lenovo's display driver is not in this device's driver store beforehand, so the retained copy is the only source). From an administrator prompt:
  `pnputil /add-driver "C:\ProgramData\LegionGoDriverConverter\lenovo-original\32.0.23044.19\u0202094.inf" /install`
  (This is the path for Lenovo 25.30.44; the exact path is also in the session log and manifest, `LenovoInfPath`.) If Windows will not start normally, see the "Last resort" section of the validation checklist.
- Before you start: **save your BitLocker recovery key somewhere off the device**, and **keep the Lenovo installer** even though the app retains a copy.

### The certificate: what trusting it means

The conversion creates a self-signed code-signing certificate (`CN=Legion Go Driver Converter (local self-signed)`, 3072-bit RSA, valid 10 years) and adds its **public** part to LocalMachine **Trusted Root Certification Authorities** and **Trusted Publishers**. This is a machine-wide trust decision with a real consequence: **anything signed with that certificate's private key is trusted as signed code on this PC.**

- The private key lives in the current user's certificate store (`CurrentUser\My`) and is never written to a file, exported or uploaded by the app. A certificate the app creates itself is imported without the exportable flag. Anyone who can run code as that user can still use the key to sign, so treat the PC accordingly.
- **If a certificate with the same subject already exists, the app reuses it.** The certificate from the manual spike (thumbprint `777C7D47...`) was made with `New-SelfSignedCertificate`, whose key is exportable by default. The app warns about an exportable key in the log and in the confirmation; it never deletes your certificate. To replace it with a non-exportable one made by the app: first roll back (or otherwise remove every driver package signed by it: while one is installed, removing the certificate breaks that driver, Code 52), then delete "Legion Go Driver Converter (local self-signed)" from `certlm.msc` (Trusted Root Certification Authorities and Trusted Publishers) and from `certmgr.msc` > Personal (this deletes the key), and run the conversion again: it creates a new certificate.
- Remove the trust when you no longer need it. The app's Rollback untrusts it, unless driver packages signed by it are still in the driver store (then it stays trusted so those drivers keep working). To remove it by hand: run `certlm.msc` and delete "Legion Go Driver Converter (local self-signed)" from **Trusted Root Certification Authorities** and from **Trusted Publishers**; run `certmgr.msc` and delete it (and its key) from **Personal**. Do this after a successful rollback, or when you have decided not to use the converted driver any more. Do not remove it while the converted driver is installed: Windows would then report the driver as unsigned (Code 52).

## Known limitations

- **Smart App Control** blocks unsigned apps. An unsigned build of this exe will not run on a PC with Smart App Control on. Distributing it to such PCs needs a code-signing certificate that Microsoft trusts.
- Windows Update may replace the converted driver with another one. This is untested.
- Localized Windows: the `pnputil` output parsing is English-only.
- Tested only with AMD 26.10.2 over Lenovo 25.30.44.
- The on-device validation in [`../docs/VALIDATION-CHECKLIST.md`](../docs/VALIDATION-CHECKLIST.md) has not been completed yet; several behaviours are listed there as open questions.

## Build

Requires the .NET 8 SDK on Windows.

```powershell
.\build.ps1 -Zip                  # from the repository root: checks the SDK, provisions tools, tests, publishes, zips (see ../CONTRIBUTING.md)
dotnet test app                    # unit tests (no system changes)
app\tools\fetch-tools.ps1          # developer-time: provisions app\tools\ (the shipped app downloads nothing)
app\publish.ps1                    # self-contained single-file exe into app\publish\ (with tools\), then verifies it. No Microsoft SDK file
```

`fetch-tools.ps1` provisions no Microsoft file (the real-tool tests use your installed Windows SDK: set `LGO_TOOLS_DIR`, see `CONTRIBUTING.md`). It copies `7z.exe`/`7z.dll` from an installed 7-Zip and `innoextract.exe` from the winget package (paths can be passed as parameters). 7-Zip's Authenticode signature is checked when present (current builds are unsigned); innoextract is unsigned. Both are **trusted on first use**: their SHA-256 and version are printed and recorded. It also provisions the license texts into `app\tools\licenses\`: 7-Zip's `License.txt` is copied from the 7-Zip folder (the script fails if it is absent), and innoextract's zlib `LICENSE` is downloaded from the project's repository at the tag of the provisioned version (`https://raw.githubusercontent.com/dscharrer/innoextract/1.9/LICENSE`), with its SHA-256 pinned in the script (the tracked copy is reused; a mismatch or failed download stops the script and says what to do). The Boost, liblzma and libstdc++ licence texts that belong to innoextract's Windows binary are copied from its download into `licenses\innoextract\` with their SHA-256 pinned. `tools.lock.json` records a `category` for each file: `distributable` or `license`.

`app\tools\tools.lock.json` (name, version, SHA-256, source per file) lets later runs detect a changed or missing listed file and any unlisted exe/dll in `tools\`. It does **not** prove that 7-Zip or innoextract were genuine when first copied, it is not signed, and anyone who can edit `app\tools` can edit the lock too. Re-provisioning (`-Force`) compares the new files with the lock and stops with a warning showing old and new hashes unless you pass `-AcceptChangedTools`. The lock and `fetch-tools.ps1` are not copied into the published `tools\` folder.

`publish.ps1` publishes WITHOUT `IncludeNativeLibrariesForSelfExtract`: otherwise .NET extracts the WPF native DLLs into the user's `%TEMP%\.net\` and reuses an existing extraction, which code without administrator rights could plant. The native DLLs (`*_cor3.dll`) ship beside the exe instead, so keep the folder together. It checks, without launching the exe, that the embedded manifest requests `requireAdministrator`, that none of the known demo-mode markers are in the output (see the demo section), that the tools and the native DLLs are present, that every shipped `tools\` and `licenses\` file matches `tools.lock.json`, that the .NET runtime licence files were found, that the output contains **no** Microsoft SDK file (no `signtool`, `makecat` or companion file, anywhere), that `licenses\` and `THIRD-PARTY-NOTICES.md` are in the output, then lists every shipped file with its size and SHA-256. Copy the whole `app\publish\` folder to `C:\Program Files\LegionGoDriverPort\` to use it. **Never launch the published exe casually: it requests administrator and, if you complete the flow, changes your GPU driver.**

## Project layout

```
app/
  LegionGoConverter.sln
  src/LegionGoConverter.Core/     conversion logic: extraction, INF/DAT patching, catalog, signing, pipeline, rollback
  src/LegionGoConverter.App/      WPF UI (select, review, progress, result), gamepad navigation, debug-only demo mode
  tests/LegionGoConverter.Core.Tests/
  tools/fetch-tools.ps1           provisions tools\ (binaries are gitignored; tools.lock.json is tracked)
  publish.ps1                     publish + verify (fails if any Microsoft SDK file is in the output)
  THIRD-PARTY-NOTICES.md          notices for the bundled components (copied into the published folder)
docs/                             VALIDATION-CHECKLIST.md, design/ (historical spec and plan)
tools/spike/                      historical research scripts (do not run)
```

## Bundled tool licenses

The published folder (and so both downloads) contains third-party programs, and `THIRD-PARTY-NOTICES.md` and `licenses\` (7-Zip's `License.txt` **and** the LGPL 2.1 text, innoextract's LICENSE and its third-party licences in `licenses\innoextract\`, and `licenses\dotnet\` with the .NET runtime's LICENSE and THIRD-PARTY-NOTICES) ship with it, together with `LICENSE.txt` (MIT) and `BEFORE-YOU-INSTALL.txt` (the plain-language notice the installer shows before installing). **No Microsoft Windows SDK file and no end-user agreement is shipped.** **This is a summary of the developer's reading of the published terms, not legal advice; review before distributing.**

| Component | In the downloads? | License / terms |
|---|---|---|
| 7-Zip (`7z.exe`, `7z.dll`) | Yes | GNU LGPL, with the unRAR restriction for part of `7z.dll` and BSD licenses for other parts. Binary redistribution must reproduce 7-Zip's `License.txt` (shipped as `licenses\7-Zip-License.txt`), which refers to the LGPL without containing it, so the LGPL 2.1 text is shipped too (`licenses\LGPL-2.1.txt`). `THIRD-PARTY-NOTICES.md` offers the 7-Zip 26.06 source from https://www.7-zip.org/download.html. |
| innoextract 1.9 (`innoextract.exe`) | Yes | zlib license (the project's README says so; `LICENSE` shipped as `licenses\innoextract-LICENSE.txt`); its statically linked Boost (BSL 1.0), liblzma and libstdc++ (GPL 3 with the GCC Runtime Library Exception) licence texts are in `licenses\innoextract\`. https://constexpr.org/innoextract/ |
| `signtool.exe`, `makecat.exe` and the other Windows SDK files | **No, never** | Not redistributed by this project. You install the Windows SDK "Signing Tools for Desktop Apps" yourself, under Microsoft's own terms; the app uses your copies after checking their Microsoft signature. |
| .NET 8 runtime (inside the single-file exe) and the Microsoft DLLs beside it (`*_cor3.dll`) | Yes | Microsoft, MIT license for the runtime (`licenses\dotnet\`); the DLLs come with the .NET Desktop runtime under Microsoft's terms for it. |
| AMD and Lenovo files | **No, never** | You supply both installers yourself. |

## Debug-only demo mode (developers)

Debug builds can show each screen with fake services, for looking at layouts and taking screenshots. It touches nothing on the system: no pipeline, rollback, restart, file dialogs or device queries. It is compiled out of Release builds (`#if DEBUG`). `publish.ps1` scans the published exe and the intermediate Release assembly for 36 known demo markers (type names and strings, ASCII and UTF-16) and fails if one is found. That proves those markers are absent; it is not proof that no other debug-only code exists, and the scan depends on single-file compression being off (the script turns it off and says so). `publish.ps1 -ScanOnly <dll>` runs the scan on a single file, for example a Debug build, as a self-test.

```powershell
dotnet run --project app\src\LegionGoConverter.App -c Debug -- --demo=review --theme=dark --size=1280x800
```

States: `select`, `review`, `review-export`, `progress`, `result-ok`, `result-pending`, `result-fail`, `prompt`, `prompt-long`. Options: `--size=WxH` (device-independent pixels), `--theme=light|dark`. (The Debug manifest does not require administrator.)
