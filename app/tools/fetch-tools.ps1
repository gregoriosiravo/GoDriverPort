<#
.SYNOPSIS
  Developer-time provisioning of app\tools\ (the shipped app downloads nothing).

.DESCRIPTION
  Places these files next to this script:
    7z.exe, 7z.dll                         from a 7-Zip install
    innoextract.exe                        from an innoextract install (winget dscharrer.innoextract)
    licenses\7-Zip-License.txt             copied from the 7-Zip folder (License.txt)
    licenses\LGPL-2.1.txt                  tracked in git, SHA-256 pinned here; downloaded from gnu.org only when missing
    licenses\innoextract-LICENSE.txt       tracked in git, SHA-256 pinned here; downloaded from the innoextract repository only when missing
    licenses\innoextract\*                 the third-party licence texts from the license\ folder of the innoextract download (SHA-256 pinned here)
    licenses\dotnet\*                      the .NET runtime licence files, from the NuGet cache (restore the win-x64 runtime packs first)

  NO Microsoft SDK file is provisioned or shipped: signtool.exe and makecat.exe come from the user's own Windows SDK at run time.
  (To run the real-tool tests, point LGO_TOOLS_DIR at your Windows SDK ...\bin\10.0.xxxxx.0\x64 folder; see CONTRIBUTING.md.)

  What is checked, and what is not:
    7-Zip            Authenticode is checked when present (the signer is printed; present-but-invalid fails). Current 7-Zip builds
                     are unsigned, in which case the files are trusted on first use (TOFU).
    innoextract      Unsigned. Its SHA-256 and version are printed; it is trusted on first use (TOFU).
    tools.lock.json  A tamper-evidence record of what was copied the first time: name, version, SHA-256, source. Later runs detect
                     a changed or missing LISTED file. It does not make 7-Zip/innoextract trustworthy (it records whatever was there
                     at first use), is not signed, and anyone who can edit app\tools can also edit the lock.
                     Re-provisioning (-Force, or after a failed check) compares the new files with the lock; if any hash differs the
                     script stops unless -AcceptChangedTools is given. The lock file is only rewritten when its content changes.
                     If you accept a new 7-Zip version, also update its version and source offer in app\THIRD-PARTY-NOTICES.md.
  Nothing here signs or installs anything: the smoke checks only run 7z.exe and `innoextract.exe --version`.

.PARAMETER SevenZipDir          Folder containing 7z.exe, 7z.dll and License.txt. Default: discovered (the 7-Zip install path from the registry,
                        %ProgramFiles%\7-Zip, %ProgramFiles(x86)%\7-Zip, or the folder of a 7z.exe on PATH).
.PARAMETER InnoExtractDir       Folder containing innoextract.exe and its license\ folder. Default: discovered (the winget package folder
                        dscharrer.innoextract_*, the winget Links folder, or the folder of an innoextract.exe on PATH).
