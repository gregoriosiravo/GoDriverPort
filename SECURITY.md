# Security policy

## What the tool does that matters for security

Legion Go Driver Port runs **elevated** (it requests administrator rights) and, once you confirm, changes trust and drivers on your PC:

- It creates a **self-signed code-signing certificate** (`CN=Legion Go Driver Converter (local self-signed)`, 3072-bit RSA) on your machine. The **private key** stays in the current user's certificate store (`CurrentUser\My`), is not marked exportable when the app creates it, and is never written to a file or sent anywhere. (The certificate subject and the data folder name `C:\ProgramData\LegionGoDriverConverter` are legacy identifiers from before the project was renamed; they are kept unchanged so existing installs, rollback data and trusted certificates keep working.)
- It adds the certificate's **public** part to the LocalMachine **Trusted Root Certification Authorities** and **Trusted Publishers** stores. That means Windows will trust anything signed with that key on this PC. Rollback removes the trust again (unless converted driver packages signed by it are still installed). See "The certificate: what trusting it means" in [`app/README.md`](app/README.md).
- It signs a converted AMD driver catalog with that key and installs the package with `pnputil`.
- It runs the helper programs in its `tools\` folder (7-Zip and innoextract), and signtool and makecat from your Windows SDK (checked for a valid Microsoft signature before use). Install the app where only administrators can write (the installer uses `C:\Program Files\...`), otherwise a non-admin program could replace them.
- It makes **no network connections** and downloads nothing.

Releases are currently **unsigned** (no Authenticode signature on the installer or the exe). Verify the SHA-256 in `SHA256SUMS.txt` against the file you downloaded, and only download from this repository's Releases page.

## Supported versions

Only the latest release is supported with security fixes.

## Reporting a vulnerability

Please report suspected vulnerabilities **privately** using GitHub's private vulnerability reporting: open the repository's **Security** tab and choose **Report a vulnerability**. Do not open a public issue or pull request for a vulnerability.

Include the app version, what you found, how to reproduce it, and the impact you expect. You can expect an acknowledgement as soon as a maintainer can respond; this is a volunteer project and no response-time guarantee is made.

Examples of in-scope problems: a way for a non-administrator to make the app run attacker-controlled code, a way to read or export the signing key, the app trusting a certificate other than the one it created, path or link-following problems in its data folder (`C:\ProgramData\LegionGoDriverConverter`), or a bypass of the exact-device check.

Not security vulnerabilities: the fact that the tool changes driver trust by design (read the README first), or a driver that does not work after conversion (open a normal bug report).
