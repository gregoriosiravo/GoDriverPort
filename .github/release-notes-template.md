## Legion Go Driver Port {{VERSION}}

> [!CAUTION]
> **Unofficial community tool. Use at your own risk.** Not affiliated with AMD, Lenovo or Microsoft. It modifies a graphics driver and adds a locally generated, self-signed certificate to this PC's trusted root store. Only the **first-generation Lenovo Legion Go (Ryzen Z1 Extreme)** is supported. **Back up your BitLocker recovery key first**, and read the [on-device checklist](https://github.com/{{REPO}}/blob/{{TAG}}/docs/VALIDATION-CHECKLIST.md) (including "Last resort") before you start. The on-device validation is not complete.

### Downloads

- `LegionGoDriverPort-{{VERSION}}-Setup.exe`: installer (recommended; asks for administrator rights and shows a plain-language notice and the MIT licence before installing).
- `LegionGoDriverPort-{{VERSION}}-portable.zip`: portable folder with the same contents (extract and copy to `C:\Program Files\LegionGoDriverPort\` as administrator; do not run it from Downloads)
- `SHA256SUMS.txt`: SHA-256 hashes of the two files above

### These files are unsigned

- **SmartScreen** will warn: choose **More info**, then **Run anyway**.
- **Smart App Control** blocks unsigned apps with no override. It can only be turned off, and on current Windows 11 builds, once turned off it cannot be turned back on without reinstalling Windows.
- Code signing may be added later. Until then, verify the hash (see below) and download only from this repository's Releases page.

### Verify the download

```powershell
Get-FileHash .\LegionGoDriverPort-{{VERSION}}-Setup.exe -Algorithm SHA256
```

The hash must equal the matching line in `SHA256SUMS.txt`.

### Prerequisite: Windows SDK signing tools

**Neither download contains any Microsoft file.** `signtool.exe` and `makecat.exe` cannot be redistributed by this project, so install the free Windows SDK signing tools once:

```powershell
winget install --interactive Microsoft.WindowsSDK.10.0.28000
```

Choose only "Windows SDK Signing Tools for Desktop Apps".

### Good to know

- Legion Go Driver Port is an unofficial community tool, not affiliated with or endorsed by Lenovo or AMD. Lenovo, Legion, AMD, Radeon and Adrenalin are trademarks of their owners.
- Built on .NET 8, which went out of support on 2026-11-10 (the self-contained build carries the final runtime, 8.0.34). Moving to .NET 10 is planned.
- You supply Lenovo's and AMD's driver installers yourself; nothing from them is bundled. The app makes no network connections.
- Uninstalling the app does **not** roll back the driver or remove the certificate. Use **Rollback** in the app first.
- Which driver versions were tested is stated in the README (`Tested configuration`); check it before you start.
- Bundled third-party components and their licences: `THIRD-PARTY-NOTICES.md` (in the install folder and in the repository).