.PARAMETER Force                Re-provision even if tools.lock.json verifies.
.PARAMETER AcceptChangedTools   Allow re-provisioning to replace files whose hash differs from tools.lock.json (new lock is written).
#>
[CmdletBinding()]
param(
    [string]$SevenZipDir,
    [string]$InnoExtractDir,
    [switch]$Force,
    [switch]$AcceptChangedTools
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ToolsDir   = $PSScriptRoot
$LockPath   = Join-Path $ToolsDir 'tools.lock.json'
$LicensesDir = Join-Path $ToolsDir 'licenses'
$SevenZipLicenseName = '7-Zip-License.txt'
$InnoLicenseName = 'innoextract-LICENSE.txt'
# innoextract 1.9 is the version provisioned (checked below against "innoextract --version"). The zlib LICENSE is fetched at the matching tag.
$InnoLicenseVersion = '1.9'
$InnoLicenseUrl = "https://raw.githubusercontent.com/dscharrer/innoextract/$InnoLicenseVersion/LICENSE"
$InnoLicenseSha = '7cd6f3bf0c83f2c3ce8438aab20a4827a5de934fb87dd7966f6f3f2651685127'   # SHA-256 of that LICENSE (zlib license text)
# NOTE: this pin was recorded from a first fetch of that URL (trust on first use). It detects a later change of the file, it is not an
# independent proof that the first download was the upstream text.
# Third-party licence texts of the statically linked parts of the innoextract Windows binary: the license\ folder of
# innoextract-1.9-windows.zip (identical in the winget package). SHA-256 values checked against that official zip.
$InnoThirdParty = [ordered]@{
    'Boost.LICENSE_1_0.txt'         = 'beb8e42e9d6b4284e03304d05a81a0755200a965fc8d0a5e0aea1e84cf805d6e'
    'liblzma.COPYING.txt'           = '54eacb8b8530b44c47b2d16b9a3f8fe32ac585cb1093ab2a251460bc411b32c3'
    'libstdc++.COPYING.RUNTIME.txt' = '90077ba306ee9d8ed966cab1b4abd69b6e728acf08777283cc50283cd9503fa6'
    'libstdc++.COPYING3.txt'        = '0b383d5a63da644f628d99c33976ea6487ed89aaa59f0b3257992deac1171e6b'
}
# .NET runtime licence files (the published exe embeds the runtime). Copied from the NuGet runtime pack of this version; publish.ps1
# checks that the version it publishes matches. Recorded in the lock (trust on first use, like 7-Zip/innoextract).
$DotnetVersion = '8.0.34'
$DotnetFiles = @(@{ Pack = 'microsoft.netcore.app.runtime.win-x64';        Src = 'LICENSE.TXT';              Dst = 'LICENSE.TXT' },
                 @{ Pack = 'microsoft.netcore.app.runtime.win-x64';        Src = 'THIRD-PARTY-NOTICES.TXT';  Dst = 'THIRD-PARTY-NOTICES.TXT' },
                 @{ Pack = 'microsoft.windowsdesktop.app.runtime.win-x64'; Src = 'LICENSE';                  Dst = 'WindowsDesktop-LICENSE.txt' })
$LgplName = 'LGPL-2.1.txt'
$LgplUrl  = 'https://www.gnu.org/licenses/old-licenses/lgpl-2.1.txt'   # the GNU LGPL 2.1 text that 7-Zip's License.txt refers to (it does not contain it)
$LgplSha  = '20e50fe7aae3e56378ebf0417d9de904f55a0e61e4df315333e632a4d3555d95'   # recorded from a first fetch (TOFU), same caveat as above

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Fail([string]$Msg) { throw "fetch-tools: $Msg" }

# ---- Tool discovery (nothing is hard-coded to one machine) ---------------------------------
# First folder (in order) that contains every file in $Files; $null when none does. Null/empty candidates are skipped.
function Find-ToolDir([string[]]$Candidates, [string[]]$Files) {
    foreach ($c in $Candidates) {
        if ([string]::IsNullOrWhiteSpace($c)) { continue }
        $ok = $true
        foreach ($f in $Files) { if (-not (Test-Path -LiteralPath (Join-Path $c $f))) { $ok = $false; break } }
        if ($ok) { return $c }
    }
    return $null
}
function Find-OnPath([string]$Exe) {
    $cmd = Get-Command $Exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { return (Split-Path $cmd.Source -Parent) }
}
function Find-SevenZipDir {
    $c = @()
    foreach ($key in 'HKLM:\SOFTWARE\7-Zip', 'HKCU:\SOFTWARE\7-Zip', 'HKLM:\SOFTWARE\WOW6432Node\7-Zip') {
        $k = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($k) { foreach ($n in 'Path64', 'Path') { if ($k.PSObject.Properties[$n]) { $c += $k.$n } } }
    }
    $c += (Join-Path $env:ProgramFiles '7-Zip')
    if (${env:ProgramFiles(x86)}) { $c += (Join-Path ${env:ProgramFiles(x86)} '7-Zip') }
    $c += (Find-OnPath '7z.exe')
    Find-ToolDir $c @('7z.exe', '7z.dll', 'License.txt')
}
function Find-InnoExtractDir {
    $c = @()
    if ($env:LOCALAPPDATA) {
        $pk = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Packages'
        if (Test-Path -LiteralPath $pk) { $c += @(Get-ChildItem -LiteralPath $pk -Directory -Filter 'dscharrer.innoextract_*' -ErrorAction SilentlyContinue | ForEach-Object FullName) }
        $c += (Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet\Links')
    }
    $c += (Find-OnPath 'innoextract.exe')
    Find-ToolDir $c @('innoextract.exe')
}

function Read-Lock {
    if (-not (Test-Path -LiteralPath $LockPath)) { return $null }
    return (Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json)
}

# Returns $true when every listed file exists and matches; throws when a listed file exists with a different hash.
function Test-Lock {
    $lock = Read-Lock
    if (-not $lock) { return $false }
    foreach ($e in $lock.files) {
        $p = Join-Path $ToolsDir $e.name
        if (-not (Test-Path -LiteralPath $p)) { Write-Host "  missing: $($e.name)"; return $false }
        if ((Get-Sha256 $p) -ne $e.sha256) { Fail "$($e.name) does not match tools.lock.json (SHA-256 differs). If the change is intended run with -Force -AcceptChangedTools." }
    }
    # Files the lock does not know about would be shipped unchecked (this includes leftover Microsoft files from older provisioning).
    $listed = @($lock.files | ForEach-Object { $_.name }) + @('fetch-tools.ps1','tools.lock.json')
    $extra = @(Get-ChildItem -LiteralPath $ToolsDir -File | Where-Object { $_.Name -notin $listed -and $_.Extension -in '.exe','.dll','.ini','.manifest' })
    if ($extra.Count -gt 0) { Fail ("Files in app\tools that tools.lock.json does not list: " + (($extra | ForEach-Object Name) -join ', ') + ". Delete them (Microsoft SDK files are no longer provisioned; the app uses your Windows SDK) or re-provision.") }
    return $true
}

function Show-Licenses {
    Write-Host ''
    Write-Host 'Licenses of the tools (a record of the published terms, not legal advice; review before distributing):'
    Write-Host '  7-Zip (7z.exe, 7z.dll)  : GNU LGPL (+ unRAR restriction for part of 7z.dll, BSD parts). Binary redistribution must reproduce License.txt: shipped as licenses\7-Zip-License.txt, with the LGPL text as licenses\LGPL-2.1.txt.'
    Write-Host '  innoextract.exe         : zlib license: licenses\innoextract-LICENSE.txt; statically linked parts: licenses\innoextract\ (Boost, liblzma, libstdc++).'
    Write-Host '  Microsoft Windows SDK files (signtool.exe, makecat.exe): NOT provisioned and NOT shipped. Users install the Windows SDK "Signing Tools for Desktop Apps" themselves.'
}
function Invoke-Tool([string]$Path, [string[]]$ToolArgs) {
    # Native stderr must not turn into a terminating error under $ErrorActionPreference = 'Stop'.
    $prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $out = & $Path @ToolArgs 2>&1 | ForEach-Object { "$_" } | Out-String; $code = $LASTEXITCODE }
    finally { $ErrorActionPreference = $prev }
    [pscustomobject]@{ Output = $out; ExitCode = $code }
}

function Invoke-SmokeCheck {
    Write-Host ''
    Write-Host 'Smoke checks (run from app\tools; no install):'
    foreach ($t in '7z.exe','innoextract.exe') {
        $p = Join-Path $ToolsDir $t
        $args2 = if ($t -eq '7z.exe') { @() } else { @('--version') }
        $r = Invoke-Tool $p $args2; $out = $r.Output; $code = $r.ExitCode
        $first = ($out -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1)
        Write-Host ("  {0}  exit={1}  first line: {2}" -f $t, $code, $first)
        if ([string]::IsNullOrWhiteSpace($out)) { Fail "$t produced no output." }
    }
}

function Show-Trust {
    Write-Host ''
    Write-Host 'Trust summary:'
    Write-Host "  7-Zip and innoextract: trusted on first use. tools.lock.json only detects later changes to the listed files;"
    Write-Host "  it does not prove those two tools were genuine when first copied."
}

function Test-ThirdPartyLicenses {
    foreach ($n in $InnoThirdParty.Keys) {
        $p = Join-Path $LicensesDir "innoextract\$n"
        if (-not (Test-Path -LiteralPath $p)) { return $false }
        if ((Get-Sha256 $p) -ne $InnoThirdParty[$n]) { Fail "licenses\innoextract\$n does not match the SHA-256 pinned in this script." }
    }
    return $true
}

# ---- Fast path: already provisioned -------------------------------------------------------
$licensesPresent = (Test-Path -LiteralPath (Join-Path $LicensesDir $SevenZipLicenseName)) -and (Test-Path -LiteralPath (Join-Path $LicensesDir $InnoLicenseName)) -and (Test-Path -LiteralPath (Join-Path $LicensesDir $LgplName)) -and (@($DotnetFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $LicensesDir ('dotnet\' + $_.Dst))) }).Count -eq 0)
if (-not $Force -and $licensesPresent -and (Test-ThirdPartyLicenses) -and (Test-Lock)) {
    Write-Host "tools.lock.json: every listed file exists and matches its recorded SHA-256 (no unlisted files)."
    if ((Get-Sha256 (Join-Path $LicensesDir $InnoLicenseName)) -ne $InnoLicenseSha) { Fail "licenses\$InnoLicenseName does not match the SHA-256 pinned in this script." }
    if ((Get-Sha256 (Join-Path $LicensesDir $LgplName)) -ne $LgplSha) { Fail "licenses\$LgplName does not match the SHA-256 pinned in this script." }
    Show-Licenses
    Invoke-SmokeCheck
    Show-Trust
    Write-Host ''; Write-Host 'OK: app\tools is provisioned.'
    return
}

# ---- Provision: stage everything in a temp folder, check, compare with the old lock, then copy ----
if (-not $SevenZipDir) {
    $SevenZipDir = Find-SevenZipDir
    if (-not $SevenZipDir) { Fail '7-Zip not found (looked in the registry, Program Files and PATH; License.txt must be beside 7z.exe). Install 7-Zip from https://www.7-zip.org/ or pass -SevenZipDir <folder>.' }
}
if (-not $InnoExtractDir) {
    $InnoExtractDir = Find-InnoExtractDir
    if (-not $InnoExtractDir) { Fail 'innoextract.exe not found (looked in the winget package folder, the winget Links folder and PATH). Install it with: winget install dscharrer.innoextract (or download innoextract-1.9-windows.zip from https://github.com/dscharrer/innoextract/releases/tag/1.9) or pass -InnoExtractDir <folder>.' }
}
$work = Join-Path ([IO.Path]::GetTempPath()) ("lgc-tools-" + [guid]::NewGuid().ToString('N'))
$stage = Join-Path $work 'stage'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$entries = New-Object System.Collections.Generic.List[object]
function Add-Entry([string]$Name, [string]$Version, [string]$Source, [string]$Category = 'distributable') {
    $entries.Add([ordered]@{ name = $Name; version = $Version; sha256 = (Get-Sha256 (Join-Path $stage $Name)); source = $Source; category = $Category })
}
try {
    # ---- 7-Zip ----
    foreach ($f in '7z.exe','7z.dll') {
        $src = Join-Path $SevenZipDir $f
        if (-not (Test-Path -LiteralPath $src)) { Fail "$f not found in '$SevenZipDir'. Install 7-Zip or pass -SevenZipDir." }
        Copy-Item -LiteralPath $src -Destination (Join-Path $stage $f) -Force
        $sig = Get-AuthenticodeSignature -LiteralPath (Join-Path $stage $f)
        if ($sig.Status -eq 'NotSigned') { Write-Host "7-Zip $f : not Authenticode-signed (expected for 7-Zip builds): trusted on first use." }
        elseif ($sig.Status -eq 'Valid')  { Write-Host "7-Zip $f : Authenticode Valid, signer: $($sig.SignerCertificate.Subject)" }
        else { Fail "7-Zip $f has a signature that is not valid (status $($sig.Status)). Refusing to use it." }
    }
    $v7 = (Get-Item -LiteralPath (Join-Path $stage '7z.exe')).VersionInfo.ProductVersion
    foreach ($f in '7z.exe','7z.dll') { Add-Entry $f $v7 'local:7-Zip' }
    Write-Host "7-Zip $v7 : 7z.exe SHA-256 $((Get-Sha256 (Join-Path $stage '7z.exe')))"

    # ---- innoextract ----
    $srcInno = Join-Path $InnoExtractDir 'innoextract.exe'
    if (-not (Test-Path -LiteralPath $srcInno)) { Fail "innoextract.exe not found in '$InnoExtractDir'. Install it (winget install dscharrer.innoextract) or pass -InnoExtractDir." }
    Copy-Item -LiteralPath $srcInno -Destination (Join-Path $stage 'innoextract.exe') -Force
    $vi = ((Invoke-Tool (Join-Path $stage 'innoextract.exe') @('--version')).Output -split "`r?`n" | Select-Object -First 1) -replace '^innoextract\s+',''
    Add-Entry 'innoextract.exe' $vi 'local:innoextract'
    Write-Host "innoextract $vi : unsigned, SHA-256 $((Get-Sha256 (Join-Path $stage 'innoextract.exe'))). Trusted on first use."
    if ($vi -ne $InnoLicenseVersion) { Fail "innoextract reports version '$vi' but this script fetches the LICENSE of '$InnoLicenseVersion'. Update InnoLicenseVersion, InnoLicenseUrl and InnoLicenseSha in this script for the new version." }

    # ---- License texts (only what the sources provide; nothing is written by hand) ----
    $licStage = Join-Path $stage 'licenses'
    New-Item -ItemType Directory -Path $licStage -Force | Out-Null
    $srcLic7 = Join-Path $SevenZipDir 'License.txt'
    if (-not (Test-Path -LiteralPath $srcLic7)) { Fail "License.txt not found in '$SevenZipDir'. The 7-Zip license must ship with 7z.exe/7z.dll: install the full 7-Zip or pass -SevenZipDir to a folder that has it." }
    Copy-Item -LiteralPath $srcLic7 -Destination (Join-Path $licStage $SevenZipLicenseName) -Force

    # The innoextract LICENSE and the LGPL text are tracked in git: reuse the local copy when it matches the pin, download only when missing.
    $localInnoLic = Join-Path $LicensesDir $InnoLicenseName
    if ((Test-Path -LiteralPath $localInnoLic) -and ((Get-Sha256 $localInnoLic) -eq $InnoLicenseSha)) { Copy-Item -LiteralPath $localInnoLic -Destination (Join-Path $licStage $InnoLicenseName) -Force }
    else {
        try { Invoke-WebRequest -Uri $InnoLicenseUrl -OutFile (Join-Path $licStage $InnoLicenseName) -UseBasicParsing }
        catch { Fail "Could not download the innoextract LICENSE from $InnoLicenseUrl ($($_.Exception.Message)). Download that file manually, check that its SHA-256 is $InnoLicenseSha, and save it as app\tools\licenses\$InnoLicenseName; then re-run." }
    }
    $localLgpl = Join-Path $LicensesDir $LgplName
    if ((Test-Path -LiteralPath $localLgpl) -and ((Get-Sha256 $localLgpl) -eq $LgplSha)) { Copy-Item -LiteralPath $localLgpl -Destination (Join-Path $licStage $LgplName) -Force }
    else {
        try { Invoke-WebRequest -Uri $LgplUrl -OutFile (Join-Path $licStage $LgplName) -UseBasicParsing }
        catch { Fail "Could not download the LGPL 2.1 text from $LgplUrl ($($_.Exception.Message)). Download it manually, check that its SHA-256 is $LgplSha, and save it as app\tools\licenses\$LgplName; then re-run." }
    }
    $gotLgpl = Get-Sha256 (Join-Path $licStage $LgplName)
    if ($gotLgpl -ne $LgplSha) { Fail "The LGPL 2.1 text has SHA-256 $gotLgpl but the script pins $LgplSha. Not using it." }
    $gotLic = Get-Sha256 (Join-Path $licStage $InnoLicenseName)
    if ($gotLic -ne $InnoLicenseSha) { Fail "The innoextract LICENSE has SHA-256 $gotLic but the script pins $InnoLicenseSha. Not using it." }
    Write-Host "innoextract LICENSE and LGPL text match the pinned SHA-256 values."

    # innoextract's statically linked third-party licences, from the license\ folder next to innoextract.exe.
    New-Item -ItemType Directory -Path (Join-Path $licStage 'innoextract') -Force | Out-Null
    foreach ($n in $InnoThirdParty.Keys) {
        $src = Join-Path $InnoExtractDir "license\$n"
        if (-not (Test-Path -LiteralPath $src)) { Fail "license\$n not found in '$InnoExtractDir'. Use the official innoextract-1.9-windows.zip (it has a license folder beside innoextract.exe) or the winget package, or pass -InnoExtractDir to such a folder." }
        $dst = Join-Path $licStage "innoextract\$n"
        Copy-Item -LiteralPath $src -Destination $dst -Force
        if ((Get-Sha256 $dst) -ne $InnoThirdParty[$n]) { Fail "license\$n in '$InnoExtractDir' does not match the SHA-256 pinned in this script (the official innoextract $InnoLicenseVersion zip). Not using it." }
        $entries.Add([ordered]@{ name = "licenses/innoextract/$n"; version = $vi; sha256 = $InnoThirdParty[$n]; source = "local:innoextract license\$n"; category = 'license' })
    }

    $pkgRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }
    New-Item -ItemType Directory -Path (Join-Path $licStage 'dotnet') -Force | Out-Null
    foreach ($d in $DotnetFiles) {
        $src = Join-Path $pkgRoot "$($d.Pack)\$DotnetVersion\$($d.Src)"
        if (-not (Test-Path -LiteralPath $src)) { Fail "$src not found. The .NET $DotnetVersion runtime packs are not in the NuGet cache. Run this once from the repository root, then re-run this script: dotnet restore app/src/LegionGoConverter.App -r win-x64 -p:SelfContained=true   (use the .NET SDK that bundles runtime $DotnetVersion, 8.0.428; if NUGET_PACKAGES is set, the packs must be in that folder)." }
        Copy-Item -LiteralPath $src -Destination (Join-Path $licStage ('dotnet\' + $d.Dst)) -Force
        $entries.Add([ordered]@{ name = "licenses/dotnet/$($d.Dst)"; version = $DotnetVersion; sha256 = (Get-Sha256 (Join-Path $licStage ('dotnet\' + $d.Dst))); source = "nuget-cache:$($d.Pack)@$DotnetVersion/$($d.Src)"; category = 'license' })
    }
    $entries.Add([ordered]@{ name = "licenses/$SevenZipLicenseName"; version = $v7; sha256 = (Get-Sha256 (Join-Path $licStage $SevenZipLicenseName)); source = "local:7-Zip License.txt"; category = 'license' })
    $entries.Add([ordered]@{ name = "licenses/$LgplName"; version = '2.1'; sha256 = $gotLgpl; source = $LgplUrl; category = 'license' })
    $entries.Add([ordered]@{ name = "licenses/$InnoLicenseName"; version = $vi; sha256 = $gotLic; source = $InnoLicenseUrl; category = 'license' })

    # ---- Compare with the existing lock before replacing anything ----
    $old = Read-Lock
    if ($old) {
        $changed = @()
        foreach ($e in $entries) {
            $o = @($old.files | Where-Object { $_.name -eq $e.name })
            if ($o.Count -eq 1 -and $o[0].sha256 -ne $e.sha256) { $changed += "  $($e.name)`n    old: $($o[0].sha256)`n    new: $($e.sha256)" }
        }
        if ($changed.Count -gt 0) {
            Write-Warning "!!! The tools to be provisioned DIFFER from tools.lock.json !!!`n$($changed -join "`n")"
            if (-not $AcceptChangedTools) { Fail 'Hash mismatch against the lock. If this is an intended upgrade (for example a new 7-Zip version), re-run with -AcceptChangedTools and update app\THIRD-PARTY-NOTICES.md (version and source offer) to match.' }
            Write-Warning 'Continuing because -AcceptChangedTools was given. Update app\THIRD-PARTY-NOTICES.md (version and source offer) to match.'
        }
    }

    New-Item -ItemType Directory -Path (Join-Path $LicensesDir 'dotnet') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $LicensesDir 'innoextract') -Force | Out-Null
    foreach ($e in $entries) { Copy-Item -LiteralPath (Join-Path $stage $e.name) -Destination (Join-Path $ToolsDir $e.name) -Force }
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# Rewrite the tracked lock only when its content changed (the JSON layout differs between Windows PowerShell and pwsh,
# so a plain rewrite would dirty the working tree for no reason).
function ConvertTo-LockKey($Files) { ($Files | Sort-Object { $_.name } | ForEach-Object { "$($_.name)|$($_.version)|$($_.sha256)|$($_.source)|$($_.category)" }) -join "`n" }
$oldLock = Read-Lock
if ($oldLock -and ((ConvertTo-LockKey $oldLock.files) -eq (ConvertTo-LockKey $entries))) {
    Write-Host "tools.lock.json is already up to date ($($entries.Count) files); not rewritten."
} else {
    [ordered]@{ files = $entries } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $LockPath -Encoding UTF8
    Write-Host "Wrote $LockPath ($($entries.Count) files; categories: $(($entries | Group-Object { $_.category } | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '))."
}

Show-Licenses
Invoke-SmokeCheck
Show-Trust
Write-Host ''; Write-Host 'OK: app\tools is provisioned.'
