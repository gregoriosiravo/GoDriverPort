# =====================================================================================================================
# HISTORICAL RESEARCH SCRIPT. UNSUPPORTED. Part of the early feasibility spike for Legion Go Driver Port.
# WARNING: THIS SCRIPT MAY CHANGE YOUR SYSTEM (driver store, certificate trust, installed software, display settings).
# Do not run it. It is kept only to document how the approach was explored. Use the application in app\ instead.
# No warranty. See ..\..\LICENSE.
# =====================================================================================================================
<#
Replace Lenovo's Store "AMD Radeon Software" with the AMD Software (Adrenalin) that ships inside the AMD 26.10.2 driver
(ccc2_install.exe -> ccc-next64.msi + amddvr64.msi), installed directly with msiexec so AMD's Setup.exe device check is skipped.
Run from an admin PowerShell, then reboot.
Undo: msiexec /x {5E5E4309-86A8-4B2A-84E1-A686D2B185EA} and /x {9CE31951-23D9-40F7-B738-63B49B9B552A};
      Lenovo's app can be reinstalled from the Microsoft Store ("AMD Radeon Software").
#>
param([Parameter(Mandatory)][string]$SoftwareDir)   # folder containing cnext\ and amddvr\ from the extracted AMD Software installer
$ErrorActionPreference = 'Stop'
$logDir = Join-Path $PSScriptRoot 'logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir 'install-amd-software.log'
function Log($m) { $l = "{0:u}  {1}" -f (Get-Date), $m; Write-Host $l; Add-Content $log $l -Encoding UTF8 }
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this from an admin PowerShell.' }

$products = @(
  [pscustomobject]@{ Name='AMD Settings'; Msi=Join-Path $SoftwareDir 'cnext\cnext64\ccc-next64.msi'; Code='{5E5E4309-86A8-4B2A-84E1-A686D2B185EA}'; Extra=@() }
  [pscustomobject]@{ Name='AMD DVR';      Msi=Join-Path $SoftwareDir 'amddvr\amddvr64\amddvr64.msi'; Code='{9CE31951-23D9-40F7-B738-63B49B9B552A}'; Extra=@('LAUNCHED_FROM_CIM=1') }
)
function Get-Installed($code) { Get-ItemProperty "HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\$code" -ErrorAction SilentlyContinue }

# 0. pre-checks: files present and AMD-signed
foreach ($p in $products) {
  if (-not (Test-Path $p.Msi)) { throw "Missing $($p.Msi)" }
  $s = Get-AuthenticodeSignature $p.Msi
  if ($s.Status -ne 'Valid' -or $s.SignerCertificate.Subject -notmatch '^CN=Advanced Micro Devices') { throw "$($p.Msi) is not validly AMD-signed ($($s.Status))" }
  Log "OK signature: $($p.Name) <- $($p.Msi)"
}
Log ("Before: " + ((Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*, HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\* -ErrorAction SilentlyContinue | Where-Object DisplayName -match 'AMD|Radeon' | ForEach-Object { "$($_.DisplayName) $($_.DisplayVersion)" }) -join '; '))

# 1. remove Lenovo's Store Radeon Software (all users + provisioned copy)
$appx = 'AdvancedMicroDevicesInc-2.AMDRadeonSoftware'
foreach ($pkg in @(Get-AppxPackage -AllUsers -Name $appx)) {
  Log "Removing Store app $($pkg.PackageFullName)"
  try { Remove-AppxPackage -Package $pkg.PackageFullName -AllUsers } catch { Remove-AppxPackage -Package $pkg.PackageFullName }
}
foreach ($prov in @(Get-AppxProvisionedPackage -Online | Where-Object DisplayName -eq $appx)) {
  Log "Removing provisioned copy $($prov.PackageName)"; Remove-AppxProvisionedPackage -Online -PackageName $prov.PackageName | Out-Null
}
if (@(Get-AppxPackage -AllUsers -Name $appx).Count) { throw 'Store app still registered; stopping before installing AMD Software.' }
Log 'Store app removed (or was not present)'

# 2. install AMD Settings, then AMD DVR
foreach ($p in $products) {
  if (Get-Installed $p.Code) { Log "$($p.Name) already installed, skipping"; continue }
  $vlog = Join-Path $logDir ("msi-" + ($p.Name -replace ' ','-') + '.log')
  $msiArgs = @('/i', "`"$($p.Msi)`"") + $p.Extra + @('/qn','/norestart','REBOOT=ReallySuppress','/L*v', "`"$vlog`"")
  Log "Installing $($p.Name) (this can take a few minutes)..."
  $proc = Start-Process "$env:WINDIR\System32\msiexec.exe" -ArgumentList $msiArgs -Wait -PassThru
  Log "$($p.Name): msiexec exit $($proc.ExitCode)  (log: $vlog)"
  if ($proc.ExitCode -notin 0, 1641, 3010) { throw "$($p.Name) failed with exit code $($proc.ExitCode). See $vlog" }
  $i = Get-Installed $p.Code
  if (-not $i) { throw "$($p.Name) not registered after install" }
  Log "Installed: $($i.DisplayName) $($i.DisplayVersion)"
}
Log 'Done. Reboot now.'

