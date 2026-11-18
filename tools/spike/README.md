# tools/spike: historical research scripts

These PowerShell scripts are the early feasibility spike that preceded the application in `app/`. They are kept
only to document how the approach was explored.

**Do not run them.** They are unsupported, and they CHANGE THE SYSTEM: depending on the script they install or
remove drivers with `pnputil`, install or uninstall software with `msiexec`, create certificates and change
certificate trust, or alter display settings. They have no tests, no safety checks comparable to the app, and no
warranty (see `../../LICENSE`).

Paths are parameters (there are no default locations on anyone's machine). The supported way to convert a driver
is the application: see `../../README.md` and `../../app/README.md`.
