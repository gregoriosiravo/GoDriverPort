<#
.SYNOPSIS
  One-command build of Legion Go Driver Port from a clone. No administrator rights needed.

.DESCRIPTION
  1. checks that a .NET 8 SDK is available (global.json pins the 8.0 line)
  2. restores the win-x64 runtime packs (a fresh clone does not have them; the .NET licence files are copied from them)
  3. provisions app\tools\ with app\tools\fetch-tools.ps1 (skip with -SkipTools when it is already set up)
       prerequisites: 7-Zip installed (https://www.7-zip.org/) and innoextract (winget install dscharrer.innoextract)
  4. runs `dotnet test app` (skip with -SkipTests)
  5. runs app\publish.ps1 (app\publish\: the exe, 7-Zip, innoextract and licence texts; NO Microsoft SDK file; never launches the app)
  6. -Zip        writes dist\LegionGoDriverPort-<version>-portable.zip from app\publish\ and checks that it has no Microsoft SDK file
  7. -Installer  compiles installer\LegionGoDriverPort.iss (from the same app\publish\) with Inno Setup 6 (ISCC.exe) to dist\LegionGoDriverPort-<version>-Setup.exe
  8. writes dist\SHA256SUMS.txt for the files in dist\

  Neither download contains signtool.exe or makecat.exe: the app uses the user's own Windows SDK (see README.md).

  The version comes from Directory.Build.props (<Version>) and is passed to the publish and the installer.
  dist\ is git-ignored. Releases are unsigned.

.PARAMETER SkipTests    Do not run the unit tests.
.PARAMETER SkipTools    Do not run fetch-tools.ps1 (app\tools is already provisioned).
.PARAMETER Zip          Also produce the portable zip.
.PARAMETER Installer    Also compile the Inno Setup installer (needs ISCC.exe).
.PARAMETER Dotnet       Path to dotnet.exe (default: PATH, then C:\Program Files\dotnet\dotnet.exe).
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$SkipTools,
    [switch]$Zip,
    [switch]$Installer,
    [string]$Dotnet
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Fail([string]$Msg) { throw "build: $Msg" }
function Step([string]$Msg) { Write-Host ''; Write-Host "== $Msg" -ForegroundColor Cyan }

$Root    = $PSScriptRoot
$AppDir  = Join-Path $Root 'app'
$Dist    = Join-Path $Root 'dist'
$Publish = Join-Path $AppDir 'publish'            # input of both the installer and the portable zip (no Microsoft SDK files)

# ---- Version (single source: Directory.Build.props) --------------------------------------------
$propsPath = Join-Path $Root 'Directory.Build.props'
if (-not (Test-Path -LiteralPath $propsPath)) { Fail "Directory.Build.props not found in $Root." }
$Version = ([xml](Get-Content -LiteralPath $propsPath -Raw)).Project.PropertyGroup.Version
if (-not $Version -or $Version -notmatch '^\d+\.\d+\.\d+$') { Fail "<Version> in Directory.Build.props must look like 1.2.3 (found '$Version')." }
Write-Host "Legion Go Driver Port $Version"

# ---- .NET SDK ----------------------------------------------------------------------------------
Step 'Checking the .NET SDK'
if (-not $Dotnet) {
    $cmd = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $Dotnet = $cmd.Source }
    elseif (Test-Path -LiteralPath (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')) { $Dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
    else { Fail 'dotnet not found. Install the .NET 8 SDK (winget install Microsoft.DotNet.SDK.8, or https://dotnet.microsoft.com/download/dotnet/8.0), or pass -Dotnet <path>.' }
}
Push-Location $Root   # so that global.json is honoured
try {
    $sdkVersion = (& $Dotnet --version 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { Fail "No usable .NET 8 SDK: 'dotnet --version' said: $sdkVersion`nInstall the .NET 8 SDK (global.json requires 8.0.x)." }
} finally { Pop-Location }
Write-Host "  dotnet $sdkVersion ($Dotnet)"

# ---- Tools -------------------------------------------------------------------------------------
Push-Location $Root   # global.json is honoured only from inside the repository
try {
    # fetch-tools.ps1 copies the .NET runtime licence files from the NuGet cache, so the win-x64 runtime packs must be restored first
    # (a fresh clone does not have them; `dotnet test` does not restore them).
    Step 'Restoring the win-x64 runtime packs (needed for the .NET licence files and the self-contained publish)'
    & $Dotnet restore (Join-Path $AppDir 'src\LegionGoConverter.App') -r win-x64 -p:SelfContained=true
    if ($LASTEXITCODE -ne 0) { Fail "dotnet restore (win-x64 runtime packs) failed with exit code $LASTEXITCODE." }

    if ($SkipTools) { Step 'Skipping tool provisioning (-SkipTools)' }
    else {
        Step 'Provisioning app\tools (7-Zip, innoextract and their licence texts; nothing from Microsoft)'
        Write-Host '  prerequisites: 7-Zip installed, and innoextract (winget install dscharrer.innoextract)'
        try { & (Join-Path $AppDir 'tools\fetch-tools.ps1') }
        catch { Fail "fetch-tools.ps1 failed:`n$($_.Exception.Message)`nIf 7-Zip or innoextract is missing, install it or pass its folder to app\tools\fetch-tools.ps1 -SevenZipDir / -InnoExtractDir. If your 7-Zip is a different version than the one recorded in app\tools\tools.lock.json, run app\tools\fetch-tools.ps1 -Force -AcceptChangedTools and update app\THIRD-PARTY-NOTICES.md (7-Zip version and source offer). Then re-run (-SkipTools if app\tools is already provisioned)." }
    }

    # ---- Tests -----------------------------------------------------------------------------------
    if ($SkipTests) { Step 'Skipping tests (-SkipTests)' }
    else {
        Step 'dotnet test app'
        & $Dotnet test $AppDir
        if ($LASTEXITCODE -ne 0) { Fail "dotnet test failed with exit code $LASTEXITCODE." }
    }

    # ---- Publish ---------------------------------------------------------------------------------
    Step 'app\publish.ps1'
    & (Join-Path $AppDir 'publish.ps1') -Dotnet $Dotnet
} finally { Pop-Location }
if (-not (Test-Path -LiteralPath (Join-Path $Publish 'LegionGoDriverPort.exe'))) { Fail 'publish.ps1 did not produce app\publish\LegionGoDriverPort.exe.' }
$msPattern = '(?i)makecat|signtool|mssign32|wintrust|appxpackaging|appxsip|opcservices|msisip|Microsoft\.Windows\.Build\.'
if (@(Get-ChildItem -LiteralPath $Publish -Recurse -Force -File | Where-Object { $_.Name -match $msPattern }).Count -gt 0) { Fail 'The publish folder contains Microsoft SDK files; refusing to package it.' }

New-Item -ItemType Directory -Path $Dist -Force | Out-Null
$zipName   = "LegionGoDriverPort-$Version-portable.zip"
$setupName = "LegionGoDriverPort-$Version-Setup.exe"
$problems  = @()

# ---- Portable zip ------------------------------------------------------------------------------
if ($Zip) {
    Step "Portable zip: dist\$zipName"
    $zipPath = Join-Path $Dist $zipName
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $fs = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
    try {
        $za = New-Object IO.Compression.ZipArchive($fs, [IO.Compression.ZipArchiveMode]::Create)
        try {
            $base = (Resolve-Path -LiteralPath $Publish).Path.TrimEnd('\')
            foreach ($f in (Get-ChildItem -LiteralPath $base -Recurse -File -Force | Sort-Object FullName)) {
                $rel = $f.FullName.Substring($base.Length).TrimStart('\').Replace('\', '/')
                [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, "LegionGoDriverPort/$rel", [IO.Compression.CompressionLevel]::Optimal)
            }
        } finally { $za.Dispose() }
    } finally { $fs.Dispose() }
    # Verify: the exe is in it and no Microsoft SDK file is.
    $zr = [IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        $names = @($zr.Entries | ForEach-Object FullName)
        if ($names -notcontains 'LegionGoDriverPort/LegionGoDriverPort.exe') { Fail 'The zip does not contain the exe.' }
        $ms = @($names | Where-Object { $_ -match $msPattern })
        if ($ms.Count -gt 0) { Fail ('The portable zip contains Microsoft SDK files: ' + ($ms -join ', ')) }
        Write-Host ("  {0} entries, {1:N0} bytes, no Microsoft SDK files (no signtool, no makecat)" -f $names.Count, (Get-Item -LiteralPath $zipPath).Length)
    } finally { $zr.Dispose() }
}

# ---- Installer ---------------------------------------------------------------------------------
if ($Installer) {
    Step "Installer: dist\$setupName"
    $iscc = $null
    $cmd = Get-Command ISCC.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $iscc = $cmd.Source }
    else {
        $cands = @()
        foreach ($pf in $env:ProgramFiles, ${env:ProgramFiles(x86)}) { if ($pf) { $cands += (Join-Path $pf 'Inno Setup 6\ISCC.exe') } }
        if ($env:LOCALAPPDATA) { $cands += (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe') }
        $iscc = $cands | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    }
    if (-not $iscc) {
        $problems += 'Installer NOT built: ISCC.exe (Inno Setup 6) was not found.'
        Write-Warning 'Inno Setup 6 (ISCC.exe) not found on PATH or in the standard locations.'
        Write-Host '  Get it from https://jrsoftware.org/isdl.php, or:  winget install JRSoftware.InnoSetup   (or: choco install innosetup -y)'
        Write-Host '  then re-run: .\build.ps1 -Installer -SkipTools -SkipTests'
    } else {
        Write-Host "  ISCC: $iscc"
        $setupPath = Join-Path $Dist $setupName
        if (Test-Path -LiteralPath $setupPath) { Remove-Item -LiteralPath $setupPath -Force }
        & $iscc "/DAppVersion=$Version" (Join-Path $Root 'installer\LegionGoDriverPort.iss')
        if ($LASTEXITCODE -ne 0) { Fail "ISCC failed with exit code $LASTEXITCODE." }
        if (-not (Test-Path -LiteralPath $setupPath)) { Fail "ISCC finished but $setupName is not in dist\." }
        Write-Host ("  {0:N0} bytes" -f (Get-Item -LiteralPath $setupPath).Length)
    }
}

# ---- SHA256SUMS --------------------------------------------------------------------------------
$artifacts = @(Get-ChildItem -LiteralPath $Dist -File | Where-Object { $_.Name -in $zipName, $setupName })
if ($artifacts.Count -gt 0) {
    Step 'dist\SHA256SUMS.txt'
    $lines = foreach ($a in ($artifacts | Sort-Object Name)) { '{0}  {1}' -f (Get-FileHash -LiteralPath $a.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $a.Name }
    # LF line endings, no BOM, so that `sha256sum -c` accepts it.
    [IO.File]::WriteAllText((Join-Path $Dist 'SHA256SUMS.txt'), (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding($false)))
    $lines | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host ''
    Write-Host 'No packaged artifact requested: pass -Zip and/or -Installer to write files to dist\.'
}

Write-Host ''
if ($problems.Count -gt 0) { $problems | ForEach-Object { Write-Warning $_ }; exit 1 }
Write-Host "OK. Published folder: $Publish   (nothing was launched or installed)"
exit 0
