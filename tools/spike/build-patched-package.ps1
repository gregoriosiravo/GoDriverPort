# =====================================================================================================================
# HISTORICAL RESEARCH SCRIPT. UNSUPPORTED. Part of the early feasibility spike for Legion Go Driver Port.
# WARNING: THIS SCRIPT MAY CHANGE YOUR SYSTEM (driver store, certificate trust, installed software, display settings).
# Do not run it. It is kept only to document how the approach was explored. Use the application in app\ instead.
# No warranty. See ..\..\LICENSE.
# =====================================================================================================================
<#
Spike: build a Legion Go-patched copy of AMD's display driver package.
Does NOT install anything and does NOT change machine trust. It:
  1. copies the AMD Display\WT6A_INF tree to an output folder (source untouched)
  2. adds the device's real hardware ID to the Win11 models section, reusing ati2mtag_Phoenix
  2b. adds the device/revision to amdgcf.dat (kernel driver allowlist) and recomputes its MetroHash64 header
  3. builds a new SHA256 catalog (makecat) covering the INF and every file in the binaries folder
  4. signs it with a self-signed code-signing cert kept in CurrentUser\My (created if missing)
  5. exports the public cert (.cer) next to the package for manual trusting
#>
param(
    [Parameter(Mandatory)][string]$AmdDisplayDir,   # <extracted AMD package>\Packages\Drivers\Display\WT6A_INF
    [string]$OutRoot       = (Join-Path $PSScriptRoot 'out\amd-patched'),
    [Parameter(Mandatory)][string]$ToolsDir,        # folder with makecat.exe and signtool.exe
    [string]$HardwareId    = '',   # empty = read from the device
    [string]$CertSubject   = 'CN=Legion Go Driver Converter (local self-signed)'
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutRoot | Out-Null
$log = Join-Path $OutRoot 'build.log'
function Log($m) { $line = "{0:u}  {1}" -f (Get-Date), $m; Write-Host $line; Add-Content -Path $log -Value $line -Encoding UTF8 }

# --- 1. hardware ID
if (-not $HardwareId) {
    $dev = Get-PnpDevice -Class Display | Where-Object InstanceId -match 'VEN_1002&DEV_15BF'
    if (-not $dev) { throw 'No AMD 15BF display device found.' }
    $HardwareId = (Get-PnpDeviceProperty -InstanceId $dev[0].InstanceId -KeyName DEVPKEY_Device_HardwareIds).Data[0]
}
if ($HardwareId -notmatch '^PCI\\VEN_1002&DEV_15BF&SUBSYS_[0-9A-F]{8}&REV_[0-9A-F]{2}$') { throw "Unexpected hardware ID: $HardwareId" }
Log "Hardware ID: $HardwareId"

# --- 2. copy package
$out = Join-Path $OutRoot 'WT6A_INF'
Log "Copying $AmdDisplayDir -> $out"
robocopy $AmdDisplayDir $out /E /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }

$infName = 'u0205499.inf'; $catName = 'u0205499.cat'
$inf = Join-Path $out $infName

# --- 3. patch INF (ASCII/CRLF, no BOM: preserved byte-for-byte except the inserted line)
$bytes = [IO.File]::ReadAllBytes((Join-Path $AmdDisplayDir $infName))
if ($bytes.Length -ge 2 -and (($bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) -or ($bytes[0] -eq 0xEF))) { throw 'INF has a BOM; this spike script only handles plain ASCII INFs.' }
if ($bytes | Where-Object { $_ -gt 127 } | Select-Object -First 1) { throw 'INF has non-ASCII bytes; refusing to re-encode.' }
$text = [Text.Encoding]::ASCII.GetString($bytes)
if ($text -match [regex]::Escape($HardwareId)) { throw 'Hardware ID already present in INF.' }

