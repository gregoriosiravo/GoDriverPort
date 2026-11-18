# Contributing

Thanks for helping. This project changes graphics drivers and machine trust, so changes are reviewed carefully and tested through fakes: **no test may touch the real driver store, certificate stores, restore points, `pnputil` or `msiexec`.**

**Never include AMD or Lenovo files in a pull request or issue** (installers, extracted drivers, INF/CAT/SYS/DAT files, logs containing them). Never paste BitLocker keys, certificates or private keys.

## Requirements

- Windows 11 x64
- .NET 8 SDK (`global.json` pins the 8.0 line)
- For tests that use real signing tools: the Windows SDK "Signing Tools for Desktop Apps" (`winget install --interactive Microsoft.WindowsSDK.10.0.28000`)
- To build a distributable folder: 7-Zip (https://www.7-zip.org/) and innoextract (`winget install dscharrer.innoextract`)
- To build the installer: Inno Setup 6 (https://jrsoftware.org/isinfo.php)

## Build and test

```powershell
dotnet build app -c Release
dotnet test app                 # no system changes; tests that need real packages are skipped when their variables are unset
```

Or in one go from the repository root: `.\build.ps1` (see below).

Keep the build free of new warnings. (The one known warning is xUnit1026 in `EdgeCaseTests`.)

### Tests that need real packages (optional)

These tests only **read** files. When their variable is not set they are **skipped**, not failed. There are no default paths. Set the variables in `app/tests/local.runsettings` once (copy `app/tests/local.runsettings.example`, which is the template; the real file is git-ignored) and run:

```powershell
dotnet test app --settings app/tests/local.runsettings
```

| Variable | Points to | Tests skipped without it |
|---|---|---|
| `LGO_AMD_ROOT` | extracted AMD driver installer (contains `Packages\Drivers\Display\WT6A_INF`) | real AMD package parsing and patching |
| `LGO_LENOVO_ROOT` | extracted Lenovo Legion Go driver package | real Lenovo package parsing |
| `LGO_AMD_SOFTWARE_ROOT` | extracted AMD Software payload (`cnext\...`, `amddvr\...`) | reading the real AMD Software MSIs |
| `LGO_INSTALLERS_DIR` | folder with `driver_amd7840U.exe` and `driver_lenovo.exe` (also needs 7-Zip and innoextract on PATH) | end-to-end extraction of real installers |
| `LGO_TOOLS_DIR` | folder with real `makecat.exe` and `signtool.exe`, normally your installed Windows SDK `...\Windows Kits\10\bin\10.0.xxxxx.0\x64` (install: `winget install --interactive Microsoft.WindowsSDK.10.0.28000`) | real catalog build and signing (CurrentUser certificate only, deleted afterwards) |

You obtain the AMD and Lenovo installers yourself from their vendors; they are never part of this repository.

## Developer tools

```powershell
.\app\tools\fetch-tools.ps1   # provisions app\tools\ (7z, innoextract and licence texts; no Microsoft files); finds 7-Zip and innoextract in the usual places
.\app\publish.ps1             # self-contained single-file build into app\publish\, then verifies it (never launches it)
```

`fetch-tools.ps1` provisions no Microsoft file; 7-Zip and innoextract are trusted on first use and recorded in the tracked `app/tools/tools.lock.json`. If your 7-Zip version differs from the one recorded there, it stops and tells you; re-run with `-AcceptChangedTools` once you have reviewed the change.

## Demo mode (Debug builds only)

Debug builds can show every screen with fake services, for checking layouts. It touches nothing on the system and is compiled out of Release builds.

```powershell
dotnet run --project app\src\LegionGoConverter.App -c Debug -- --demo=review --theme=dark --size=1280x800
```

States: `select`, `review`, `review-export`, `progress`, `result-ok`, `result-pending`, `result-fail`, `prompt`, `prompt-long`.

## Code layout

```
app/src/LegionGoConverter.Core/   conversion logic (extract, patch INF and amdgcf.dat, catalog, signing, install, rollback); every system effect is behind an interface
app/src/LegionGoConverter.App/    WPF UI, gamepad navigation, Debug-only demo mode
app/tests/LegionGoConverter.Core.Tests/   xUnit tests (fakes for all system effects)
app/tools/                        developer-time provisioning of the bundled helper tools (binaries git-ignored, lock file tracked)
app/publish.ps1                   publish + verify
installer/                        Inno Setup script and the plain-language pre-install notice
docs/                             VALIDATION-CHECKLIST.md (on-device test), design/ (historical design and plan)
tools/spike/                      historical research scripts: do not run
build.ps1                         one-command build from a clone
```

## Pull requests

- Keep a change focused, add or update tests, and fill in the pull request template.
- Every pull request needs the maintainer's approval (`.github/CODEOWNERS` requests it automatically). Repository: https://github.com/gregoriosiravo/LegionGoDriverPort.
- Anything that changes what the app does to the system must be reflected in `app/README.md` and `docs/VALIDATION-CHECKLIST.md`.
- Do not add dependencies or bundled binaries without updating `app/THIRD-PARTY-NOTICES.md` and discussing the licence.

## Building the installer and a release

```powershell
.\build.ps1 -Installer -Zip      # output in dist\: Setup.exe and portable .zip (both from app\publish: no Microsoft SDK files), SHA256SUMS.txt
```

### No Microsoft files in any download

No downloadable artifact may contain a Microsoft SDK file: Setup.exe and the portable zip both contain only the app, 7-Zip, innoextract and licence texts. The app uses `signtool.exe` and `makecat.exe` from the user's own Windows SDK. `publish.ps1` fails if any such file is in `app\publish`, `build.ps1` re-checks the zip, and the installer script refuses to compile otherwise. Do not add Microsoft SDK files to the build.

The version comes from `Directory.Build.props` (`<Version>`). `-Installer` needs Inno Setup 6 (`ISCC.exe`).

### Owner-only steps (repository settings and decisions; the code cannot do these)

- Enable **private vulnerability reporting** (Settings > Code security), which `SECURITY.md` relies on.
- **Protect the `v*` tags** (Settings > Rules > Rulesets: restrict who can create/update/delete tags matching `v*`), and optionally add an `environment:` with required reviewers to the `release` job of `release.yml` (the workflow does not enforce one). The workflow itself refuses a tag whose commit is not reachable from `origin/main`.
- Do a **manual `workflow_dispatch` dry run** of the Release workflow before the first tag. It builds everything and keeps the files as workflow artifacts without creating a release. The Chocolatey Inno Setup version pin and the tool downloads have not been exercised on a runner yet.
- Decide whether to **remove personal data from the git history** (older commits contain local paths and the author identity). The tracked files are clean.
- Apply for free open-source code signing (for example SignPath Foundation); releases are unsigned until then.

### Release build determinism

The release workflow builds with exactly .NET SDK 8.0.428 (it temporarily overrides `global.json` with `rollForward: disable`), because the SDK decides which .NET runtime the self-contained exe embeds: 8.0.428 bundles runtime 8.0.34, which `app/tools/tools.lock.json` and `fetch-tools.ps1` (`$DotnetVersion`) pin together with the runtime licence files. To move to a newer runtime, change the SDK version in `release.yml`, `$DotnetVersion` in `fetch-tools.ps1`, restore the new packs (`dotnet restore app/src/LegionGoConverter.App -r win-x64 -p:SelfContained=true`) and re-provision with `-Force -AcceptChangedTools`. The GitHub actions are pinned to commit SHAs (with the tag in a comment); update them deliberately.

### Cutting a release (maintainers)

1. Update `<Version>` in `Directory.Build.props` and commit it.
2. Tag and push the tag (this starts the release workflow, which builds, tests, compiles the installer and creates the GitHub Release with `Setup.exe`, the portable `.zip` and `SHA256SUMS.txt`):

   ```powershell
   git tag -a v0.1.0 -m "v0.1.0"
   git push origin v0.1.0
   ```

   The tag must equal `v<Version>`; the workflow fails otherwise. The workflow can also be run manually (Actions > Release > Run workflow) on a branch: that builds and tests everything and uploads the files as workflow artifacts, but does not create a release. (Started on a tag ref, a manual run does create the release.)
3. Edit the release notes if needed. The template is `.github/release-notes-template.md` (disclaimers: unsigned, one device, risks, the SDK signing tools prerequisite, how to verify the download).

Releases are unsigned for now; see `SECURITY.md` and the README.

## Conduct

Be kind and assume good faith. Hardware changes can brick a device; be honest about what you tested.
