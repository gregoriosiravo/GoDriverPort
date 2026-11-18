<#
.SYNOPSIS
  Builds the self-contained single-file Release exe into app\publish\ and verifies it WITHOUT launching it.

.DESCRIPTION
  1. dotnet publish (Release, win-x64, self-contained, single file) into app\publish\. Native libraries are NOT bundled
     for self-extraction (no IncludeNativeLibrariesForSelfExtract): .NET would extract them into the user's %TEMP%\.net
     and reuse an existing extraction, which unelevated code could plant. The WPF native DLLs ship beside the exe instead.
  2. copies an explicit allow-list from app\tools\ to app\publish\tools\ (run app\tools\fetch-tools.ps1 first):
     7z.exe, 7z.dll and innoextract.exe. NO Microsoft Windows SDK file is ever shipped (no signtool, no makecat, no companion
     DLLs): the app uses the user's own installed Windows SDK. The build fails if any such file ends up in the output.
     It also ships the MIT LICENSE (as LICENSE.txt), the plain-language pre-install notice (BEFORE-YOU-INSTALL.txt) and README.md.
  3. copies licenses\ (7-Zip License.txt, the LGPL 2.1 text, innoextract LICENSE and its third-party licences), licenses\dotnet\
     (the .NET runtime's LICENSE and THIRD-PARTY-NOTICES, taken from the runtime pack of the published version) and
     THIRD-PARTY-NOTICES.md into the published folder
  4. verifies: exe exists; embedded manifest requests requireAdministrator; no demo-mode strings in any published binary
     (demo code is Debug-only); the tools are present; no Microsoft SDK file anywhere in the folder; license files and notices
     are present; every shipped tools\ and licenses\ file matches tools.lock.json
  5. lists every shipped file with its size and SHA-256

  Every run starts from an empty app\publish\. The same folder feeds both the portable zip and the installer.
  Install the published folder to C:\Program Files\LegionGoDriverPort\ (admin-writable only) and run it from there.

  Notices only; not legal advice; review before distributing.
  The published exe requests administrator rights, so this script never starts it.
#>
[CmdletBinding()]
param(
    [string]$Dotnet,
    # Self-test of the demo-code scan: scan this one file, print which markers are found, and exit (no build).
    [string]$ScanOnly
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Msg) { throw "publish: $Msg" }

# ---- Demo-code markers ---------------------------------------------------------------------
# Distinctive names and strings of the Debug-only demo code (app\src\LegionGoConverter.App\DemoMode.cs, `#if DEBUG`).
# Each is searched as ASCII/UTF-8 and as UTF-16 (C# string literals are UTF-16 in the metadata #US heap; names are UTF-8).
$DemoTypeNames = @('DemoMode','DemoRunner','DemoRollback','DemoIdentifier','DemoFiles','DemoDevice','DemoSystem','SnapshotAsync')
$DemoStrings   = @('--demo=','--snapshot=','--bindlog=','--size=','--theme=',
                   'review-export','result-pending','result-fail','result-ok','prompt-long',
                   'Demo mode: Windows would restart now','Demo mode: would open',
                   'lnvgy_dr_amd_graphics_25.30.44_legion_go.exe','swapped_lenovo_package.exe')
function New-Needle([string]$Label, [byte[]]$Bytes) { [pscustomobject]@{ Label = $Label; Bytes = $Bytes } }
$Needles = @()
foreach ($n in $DemoTypeNames) {
    # NUL on both sides: a bare 'DemoMode' also matches framework metadata such as 'get_IsDemoModeEnabled'.
    $Needles += New-Needle "type $n" ([byte[]]@(0) + [Text.Encoding]::UTF8.GetBytes($n) + [byte[]]@(0))
}
foreach ($s in $DemoStrings) {
    $Needles += New-Needle "'$s' (UTF-8)"  ([Text.Encoding]::UTF8.GetBytes($s))
    $Needles += New-Needle "'$s' (UTF-16)" ([Text.Encoding]::Unicode.GetBytes($s))
}
function Test-Bytes([byte[]]$Hay, [byte[]]$Needle) {
    $first = $Needle[0]; $last = $Hay.Length - $Needle.Length
    $i = [Array]::IndexOf($Hay, $first, 0)
    while ($i -ge 0 -and $i -le $last) {
        $ok = $true
        for ($j = 1; $j -lt $Needle.Length; $j++) { if ($Hay[$i + $j] -ne $Needle[$j]) { $ok = $false; break } }
        if ($ok) { return $true }
        if ($i + 1 -gt $last) { break }
        $i = [Array]::IndexOf($Hay, $first, $i + 1)
    }
    return $false
}
function Find-DemoMarkers([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    foreach ($n in $Needles) { if (Test-Bytes $bytes $n.Bytes) { "$(Split-Path $Path -Leaf): $($n.Label)" } }
}

if ($ScanOnly) {
    if (-not (Test-Path -LiteralPath $ScanOnly)) { Fail "ScanOnly file not found: $ScanOnly" }
    $found = @(Find-DemoMarkers (Resolve-Path -LiteralPath $ScanOnly).Path)
    Write-Host "Markers searched: $($Needles.Count) patterns ($($DemoTypeNames.Count) type names, $($DemoStrings.Count) strings x 2 encodings)"
    Write-Host "Found $($found.Count):"; $found | ForEach-Object { Write-Host "  $_" }
    return
}

$AppDir     = $PSScriptRoot
$Project    = Join-Path $AppDir 'src\LegionGoConverter.App\LegionGoConverter.App.csproj'
$OutDir     = Join-Path $AppDir 'publish'
$ToolsSrc   = Join-Path $AppDir 'tools'
$ExeName    = 'LegionGoDriverPort.exe'
# Allow-list of what the build ships in tools\ : 7-Zip and innoextract. Nothing from the Microsoft Windows SDK is ever shipped.
$ShipTools    = @('7z.exe','7z.dll','innoextract.exe')
$LicenseFiles = @('7-Zip-License.txt','LGPL-2.1.txt','innoextract-LICENSE.txt')
$InnoThirdPartyFiles = @('Boost.LICENSE_1_0.txt','liblzma.COPYING.txt','libstdc++.COPYING.RUNTIME.txt','libstdc++.COPYING3.txt')
$NoticesSrc   = Join-Path $AppDir 'THIRD-PARTY-NOTICES.md'
$RepoRoot     = Split-Path $AppDir -Parent
$LicenseSrc   = Join-Path $RepoRoot 'LICENSE'
$NoticeSrc    = Join-Path $RepoRoot 'installer\BEFORE-YOU-INSTALL.txt'
$ReadmeSrc    = Join-Path $RepoRoot 'README.md'
# Names of every Microsoft Windows SDK file the project has ever bundled or could mistake for its own (signtool, makecat, REDIST companions).
# None of them may appear anywhere in the output (see the check below).
$MicrosoftSdkFiles = @('signtool.exe','signtool.exe.manifest','makecat.exe','makecat.exe.manifest',
                'mssign32.dll','wintrust.dll','wintrust.dll.ini','appxpackaging.dll','appxsip.dll','opcservices.dll','msisip.dll',
                'Microsoft.Windows.Build.Appx.AppxPackaging.dll.manifest','Microsoft.Windows.Build.Appx.AppxSip.dll.manifest',
                'Microsoft.Windows.Build.Appx.OpcServices.dll.manifest','Microsoft.Windows.Build.Signing.mssign32.dll.manifest',
                'Microsoft.Windows.Build.Signing.wintrust.dll.manifest')

if (-not $Dotnet) {
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $Dotnet = $cmd.Source }
    elseif (Test-Path 'C:\Program Files\dotnet\dotnet.exe') { $Dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
    else { Fail 'dotnet not found on PATH or in C:\Program Files\dotnet. Pass -Dotnet <path>.' }
}
if (-not (Test-Path -LiteralPath $Project)) { Fail "Project not found: $Project" }
foreach ($t in $ShipTools) {
    if (-not (Test-Path -LiteralPath (Join-Path $ToolsSrc $t))) { Fail "Missing app\tools\$t. Run app\tools\fetch-tools.ps1 first." }
}
foreach ($l in $LicenseFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $ToolsSrc "licenses\$l"))) { Fail "Missing app\tools\licenses\$l. Run app\tools\fetch-tools.ps1 first." }
}
if (-not (Test-Path -LiteralPath $NoticesSrc)) { Fail "Missing $NoticesSrc." }
foreach ($f in $LicenseSrc, $NoticeSrc, $ReadmeSrc) { if (-not (Test-Path -LiteralPath $f)) { Fail "Missing $f." } }
foreach ($n in $InnoThirdPartyFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $ToolsSrc "licenses\innoextract\$n"))) { Fail "Missing app\tools\licenses\innoextract\$n. Run app\tools\fetch-tools.ps1 first." }
}