$section = '[ATI.Mfg.NTamd64.10.0.1..19044]'
$secStart = $text.IndexOf("`r`n$section`r`n")
if ($secStart -lt 0) { throw "Section $section not found" }
$secEnd = $text.IndexOf("`r`n[", $secStart + $section.Length + 2)
$body = $text.Substring($secStart, $secEnd - $secStart)
$last = [regex]::Matches($body, '(?m)^"%AMD15BF\.\d+%" = ati2mtag_Phoenix, PCI\\VEN_1002&DEV_15BF[^\r\n]*\r\n') | Select-Object -Last 1
if (-not $last) { throw 'No 15BF Phoenix line in section' }
if ($text -notmatch '(?m)^AMD15BF\.781\s*= "AMD Radeon 780M Graphics"') { throw 'String AMD15BF.781 not found/changed' }
$newLine = "`"%AMD15BF.781%`" = ati2mtag_Phoenix, $HardwareId`r`n"
$insertAt = $secStart + $last.Index + $last.Length
$patched = $text.Insert($insertAt, $newLine)
[IO.File]::WriteAllBytes($inf, [Text.Encoding]::ASCII.GetBytes($patched))
Log "Inserted after AMD line '$($last.Value.Trim())': $($newLine.Trim())"
Log ("INF size {0} -> {1} bytes (+{2})" -f $bytes.Length, (Get-Item $inf).Length, $newLine.Length)

# --- 3b. patch amdgcf.dat: the kernel driver's own device/revision allowlist (cause of Code 43 without it)
. (Join-Path $PSScriptRoot 'AmdGcf.ps1')
$hw = [regex]::Match($HardwareId, 'DEV_([0-9A-F]{4}).*REV_([0-9A-F]{2})')
$dev = [Convert]::ToInt32($hw.Groups[1].Value, 16); $rev = [Convert]::ToInt32($hw.Groups[2].Value, 16)
$srcDat = Join-Path $AmdDisplayDir 'B026716\amdgcf.dat'
$dstDat = Join-Path $out 'B026716\amdgcf.dat'
$release = Get-AmdReleaseString (Join-Path $AmdDisplayDir $infName)
$recs = Read-AmdGcf $srcDat $release      # throws unless the header self-test passes
Log "amdgcf.dat: $($recs.Count) records, header self-test PASS (release '$release')"
if ($recs | Where-Object { $_.Dev -eq $dev -and $_.Rev -eq $rev }) { throw ('amdgcf.dat already allows {0:X4}/{1:X2}' -f $dev, $rev) }
Write-AmdGcf (@($recs) + [pscustomobject]@{ Dev = $dev; Rev = $rev }) $release $dstDat
$check = Read-AmdGcf $dstDat $release     # re-verify what we wrote
if ($check.Count -ne $recs.Count + 1 -or -not ($check | Where-Object { $_.Dev -eq $dev -and $_.Rev -eq $rev })) { throw 'amdgcf.dat patch verification failed' }
Log ('amdgcf.dat: added DEV_{0:X4} REV_{1:X2} -> {2} records, re-read self-test PASS' -f $dev, $rev, $check.Count)

# --- 4. catalog
Remove-Item (Join-Path $out $catName) -ErrorAction SilentlyContinue
$bin = Join-Path $out 'B026716'
$cdf = Join-Path $OutRoot 'u0205499.cdf'
$sb = [Text.StringBuilder]::new()
[void]$sb.AppendLine('[CatalogHeader]')
[void]$sb.AppendLine("Name=$catName")
[void]$sb.AppendLine("ResultDir=$out")
[void]$sb.AppendLine('PublicVersion=0x0000001')
[void]$sb.AppendLine('CatalogVersion=2')
[void]$sb.AppendLine('HashAlgorithms=SHA256')
[void]$sb.AppendLine('EncodingType=0x00010001')
[void]$sb.AppendLine('CATATTR1=0x10010001:OSAttr:2:10.0')
[void]$sb.AppendLine('[CatalogFiles]')
$files = @(Get-Item $inf) + @(Get-ChildItem $bin -File)
foreach ($f in $files) {
    $tag = $f.Name
    [void]$sb.AppendLine("<hash>$tag=$($f.FullName)")
    [void]$sb.AppendLine("<hash>${tag}ATTR1=0x10010001:OSAttr:2:10.0")
    [void]$sb.AppendLine("<hash>${tag}ATTR2=0x10010001:File:$($f.Name)")
}
[IO.File]::WriteAllText($cdf, $sb.ToString(), [Text.Encoding]::ASCII)
Log "CDF written with $($files.Count) files"
& (Join-Path $ToolsDir 'makecat.exe') -v $cdf | Out-File -Append -Encoding utf8 $log
if ($LASTEXITCODE -ne 0) { throw "makecat failed ($LASTEXITCODE)" }
Log "Catalog built: $(Join-Path $out $catName)"

# --- 5. cert (CurrentUser\My only: no trust change) + sign
$cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $CertSubject -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $CertSubject -KeyAlgorithm RSA -KeyLength 3072 `
        -HashAlgorithm SHA256 -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(10)
    Log "Created self-signed cert $($cert.Thumbprint) in CurrentUser\My"
} else { Log "Reusing cert $($cert.Thumbprint)" }
$cer = Join-Path $OutRoot 'LegionGoDriverPort.cer'
Export-Certificate -Cert $cert -FilePath $cer -Type CERT | Out-Null
Log "Exported public cert: $cer"

& (Join-Path $ToolsDir 'signtool.exe') sign /fd SHA256 /sha1 $cert.Thumbprint /s My (Join-Path $out $catName) | Out-File -Append -Encoding utf8 $log
if ($LASTEXITCODE -ne 0) { throw "signtool failed ($LASTEXITCODE)" }
$sig = Get-AuthenticodeSignature (Join-Path $out $catName)
Log "Catalog signature: status=$($sig.Status) signer=$($sig.SignerCertificate.Subject) (UnknownError/NotTrusted is expected until the cert is trusted)"
Log 'DONE'
