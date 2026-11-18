# Third-party notices

Legion Go Driver Port ships with the components below. Nothing from AMD or Lenovo is included: you supply those
installers yourself, and the app makes no network connections.
Project: https://github.com/gregoriosiravo/LegionGoDriverPort

**No Microsoft Windows SDK file is included in any download** (neither `signtool.exe`, `makecat.exe`, nor any companion file).
The app uses the copies from the Windows SDK that you install yourself; see `README.md`.

**Notices only; not legal advice; review before distributing.**

## Bundled in `tools\`

### 7-Zip 26.06 (`7z.exe`, `7z.dll`)

- License: GNU LGPL as the main license. For some code in `7z.dll` the "GNU LGPL with unRAR license restriction" applies, and
  some code is under the BSD 3-clause and BSD 2-clause licenses. 7-Zip's own `License.txt` is `licenses\7-Zip-License.txt`
  (copied unchanged); it states that binary redistributions must reproduce the related license information. It refers to the
  GNU LGPL but does not contain its text, so the LGPL version 2.1 text is also shipped: `licenses\LGPL-2.1.txt`
  (from https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt, unchanged).
- **Source offer.** The files are unmodified binaries of 7-Zip 26.06. The corresponding 7-Zip source code for that version is
  available from the official 7-Zip download page, https://www.7-zip.org/download.html (version 26.06), and the project's
  site is https://www.7-zip.org/. You may obtain it from there.
- Used for: opening the installers and the AMD package to read their contents.

### innoextract 1.9 (`innoextract.exe`)

- License: zlib license. Full text: `licenses\innoextract-LICENSE.txt` (the project's `LICENSE` at tag 1.9, copied unchanged).
  The project's README states that innoextract is available under the zlib license.
- The prebuilt Windows binary statically links third-party components. Their licence texts, copied unchanged from the
  `license\` folder of the official `innoextract-1.9-windows.zip`, are in `licenses\innoextract\`: Boost Software License 1.0
  (`Boost.LICENSE_1_0.txt`), liblzma (`liblzma.COPYING.txt`, public domain) and the libstdc++ runtime
  (`libstdc++.COPYING3.txt` with the GCC Runtime Library Exception, `libstdc++.COPYING.RUNTIME.txt`).
- Source / website: https://constexpr.org/innoextract/ and https://github.com/dscharrer/innoextract
- Used for: reading the contents of Inno Setup based installers. The file is shipped unmodified.

## Not included: Microsoft Windows SDK files

`signtool.exe` and `makecat.exe` (used to create and sign the driver catalog) are **not** distributed by this project. The app
uses the copies from the Windows SDK that you install yourself ("Windows SDK Signing Tools for Desktop Apps"; see `README.md`),
under Microsoft's own license terms for the SDK. Before running them it checks that they carry a valid embedded Microsoft
signature. Microsoft and Windows are trademarks of Microsoft Corporation.

## Runtime and files beside the exe

The executable is a self-contained .NET 8 (WPF) application, so it contains the .NET runtime and base class libraries
(Microsoft, MIT license). The runtime's `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` (and the .NET Desktop runtime's license
file) are in `licenses\dotnet\`, copied from the runtime pack the exe was published with.

These files sit beside the exe and are Microsoft binaries that come with the .NET Desktop runtime: `vcruntime140_cor3.dll`,
`D3DCompiler_47_cor3.dll`, `wpfgfx_cor3.dll`, `PenImc_cor3.dll`, `PresentationNative_cor3.dll`. They are distributed as part of
that runtime under Microsoft's terms for it; no separate terms are stated here.

---

Notices only; not legal advice; review before distributing.