# ---- Build -------------------------------------------------------------------------------
if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
Write-Host 'dotnet publish ...'
& $Dotnet publish $Project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:EnableCompressionInSingleFile=false -p:ContinuousIntegrationBuild=true -o $OutDir
if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed with exit code $LASTEXITCODE." }

# ---- Tools ---------------------------------------------------------------------------------
# Only the allow-listed files are copied (never a wildcard), so nothing unlisted can slip in.
$toolsDst = Join-Path $OutDir 'tools'
New-Item -ItemType Directory -Path $toolsDst -Force | Out-Null
foreach ($t in $ShipTools) { Copy-Item -LiteralPath (Join-Path $ToolsSrc $t) -Destination (Join-Path $toolsDst $t) -Force }
$licDst = Join-Path $OutDir 'licenses'
New-Item -ItemType Directory -Path $licDst -Force | Out-Null
foreach ($l in $LicenseFiles) { Copy-Item -LiteralPath (Join-Path $ToolsSrc "licenses\$l") -Destination (Join-Path $licDst $l) -Force }
New-Item -ItemType Directory -Path (Join-Path $licDst 'innoextract') -Force | Out-Null
foreach ($n in $InnoThirdPartyFiles) { Copy-Item -LiteralPath (Join-Path $ToolsSrc "licenses\innoextract\$n") -Destination (Join-Path $licDst "innoextract\$n") -Force }
Copy-Item -LiteralPath $NoticesSrc -Destination (Join-Path $OutDir 'THIRD-PARTY-NOTICES.md') -Force
Copy-Item -LiteralPath $LicenseSrc -Destination (Join-Path $OutDir 'LICENSE.txt') -Force
Copy-Item -LiteralPath $NoticeSrc -Destination (Join-Path $OutDir 'BEFORE-YOU-INSTALL.txt') -Force
Copy-Item -LiteralPath $ReadmeSrc -Destination (Join-Path $OutDir 'README.md') -Force   # THIRD-PARTY-NOTICES.md and the notice refer to it

