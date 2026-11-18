# =====================================================================================================================
# HISTORICAL RESEARCH SCRIPT. UNSUPPORTED. Part of the early feasibility spike for Legion Go Driver Port.
# WARNING: THIS SCRIPT MAY CHANGE YOUR SYSTEM (driver store, certificate trust, installed software, display settings).
# Do not run it. It is kept only to document how the approach was explored. Use the application in app\ instead.
# No warranty. See ..\..\LICENSE.
# =====================================================================================================================
<#
Rollback of a spike install of the patched AMD package.
Finds the store packages that came from the converted package by ORIGINAL NAME + DRIVER VERSION
(published oemNN numbers change between installs, so they are never hard-coded),
removes them display-driver first so the 780M falls back to Lenovo's u0202094.inf, then removes the cert trust.
Run as admin, then reboot.
#>
param([Parameter(Mandatory)][string]$PackageDir)   # the patched WT6A_INF folder that build-patched-package.ps1 produced
$ErrorActionPreference = 'Continue'
$log = Join-Path (Split-Path $PackageDir) 'rollback.log'
function Log($m) { $l = "{0:u}  {1}" -f (Get-Date), $m; Write-Host $l; Add-Content $log $l -Encoding UTF8 }
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an admin PowerShell.' }

# What our package contains: original INF name -> DriverVer version
$ours = @{}
Get-ChildItem $PackageDir -Recurse -Filter *.inf | ForEach-Object {
  $v = (Select-String -Path $_.FullName -Pattern '^\s*DriverVer\s*=\s*[^,]+,\s*([\d\.]+)' | Select-Object -First 1).Matches.Groups[1].Value
  if ($v) { $ours[$_.Name.ToLower()] = $v }
}
# What is in the store
$raw = pnputil /enum-drivers | Out-String
$store = foreach ($b in ($raw -split "(\r?\n){2,}" | Where-Object { $_ -match 'Published Name' })) {
  [pscustomobject]@{
    Oem  = [regex]::Match($b, 'Published Name:\s+(\S+)').Groups[1].Value
    Orig = [regex]::Match($b, 'Original Name:\s+(\S+)').Groups[1].Value.ToLower()
    Ver  = [regex]::Match($b, 'Driver Version:\s+\S+\s+([\d\.]+)').Groups[1].Value
  }
}
$targets = @($store | Where-Object { $_.Ver -and $ours.ContainsKey($_.Orig) -and [version]$ours[$_.Orig] -eq [version]$_.Ver })   # [version]: 26.10.0.0001 == 26.10.0.1
# display driver first
$targets = @($targets | Sort-Object { if ($_.Orig -eq 'u0205499.inf') { 0 } else { 1 } })
Log ("Will remove: " + (($targets | ForEach-Object { "$($_.Oem)=$($_.Orig) $($_.Ver)" }) -join '; '))
foreach ($t in $targets) {
  Log "Removing $($t.Oem) ($($t.Orig) $($t.Ver))"
  pnputil /delete-driver $t.Oem /uninstall | ForEach-Object { Log "   $_" }
}

$thumb = '777C7D479879BCB167901701A72CABF29DD9BEB9'
foreach ($s in 'Root','TrustedPublisher') {
  Get-ChildItem "Cert:\LocalMachine\$s" | Where-Object Thumbprint -eq $thumb | ForEach-Object { Remove-Item $_.PSPath; Log "Removed cert from LocalMachine\$s" }
}
$d = Get-PnpDevice -Class Display | Where-Object InstanceId -match 'DEV_15BF'
Log ("780M now: status={0} version={1} inf={2} problem={3}" -f $d.Status,
  (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName DEVPKEY_Device_DriverVersion).Data,
  (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName DEVPKEY_Device_DriverInfPath).Data,
  (Get-PnpDeviceProperty -InstanceId $d.InstanceId -KeyName DEVPKEY_Device_ProblemCode).Data)
Log 'Done. Reboot now.'
