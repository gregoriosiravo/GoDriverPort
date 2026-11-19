# Legion Go Driver Port

A Windows app that takes AMD's current Radeon 780M graphics driver and converts it so that it installs on the **Lenovo Legion Go** (first model, Ryzen Z1 Extreme), whose GPU is otherwise stuck on Lenovo's older OEM driver. You provide Lenovo's and AMD's installers; the app patches the AMD driver, signs it with a local self-signed certificate, installs it, and can roll everything back.

> [!CAUTION]
> **Read this before you use it. Use at your own risk.**
>
> - This is an **unofficial, community tool**. It is **not affiliated with, endorsed by or supported by AMD, Lenovo or Microsoft.** Lenovo, Legion, AMD, Radeon and Adrenalin are trademarks of their respective owners.
> - It **modifies a graphics driver** and **adds a locally generated, self-signed certificate to this PC's trusted root certificate store** (and Trusted Publishers). While trusted, Windows accepts anything signed with that certificate's key on this PC. The converted driver is **not** WHQL-certified.
> - A bad driver can leave you with a black screen or a PC that does not start normally. **Back up your BitLocker recovery key somewhere off the device first**, keep Lenovo's installer, and read the [on-device checklist](docs/VALIDATION-CHECKLIST.md) (including "Last resort") before you start.
> - **Only the exact first-generation Lenovo Legion Go (Ryzen Z1 Extreme) is supported.** The app refuses other hardware. Legion Go S, Legion Go 2 and other handhelds are not supported.
> - Tested configuration at the time of writing: AMD 26.10.2 over Lenovo 25.30.44. The on-device validation is not complete; several behaviours are listed there as open questions. Rollback has not been proven on every configuration.
> - No warranty. See [LICENSE](LICENSE) and the licence text shown by the installer.

## Get it

### Option 1: download a release (recommended)