# .NET runtime licence + third-party notices, from the runtime pack the exe was published with (the exe embeds that runtime).
# Licence files come from app\tools\licenses\dotnet (hash-pinned in tools.lock.json by fetch-tools.ps1); the runtime version must match.
$assets = Join-Path $AppDir 'src\LegionGoConverter.App\obj\Release\net8.0-windows10.0.19041.0\win-x64\LegionGoDriverPort.deps.json'
if (-not (Test-Path -LiteralPath $assets)) { Fail "Cannot find $assets to determine the .NET runtime pack version." }
$m = [regex]::Match([IO.File]::ReadAllText($assets), 'Microsoft\.NETCore\.App\.Runtime\.win-x64/(\d+\.\d+\.\d+[^"\s]*)')
if (-not $m.Success) { Fail 'The runtime pack version (Microsoft.NETCore.App.Runtime.win-x64/<version>) is not in the Release deps.json.' }
$rtVer = $m.Groups[1].Value
$lockVer = ((Get-Content -LiteralPath (Join-Path $ToolsSrc 'tools.lock.json') -Raw | ConvertFrom-Json).files | Where-Object { $_.name -eq 'licenses/dotnet/LICENSE.TXT' } | Select-Object -First 1)
if (-not $lockVer -or $lockVer.version -ne $rtVer) { Fail "The exe embeds .NET $rtVer but app\tools\licenses\dotnet is for $(if ($lockVer) { $lockVer.version } else { 'nothing (not provisioned)' }). Update DotnetVersion in fetch-tools.ps1 and run it with -Force -AcceptChangedTools." }
$dnLic = Join-Path $licDst 'dotnet'
New-Item -ItemType Directory -Path $dnLic -Force | Out-Null
$dnFiles = @(@{ Src = (Join-Path $ToolsSrc 'licenses\dotnet\LICENSE.TXT');              Dst = 'LICENSE.TXT' },
             @{ Src = (Join-Path $ToolsSrc 'licenses\dotnet\THIRD-PARTY-NOTICES.TXT');   Dst = 'THIRD-PARTY-NOTICES.TXT' },
             @{ Src = (Join-Path $ToolsSrc 'licenses\dotnet\WindowsDesktop-LICENSE.txt');                 Dst = 'WindowsDesktop-LICENSE.txt' })
