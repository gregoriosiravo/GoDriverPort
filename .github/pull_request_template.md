## What and why

<!-- A short description of the change and the problem it solves. Link the issue if there is one. -->

## Checklist

- [ ] `dotnet test app` passes (tests that need real driver packages are skipped without the `LGO_*` variables; that is fine)
- [ ] No new compiler warnings
- [ ] This PR contains **no AMD or Lenovo files** (installers, extracted drivers, INF/CAT/SYS/DAT files) and no BitLocker keys, certificates or private keys
- [ ] No personal paths or personal data in code, tests, docs or logs
- [ ] If this changes what the app does to the system (driver store, certificates, restore points, rollback), the README and `docs/VALIDATION-CHECKLIST.md` are updated, and the change was exercised through the test fakes
- [ ] If this changes the bundled third-party files, `app/THIRD-PARTY-NOTICES.md` and `app/tools/tools.lock.json` are updated

## Tested on

<!-- Windows build, and whether you ran it on a Legion Go. Never claim a hardware test you did not do. -->