1. Go to the [Releases](https://github.com/gregoriosiravo/LegionGoDriverPort/releases) page and download `LegionGoDriverPort-<version>-Setup.exe` (installer) or `...-portable.zip`.
2. Check the file against `SHA256SUMS.txt` from the same release:

   ```powershell
   Get-FileHash .\LegionGoDriverPort-<version>-Setup.exe -Algorithm SHA256
   ```

3. Run the installer (it needs administrator rights), read the notice it shows and accept the licence. For the portable zip, extract it and copy the whole folder to `C:\Program Files\LegionGoDriverPort\` (as administrator), then start it from there; do not run it from Downloads or Documents, because the app starts programs from its `tools\` folder.

**Releases are unsigned for now**, so Windows will warn you:

- **SmartScreen** ("Windows protected your PC"): choose **More info**, then **Run anyway**.
- **Smart App Control** blocks unsigned apps outright, with no "run anyway". If Smart App Control is on, the app will not start. Smart App Control can only be switched off, and **on current Windows 11 builds, once off it cannot be turned back on without reinstalling Windows**, so think carefully before doing it. This is a real cost of using an unsigned build; code signing may be added later.

Which package contains what (not legal advice):

- **Setup.exe** (installer) and **portable.zip** contain the same things: the app, 7-Zip, innoextract and their licence texts. **Neither contains any Microsoft Windows SDK file** (no `signtool.exe`, no `makecat.exe`), and nothing from AMD or Lenovo. You install the Windows SDK "Signing Tools for Desktop Apps" yourself (see Requirements); the app finds `signtool.exe` and `makecat.exe` in your installed SDK.
- **Uninstalling** with the installer asks whether to also delete the app's data folder (`C:\ProgramData\LegionGoDriverConverter`, a legacy name kept for compatibility with existing installs; it holds the session logs and the copy of Lenovo's original driver that Rollback needs). The default is No (keep it), and a silent uninstall always keeps it.

### Option 2: build from source

See [Build from source](#build-from-source).

## Requirements

- Windows 11 (64-bit), administrator rights.
- Two installers you download yourself: Lenovo's Legion Go display driver installer and AMD's Radeon 780M (Ryzen 7040 series) driver installer. Nothing from AMD or Lenovo is bundled, and the app makes no network connections.
- **The free Windows SDK signing tools** (`signtool.exe` and `makecat.exe`): Microsoft does not allow redistributing them, so the downloads contain no Microsoft files and you install them yourself once. The installer and the app tell you when they are missing. Install them with:

  ```powershell
  winget install --interactive Microsoft.WindowsSDK.10.0.28000
  ```

  Use `--interactive` and select only the "Windows SDK Signing Tools for Desktop Apps" feature. Details: [`app/README.md`](app/README.md#installing-makecatexe-windows-sdk-signing-tools-for-desktop-apps).

- To **build** from source only: the .NET 8 SDK, 7-Zip and innoextract (`winget install dscharrer.innoextract`); Inno Setup 6 for the installer.

## How it works, in short

Extract both installers, read the Lenovo device ID and check it matches this PC, patch the AMD INF and `amdgcf.dat`, build and sign a new catalog with a local self-signed certificate, show you exactly what will change and ask for confirmation, create a System Restore point, keep Lenovo's original driver in the driver store for the way back, trust the certificate, install the driver with `pnputil`, and verify the result. Full details, limits and the certificate explanation: [`app/README.md`](app/README.md).

**Rollback:** the app has a Rollback button. **Uninstalling the app does not roll back the driver and does not remove the certificate.** Use Rollback first.

## Build from source

Prerequisites: Windows 11, the .NET 8 SDK, and (for the helper tools bundled in the app folder) 7-Zip installed and innoextract (`winget install dscharrer.innoextract`). A fresh clone is enough: `build.ps1` restores the .NET runtime packs it needs.

```powershell
git clone https://github.com/gregoriosiravo/LegionGoDriverPort.git
cd LegionGoDriverPort
.\build.ps1                       # checks the SDK, restores the runtime packs, provisions app\tools, runs the tests, publishes app\publish\
.\build.ps1 -Zip                  # also writes the portable zip and SHA256SUMS.txt
.\build.ps1 -Installer -Zip       # also compiles the Inno Setup installer (needs Inno Setup 6: https://jrsoftware.org/isinfo.php)
```

Windows PowerShell's default execution policy (`Restricted`) refuses to run scripts, so on a clean Windows 11 client start it as `powershell -ExecutionPolicy Bypass -File .\build.ps1 -Zip` (or use PowerShell 7).

`-SkipTests` skips the test run; `-SkipTools` skips provisioning when `app\tools` is already set up. Output goes to `dist\`. No build contains any Microsoft Windows SDK file. More in [CONTRIBUTING.md](CONTRIBUTING.md).

## Runtime note

Version 0.1.1 targets **.NET 8**, which went out of support on 2026-11-10; the self-contained build carries the final runtime, 8.0.34, and will receive no further runtime security fixes. Moving to .NET 10 (LTS) is planned.

## Documentation

- [`app/README.md`](app/README.md): what the app does, safety, the certificate, known limitations
- [`docs/VALIDATION-CHECKLIST.md`](docs/VALIDATION-CHECKLIST.md): step-by-step on-device test, rollback, last resort
- [`app/THIRD-PARTY-NOTICES.md`](app/THIRD-PARTY-NOTICES.md): licences of bundled components
- [`CONTRIBUTING.md`](CONTRIBUTING.md), [`SECURITY.md`](SECURITY.md)
- [`docs/design/`](docs/design/): original design and implementation plan (historical)

## Code signing policy

Releases are not code-signed yet. This section is the policy that will apply once they are (free code signing provided by [SignPath.io](https://signpath.io), certificate by the [SignPath Foundation](https://signpath.org/foundation)).

- Only release files built by the [Release workflow](.github/workflows/release.yml) from a `v*` tag on `main` in this repository are submitted for signing. Local or fork builds are never signed.
- **Committers and reviewers / approvers:** [@gregoriosiravo](https://github.com/gregoriosiravo), the repository owner and sole maintainer (see [`CODEOWNERS`](.github/CODEOWNERS)). Every change to `main` needs the maintainer's review.
- Each signing request is approved manually by the maintainer.
- The signed files are the app executable and `Setup.exe`. The SHA-256 of every release file is published in `SHA256SUMS.txt`.
- This is separate from the self-signed certificate the app creates on your PC to sign the converted driver (see [`app/README.md`](app/README.md)); that one is never shared or uploaded.

## Privacy

The app does not collect, store remotely or send any data, and contains no telemetry, analytics, update check or network code. It works on your PC only:

- It reads the installers you point it to and your PC's hardware IDs, to check the driver matches your device.
- Its working files and session logs stay on your PC in `C:\ProgramData\LegionGoDriverConverter` and are never uploaded. Logs can include file paths and device IDs, so look them over before attaching them to a bug report.
- The only link it shows is the Microsoft page to download the Windows SDK, which you open yourself.

## License

The source code is under the [MIT License](LICENSE). The released app bundles third-party components under their own licences; see [`app/THIRD-PARTY-NOTICES.md`](app/THIRD-PARTY-NOTICES.md). AMD, Radeon, Adrenalin, Lenovo, Legion, Microsoft and Windows are trademarks of their respective owners. Legion Go Driver Port is an unofficial community tool, not affiliated with or endorsed by Lenovo or AMD.