foreach ($f in $dnFiles) {
    if (-not (Test-Path -LiteralPath $f.Src)) { Fail "The .NET runtime licence file $($f.Src) was not found. The self-contained exe embeds .NET $rtVer, whose licence and notices must ship with it. Run app\tools\fetch-tools.ps1 -Force." }
    Copy-Item -LiteralPath $f.Src -Destination (Join-Path $dnLic $f.Dst) -Force
}
Write-Host "  .NET runtime $rtVer licence files copied from app\tools\licenses\dotnet"

# ---- Verification (no launch) -----------------------------------------------------------
$exe = Join-Path $OutDir $ExeName
if (-not (Test-Path -LiteralPath $exe)) { Fail "Published exe not found: $exe" }
Write-Host ''; Write-Host 'Verification:'; Write-Host "  [ok] exe exists: $exe"

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class ManifestReader {
    [DllImport("kernel32", SetLastError=true, CharSet=CharSet.Unicode)] static extern IntPtr LoadLibraryEx(string f, IntPtr h, uint flags);
    [DllImport("kernel32", SetLastError=true)] static extern bool FreeLibrary(IntPtr h);
    [DllImport("kernel32", SetLastError=true)] static extern IntPtr FindResource(IntPtr h, IntPtr name, IntPtr type);
    [DllImport("kernel32", SetLastError=true)] static extern IntPtr LoadResource(IntPtr h, IntPtr r);
    [DllImport("kernel32", SetLastError=true)] static extern IntPtr LockResource(IntPtr r);
    [DllImport("kernel32", SetLastError=true)] static extern uint SizeofResource(IntPtr h, IntPtr r);
    // Reads RT_MANIFEST (24) resource ids 1..3 from the file as a data file: the exe is mapped, never executed.
    public static string Read(string path) {
        IntPtr h = LoadLibraryEx(path, IntPtr.Zero, 0x2 /* LOAD_LIBRARY_AS_DATAFILE */);
        if (h == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try {
            for (int id = 1; id <= 3; id++) {
                IntPtr r = FindResource(h, (IntPtr)id, (IntPtr)24);
                if (r == IntPtr.Zero) continue;
                uint size = SizeofResource(h, r);
                IntPtr p = LockResource(LoadResource(h, r));
                byte[] b = new byte[size]; Marshal.Copy(p, b, 0, (int)size);
                return Encoding.UTF8.GetString(b);
            }
            return null;
        } finally { FreeLibrary(h); }
    }
}
'@
$manifest = [ManifestReader]::Read($exe)
if (-not $manifest) { Fail 'The exe has no embedded manifest resource.' }
if ($manifest -notmatch 'level="requireAdministrator"') { Fail 'Embedded manifest does not request requireAdministrator.' }
Write-Host '  [ok] embedded manifest requests requireAdministrator'

# Demo code is Debug-only (`#if DEBUG`). Scan the published binaries AND the intermediate Release assembly (obj\Release\...,
# plain IL, where single-file bundling and any compression cannot hide anything) for every demo marker.
$objDll = Get-ChildItem -LiteralPath (Join-Path $AppDir 'src\LegionGoConverter.App\obj\Release') -Recurse -Filter 'LegionGoDriverPort.dll' -ErrorAction SilentlyContinue |
          Where-Object { $_.FullName -like '*win-x64*' } | Select-Object -First 1
if (-not $objDll) { Fail 'Intermediate Release assembly (obj\Release\...\win-x64\LegionGoDriverPort.dll) not found.' }
$targets = @(Get-ChildItem -LiteralPath $OutDir -File | Where-Object { $_.Extension -in '.exe','.dll' }) + @($objDll)
$hits = @()
foreach ($f in $targets) { $hits += @(Find-DemoMarkers $f.FullName) }
if ($hits) { Fail ("Demo-mode markers found:`n  " + ($hits -join "`n  ")) }
Write-Host "  [ok] none of $($Needles.Count) demo markers found in $(@($targets).Count) files (published binaries + obj Release assembly)"
Write-Host "       single-file compression: OFF (EnableCompressionInSingleFile=false), so the bundled assemblies are scanned as stored."
Write-Host "       This scan proves the known markers are absent. It does not prove that no other debug-only code exists;"
Write-Host "       the real guarantee is the #if DEBUG guard, which Release builds compile out. Self-test: publish.ps1 -ScanOnly <Debug build dll>."

foreach ($t in $ShipTools) {
    if (-not (Test-Path -LiteralPath (Join-Path $toolsDst $t))) { Fail "tools\$t missing from the published folder." }
}
Write-Host "  [ok] tools\ has: $($ShipTools -join ', ')"
$extraTools = @(Get-ChildItem -LiteralPath $toolsDst -Force -Recurse | Where-Object { $_.Name -notin $ShipTools })
if ($extraTools) { Fail ("tools\ contains files that are not on the allow-list: " + (($extraTools | ForEach-Object Name) -join ', ')) }
Write-Host '  [ok] tools\ contains nothing beyond the allow-list'

# No Microsoft Windows SDK file may be in the output (signtool, makecat, companion DLLs/manifests): not in tools\, not anywhere else.
$msFound = @(Get-ChildItem -LiteralPath $OutDir -Recurse -Force -File | Where-Object { $_.Name -in $MicrosoftSdkFiles -or $_.Name -like 'makecat*' -or $_.Name -like 'signtool*' })
if ($msFound) { Fail ("The published folder must contain no Microsoft SDK file, but found: " + (($msFound | ForEach-Object FullName) -join ', ')) }
if ($ShipTools.Count -ne 3) { Fail 'The tools allow-list is not 7z.exe, 7z.dll, innoextract.exe.' }
Write-Host '  [ok] none of the Microsoft SDK files (signtool, makecat, REDIST companions) is in the published folder; the app uses the Windows SDK you install'

# License files and notices must ship.
foreach ($l in $LicenseFiles) {
    $lp = Join-Path $licDst $l
    if (-not (Test-Path -LiteralPath $lp) -or (Get-Item -LiteralPath $lp).Length -eq 0) { Fail "licenses\$l is missing or empty in the published folder." }
}
Write-Host "  [ok] licenses\ has: $($LicenseFiles -join ', ')"
$notices = Join-Path $OutDir 'THIRD-PARTY-NOTICES.md'
if (-not (Test-Path -LiteralPath $notices) -or (Get-Item -LiteralPath $notices).Length -eq 0) { Fail 'THIRD-PARTY-NOTICES.md is missing or empty in the published folder.' }
Write-Host '  [ok] THIRD-PARTY-NOTICES.md is present'
foreach ($f in $dnFiles) {
    $dp = Join-Path $dnLic $f.Dst
    if (-not (Test-Path -LiteralPath $dp) -or (Get-Item -LiteralPath $dp).Length -eq 0) { Fail "licenses\dotnet\$($f.Dst) is missing or empty in the published folder." }
}
Write-Host "  [ok] licenses\dotnet\ has the .NET $rtVer runtime LICENSE and THIRD-PARTY-NOTICES"

# Every shipped tools\ and licenses\ file must match tools.lock.json (name, SHA-256).
$lockPath = Join-Path $ToolsSrc 'tools.lock.json'
if (-not (Test-Path -LiteralPath $lockPath)) { Fail 'tools.lock.json not found. Run app\tools\fetch-tools.ps1 first.' }
$lock = Get-Content -LiteralPath $lockPath -Raw | ConvertFrom-Json
$lockHash = @{}
foreach ($e in $lock.files) { $lockHash[$e.name.Replace('\', '/')] = $e.sha256 }
$checked = 0
foreach ($f in (Get-ChildItem -LiteralPath $toolsDst -File) + (Get-ChildItem -LiteralPath $licDst -File -Recurse)) {
    $rel = $f.FullName.Substring($OutDir.Length).TrimStart('\').Replace('\', '/')
    if ($rel.StartsWith('tools/')) { $rel = $rel.Substring(6) }
    if (-not $lockHash.ContainsKey($rel)) { Fail "Shipped file $rel is not listed in tools.lock.json." }
    $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($h -ne $lockHash[$rel]) { Fail "Shipped file $rel has SHA-256 $h but tools.lock.json records $($lockHash[$rel])." }
    $checked++
}
Write-Host "  [ok] $checked shipped tools\ and licenses\ files match tools.lock.json"
foreach ($n in 'BEFORE-YOU-INSTALL.txt', 'LICENSE.txt', 'README.md') {
    $np = Join-Path $OutDir $n
    if (-not (Test-Path -LiteralPath $np) -or (Get-Item -LiteralPath $np).Length -eq 0) { Fail "$n is missing or empty in the published folder." }
}
Write-Host '  [ok] LICENSE.txt (MIT), BEFORE-YOU-INSTALL.txt (plain-language notice) and README.md are present'
# The exe version must match the single version source (Directory.Build.props).
$propsPath = Join-Path $RepoRoot 'Directory.Build.props'
if (Test-Path -LiteralPath $propsPath) {
    $pv = ([xml](Get-Content -LiteralPath $propsPath -Raw)).Project.PropertyGroup.Version
    $fv = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
    if ($pv -and -not "$fv".StartsWith($pv)) { Fail "The exe version is '$fv' but Directory.Build.props says $pv." }
    Write-Host "  [ok] exe version $fv matches Directory.Build.props ($pv)"
}

# Native libraries must ship beside the exe (never self-extracted to %TEMP%): the WPF native DLLs have to be present here.
$nativeBeside = @(Get-ChildItem -LiteralPath $OutDir -File -Filter '*_cor3.dll')
if ($nativeBeside.Count -eq 0) { Fail 'No WPF native DLLs (*_cor3.dll) beside the exe: were native libraries bundled for self-extraction?' }
Write-Host "  [ok] native libraries ship beside the exe (not extracted to %TEMP%): $(($nativeBeside | ForEach-Object Name) -join ', ')"

Write-Host ''
Write-Host 'Shipped files (size, SHA-256):'
Get-ChildItem -LiteralPath $OutDir -File -Recurse | Sort-Object FullName | ForEach-Object {
    $rel = $_.FullName.Substring($OutDir.Length).TrimStart('\')
    $h = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    Write-Host ("  {0,-36} {1,14:N0}  {2}" -f $rel, $_.Length, $h)
}
$hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host ''
Write-Host ("{0}: {1:N0} bytes" -f $ExeName, (Get-Item -LiteralPath $exe).Length)
Write-Host "SHA-256: $hash"
Write-Host ''
Write-Host "OK: $OutDir (not launched: it requests administrator)."
Write-Host "Install: copy the whole folder to C:\Program Files\LegionGoDriverPort\ as administrator and run it from there."
