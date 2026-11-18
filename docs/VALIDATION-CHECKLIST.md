# On-device validation checklist

You run this yourself, on the Legion Go. It is never automated. Work top to bottom and write down what you see. The "Expected" lines are what the code and spike predict; any mismatch is a finding, so record it.

> Note: the certificate subject `Legion Go Driver Converter (local self-signed)` and the data folder `C:\ProgramData\LegionGoDriverConverter\` are legacy identifiers from before the project was renamed to Legion Go Driver Port. They are kept unchanged so existing installs, sessions, rollback data and the trusted certificate keep working.

## Stop and roll back if

Stop what you are doing and use the app's Rollback (or System Restore, or the retained Lenovo package) if any of these happens:

- Black or garbled screen for more than 30 seconds that does not recover.
- Device Manager shows the AMD GPU with Code 43 or Code 52 (or any other error code that does not clear after one restart).
- A blue screen (BSOD) at any point after the install.
- The app reports a failure and the GPU is not in the Lenovo 25.30.44 state afterwards.
- Secure Boot or Memory Integrity changed from what you noted in the preconditions.

## Step 0: the current state of this device (read first)

This Legion Go is **not** in the Lenovo 25.30.44 starting state the rest of this checklist was first written for:

- The GPU runs the **spike-converted AMD driver 32.0.32015.12012** (`oem10.inf`, original name `u0205499.inf`), signed by the spike's self-signed certificate (`CN=Legion Go Driver Converter (local self-signed)`, thumbprint `777C7D479879BCB167901701A72CABF29DD9BEB9`). That certificate is **trusted** in LocalMachine Trusted Root and Trusted Publishers, and its private key is in your `CurrentUser\My` store.
- The AMD 26.10.2 component packages from the spike (amdxe, amdafd, amdpcibridgeextension and the others of that release) are in the driver store too.
- **Lenovo's display package (`u0202094.inf`) is NOT in the driver store.** Lenovo's extension INF (`amduw23e.inf`, `oem54.inf`) still is.

What the **first run** of the app (with the same AMD 26.10.2 installer) will therefore do:

- It reuses the spike certificate, because it has the same subject (the app looks for that subject first). The confirmation will say the certificate is already trusted: no change. The app warns that this certificate's key is exportable (it was made by `New-SelfSignedCertificate`); see the README for replacing it later. It never deletes it.
- It copies Lenovo's display package to `C:\ProgramData\LegionGoDriverConverter\lenovo-original\32.0.23044.19\` and stages it into the driver store (store only: the active driver does not change).
- It finds the converted driver of this version already installed, signed by that certificate and running with problem code 0, so it does **not** add it again ("already converted" path). Verification still runs.
- The session manifest records the spike packages as "already present before this run".

What a **Rollback** after that first run will and will not do:

- It **will** remove every driver package of the AMD 26.10.2 conversion (the display package and all components of that release, including the spike's ones, because the display package of that version is signed by our certificate), with Lenovo's driver staged first from the retained copy, then untrust the certificate when nothing signed by it is left, and uninstall the AMD Software that this run installed (if any).
- It will **not** restore Lenovo's Microsoft Store app "AMD Radeon Software", and it will not touch Lenovo's extension (`oem54.inf`) or Lenovo-era packages of other versions.
- Lenovo's driver comes from the **retained copy** the run made. Windows has to rebind the GPU to it after the converted package is deleted (open question 1).
- The data folder `C:\ProgramData\LegionGoDriverConverter\` is created admin-only. If it already exists and is owned by your user account (not Administrators or SYSTEM), the app refuses to use it and says so: rename it as administrator and start again.

How to get to the starting state you want:

- **Lenovo 25.30.44 first (recommended for testing the full flow):** run the app once (the "already converted" path above, which also retains and stages Lenovo's package), then use its Rollback and restart. That is also the first real test of rollback, so do it with the BitLocker key and the "Last resort" section at hand. Record the result, then start this checklist at "Preconditions" from the Lenovo state.
- **Directly from the spike state:** go through the checklist as it is, but expect "already converted" in step 5 instead of a fresh install.

## Preconditions

- [ ] BitLocker recovery key saved **off the device** (printed or on another machine/phone). Confirm you can read it.
- [ ] USB keyboard (and ideally a mouse) available, in case the touch screen or controllers stop working.
- [ ] Power plugged in for the whole run.
- [ ] Lenovo's installer (Lenovo 25.30.44 legacy driver) and AMD's installer (26.10.2) in a known folder, for example `C:\LGC-test\`.
- [ ] Note the current state: Device Manager > Display adapters > AMD Radeon(TM) Graphics > Driver tab: version (`32.0.23044.19` = Lenovo 25.30.44 if you rolled back first, or `32.0.32015.12012` = the spike/converted driver, see Step 0), no error code. Note Secure Boot (msinfo32) and Memory Integrity (Windows Security > Device security > Core isolation) as **on** or **off**.
- [ ] Create a manual restore point yourself too ("Before Legion Go Driver Port") and note its name and time.
- [ ] Note that a Windows restore point may fail if System Protection is off for C:. Check it is on.
- [ ] Close other apps. Do not run during a pending Windows Update.

## 1. First launch

The normal way: run `LegionGoDriverPort-<version>-Setup.exe` as administrator (it shows a plain-language notice, then installs to `C:\Program Files\LegionGoDriverPort\`) and start the app from the Start menu. For the portable zip or a self-built folder instead: as administrator, copy the whole folder (exe, the `*_cor3.dll` files, `tools\`, `licenses\` and `THIRD-PARTY-NOTICES.md`) to `C:\Program Files\LegionGoDriverPort\`, and start the exe from there. Do not run it from `Documents` or `Downloads`: the app runs as administrator, so its folder must be one that only administrators can change.

- [ ] Expected: no startup warning about the app's folder. (Started from a user-writable folder, the app shows a warning naming the folder and telling you to move it.)

- [ ] A UAC prompt appears. Expected: yes (the app requires administrator). Accept.
- [ ] The window appears without errors.
- [ ] Controller navigation works: D-pad/stick moves focus, A activates, B goes back, and long text areas scroll with the stick.
- [ ] The `tools\` folder beside the exe contains only `7z.exe`, `7z.dll` and `innoextract.exe` (no Microsoft SDK file: no `signtool.exe`, no `makecat.exe`), and the folder also has `licenses\` and `THIRD-PARTY-NOTICES.md`. Nothing from AMD or Lenovo is in it. Expected: the app starts.
- [ ] `makecat.exe` and `signtool.exe`: no download contains them; the app takes them from your installed Windows SDK (`Program Files (x86)\Windows Kits\10\bin\<version>\x64\`) and refuses a file without a valid EMBEDDED Microsoft signature (a catalog-signed-only file is refused). Install with `winget install --interactive Microsoft.WindowsSDK.10.0.28000` and choose only the signing tools (the feature is called "Windows SDK Signing Tools for Desktop Apps"; the label may vary by SDK version), or from https://developer.microsoft.com/windows/downloads/windows-sdk/.
- [ ] If `makecat.exe` or `signtool.exe` is not found in the SDK, expected: the app starts and shows a startup warning naming `makecat.exe`, where it looked and the SDK install steps; conversion is unavailable, and Rollback still works. (Test this only on a PC where it is safe to hide the SDK; install it and restart the app afterwards.)
- [ ] Licensing, before giving a build to anyone else (summary; the full table is in `app\README.md`; not legal advice, review before distributing): 7-Zip is LGPL (+ unRAR restriction for part of `7z.dll`): `License.txt` and the LGPL 2.1 text ship (`licenses\`) and the notices offer the 7-Zip source; innoextract is zlib; no Microsoft Windows SDK file (`signtool.exe`, `makecat.exe`, companions) and no end-user agreement is shipped, and the downloads must contain none; the .NET runtime notices are in `licenses\dotnet\`.

## 2. Selecting files

- [ ] Pick Lenovo's installer in the Lenovo slot. Expected: it is identified as the Lenovo package (version 25.30.44).
- [ ] Pick the AMD installer in the AMD slot. Expected: identified as AMD 26.10.2.
- [ ] Swap them (AMD file in the Lenovo slot, Lenovo file in the AMD slot). Expected: each is rejected with a clear message saying which kind of file was expected. Nothing is changed.
- [ ] Put the correct files back.

## 3. Review screen

- [ ] The device ID shown matches `PCI\VEN_1002&DEV_15BF&SUBSYS_381217AA&REV_04`.
- [ ] Secure Boot and Memory Integrity are shown and match what you noted in the preconditions.
- [ ] The consent text can be reached and scrolled with the controller.

## 4. Start conversion

- [ ] Start. Expected: the progress screen steps through stages (extract, patch, catalog, sign) without changing the system yet.
- [ ] A confirmation sheet lists the changes about to be made (restore point, including turning System Protection on if it is off; Lenovo's driver into the driver store; certificate trust; driver install; AMD Software if chosen) and says that once confirmed the run cannot be cancelled. If the reused certificate's key is exportable (the spike certificate, see Step 0), it says so.
- [ ] **Decline.** Expected: nothing is changed. Check Device Manager driver version is unchanged and that no new certificate or driver was installed (`pnputil /enum-drivers` does not list a new `oem*.inf` for the AMD driver).
- [ ] Start again and note anything different on the second attempt.

## 5. Full install

With "Install AMD Software" on, confirm the sheet and let it run. Do not touch the device while it runs. Record what you see at each stage.

- [ ] After you confirm, the Cancel button is replaced by "Installing: cannot be cancelled"; B / Esc do nothing.
- [ ] Restore point created (check System Restore afterwards).
- [ ] "Keep Lenovo's driver available" completes: `pnputil /enum-drivers` afterwards lists `u0202094.inf` (Lenovo's display driver) even though the GPU does not use it.
- [ ] Certificate trust step completes (on this device: "already trusted", see Step 0). Understand what this does: the app's self-signed certificate (`Legion Go Driver Converter (local self-signed)`) is trusted machine-wide in LocalMachine **Trusted Root Certification Authorities** and **Trusted Publishers**, so anything signed with its private key is trusted as signed code on this PC. The private key stays in your user's certificate store (`certmgr.msc` > Personal) and is never written to a file by the app. A certificate the app creates itself is non-exportable; the reused spike certificate's key probably is exportable. Remove the trust when you are done (see "Remove the certificate" at the end).
- [ ] Driver install step completes. The screen may flicker or go black briefly. Expected: it recovers within 30 seconds.
- [ ] Verification result shown. Expected: success, or a clear "installed but not verified yet, restart" notice.
- [ ] AMD Software step completes (or reports it was already present).
- [ ] The app says whether a restart is needed. Restart when told to. If it does not say so, still restart once.

## 6. After the reboot

- [ ] Device Manager > Display adapters > AMD Radeon(TM) Graphics: driver version **32.0.32015.12012**, no error code ("This device is working properly").
- [ ] Secure Boot unchanged from the preconditions.
- [ ] Memory Integrity unchanged from the preconditions.
- [ ] Legion Space opens, and these work:
  - [ ] TDP changes
  - [ ] Controllers (both, including detaching and reattaching if you use that)
  - [ ] Refresh rate switches between 60 Hz and 144 Hz
  - [ ] Brightness
  - [ ] Sleep and wake (two cycles)
  - [ ] USB-C monitor output
- [ ] A game launches and runs normally.
- [ ] AMD Software (Adrenalin) opens.

## 7. Re-run on an already converted system

- [ ] Run the app again with the same two files. Expected: it recognises the current state, does not create duplicate restore points beyond what it reports, does not add a second copy of the certificate, does not install AMD Software a second time, and ends in the same good state.
- [ ] Check Certificates (Local Machine > Trusted Root and Trusted Publishers) for exactly one entry of the app's certificate.
- [ ] `pnputil /enum-drivers` shows no unexpected duplicate AMD display driver entries.

## 8. Rollback

**Rollback is untested on real hardware.** Lenovo's display driver is not in this device's driver store before the conversion, so rollback depends on the package the app retained and on Windows rebinding the GPU to it after the converted driver is removed (open question 1). Do not assume the best case.

- [ ] Use the app's Rollback button. Expected: it asks for confirmation (and says Lenovo's Store app "AMD Radeon Software" is not restored), removes all converted AMD 26.10.2 driver packages (also the spike's, see Step 0), uninstalls the AMD Software the install run added, re-checks the GPU and tells you what it runs, and asks for a restart.
- [ ] `pnputil /enum-drivers`: no `u0205499.inf` 32.0.32015.12012 and no AMD 26.10.2 component packages left; `u0202094.inf` and `amduw23e.inf` (oem54) still there.
- [ ] Restart. **Best case:** Device Manager shows Lenovo **25.30.44** (driver version `32.0.23044.19`) with no error code, Secure Boot / Memory Integrity unchanged, and Legion Space still works.
- [ ] **Worst case:** the GPU shows as **Microsoft Basic Display Adapter** (low resolution, no 144 Hz). The retained Lenovo package then has to be installed by hand. The INF sits directly in the version folder (the version is the Lenovo INF's DriverVer, `32.0.23044.19` for Lenovo 25.30.44; the session manifest's `LenovoInfPath` has the exact path). From an administrator PowerShell or Command Prompt:
  `pnputil /add-driver "C:\ProgramData\LegionGoDriverConverter\lenovo-original\32.0.23044.19\u0202094.inf" /install`
  then restart and check Device Manager again. Record which case happened.
- [ ] Reinstall "AMD Radeon Software" from the Microsoft Store if you want Lenovo's app back.
- [ ] If you want to continue testing, run the conversion again from this state.

## Last resort: Windows will not start normally or the screen stays black

Have the BitLocker recovery key at hand first; Recovery Environment and Safe Mode can ask for it.

1. Wait 30 to 60 seconds, then hold the power button to switch off. Use the USB keyboard.
2. Reach Windows Recovery Environment: hold **Shift** while choosing Restart (if you can reach the sign-in screen), or power-cycle the device three times in a row during start-up (power on, and switch off as soon as the logo appears) until "Preparing Automatic Repair" appears.
3. Troubleshoot > Advanced options > **Startup Settings** > Restart > press **4** or **F4** for **Safe Mode**. Safe Mode uses the basic display driver, so a broken GPU driver does not block it.
4. In Safe Mode you can: run the app's Rollback only if it starts there (not guaranteed, it may need services Safe Mode lacks); or run `pnputil /add-driver "C:\ProgramData\LegionGoDriverConverter\lenovo-original\32.0.23044.19\u0202094.inf" /install` (the command from the Worst case above); or uninstall the converted driver in Device Manager (tick "Attempt to remove the driver for this device").
5. If that does not work: Troubleshoot > Advanced options > **System Restore** and pick the restore point created just before the conversion (the app's, and the one you made manually).
6. Afterwards, record what happened and send back the session folder and log.

## Remove the certificate (when you are done)

The conversion trusts its self-signed certificate machine-wide. Remove it once you have rolled back or no longer want the converted driver. Do not remove it while the converted driver is installed (Windows would then report the driver as unsigned, Code 52).

- The app's Rollback untrusts it, unless driver packages signed by it remain in the driver store (then it deliberately stays).
- By hand: `certlm.msc` > delete "Legion Go Driver Converter (local self-signed)" from **Trusted Root Certification Authorities** and from **Trusted Publishers**. Then `certmgr.msc` > **Personal** > Certificates > delete it there too (this deletes the private key).
- [ ] After rollback, confirm in `certlm.msc` that it is gone from both machine stores.

## Open questions the code could not settle

Write down the exact behaviour for each:

1. After `pnputil /delete-driver /uninstall`, does Windows rebind the GPU to the staged Lenovo driver by itself, or does the GPU sit with no driver until something else installs one? Evidence so far: in the spike, Windows **did** rebind the GPU to Lenovo's `oem55.inf` after `/delete-driver /uninstall` while Lenovo's package was in the driver store; a package staged by the app from the retained copy is the same situation, but this has not been observed yet.
2. What exit code does `pnputil /add-driver /install` return when it adds the packages but updates no device? (259, ERROR_NO_MORE_ITEMS, is expected but unverified.) The app now treats 259 from the install as "added", logs it as a warning, and lets verification decide: note whether you saw it in the session log.
3. Does a restart-pending result (exit 3010, or the "installed but not verified" notice) actually occur in a real run, and is the driver correct after the restart?
4. What happens after a Windows Update: does it replace the converted driver, offer a different one, or leave it alone? Check Windows Update > Update history and Device Manager after the next cumulative update.
5. With System Protection off for C:, does `Get-ComputerRestorePoint` fail (the app then logs a warning and still runs `Enable-ComputerRestore` + `Checkpoint-Computer`, and verifies the new point)? Note what the session log says at the restore point stage.
6. Does controller navigation (Windows.Gaming.Input) work while the app runs elevated (as administrator)? This has not been verified on the device.
7. Does innoextract 1.9 (bundled) extract the Lenovo installer correctly? Future Lenovo installers built with Inno Setup 6.3 or later may not be supported by innoextract 1.9; record the version shown by the extraction step if it fails.

## What to record and send back

- The whole session folder: `C:\ProgramData\LegionGoDriverConverter\sessions\<timestamp>\` (the log and the manifest). Zip it.
- For each step above: pass or fail, and for any failure the exact message on screen (a photo is fine).
- Device Manager driver version and problem code before and after each stage (conversion, reboot, rollback).
- Answers to the open questions.
- Anything odd: flicker lengths, long waits, controller or sleep quirks.
