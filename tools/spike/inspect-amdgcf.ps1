# =====================================================================================================================
# HISTORICAL RESEARCH SCRIPT. UNSUPPORTED. Part of the early feasibility spike for Legion Go Driver Port.
# WARNING: THIS SCRIPT MAY CHANGE YOUR SYSTEM (driver store, certificate trust, installed software, display settings).
# Do not run it. It is kept only to document how the approach was explored. Use the application in app\ instead.
# No warranty. See ..\..\LICENSE.
# =====================================================================================================================
<#
Read-only inspection of AMD's amdgcf.dat (device-ID -> revision allowlist read by the kernel driver).
Format (as documented by the MIT-licensed LeGo1-graphic-driver-toolkit, verified here by a hash self-test):
  [0..3]  uint32 record count
  [4..11] MetroHash64 (seed 0), big-endian, over (records bytes + ASCII release string)
  [12..]  3-byte records: uint16 storedOffset (= deviceId + recordPosition, mod 0x10000), byte revision
Release string = INF DriverBuildNumber without its leading timestamp segment.
#>
param(
  [Parameter(Mandatory)][string]$DatPath,   # path to amdgcf.dat inside the extracted AMD package
  [Parameter(Mandatory)][string]$InfPath,   # path to the display INF in the same package
  [int[]]$ShowDevice = @(0x15BF, 0x15C8)
)
$ErrorActionPreference = 'Stop'
if (-not ('LgoMetroHash64' -as [type])) {
Add-Type -Language CSharp -TypeDefinition @'
using System;
public static class LgoMetroHash64 {
  const ulong K0=0xD6D018F5UL,K1=0xA2AA033BUL,K2=0x62992FC1UL,K3=0x30BC5B29UL;
  static ulong R(ulong v,int b){return (v>>b)|(v<<(64-b));}
  static ulong U64(byte[]d,int o){return BitConverter.ToUInt64(d,o);}
  static uint U32(byte[]d,int o){return BitConverter.ToUInt32(d,o);}
  static ushort U16(byte[]d,int o){return BitConverter.ToUInt16(d,o);}
  public static ulong Hash(byte[] d, ulong seed) { unchecked {
    int len=d.Length,o=0; ulong h=(seed+K2)*K0;
    if(len>=32){ ulong v0=h,v1=h,v2=h,v3=h; int end=len-32;
      while(o<=end){ v0+=U64(d,o)*K0;o+=8;v0=R(v0,29)+v2; v1+=U64(d,o)*K1;o+=8;v1=R(v1,29)+v3;
                     v2+=U64(d,o)*K2;o+=8;v2=R(v2,29)+v0; v3+=U64(d,o)*K3;o+=8;v3=R(v3,29)+v1; }
      v2^=R(((v0+v3)*K0)+v1,37)*K1; v3^=R(((v1+v2)*K1)+v0,37)*K0; v0^=R(((v0+v2)*K0)+v3,37)*K1; v1^=R(((v1+v3)*K1)+v2,37)*K0; h+=v0^v1; }
    int rem=len-o;
    if(rem>=16){ ulong v0=h+(U64(d,o)*K2);o+=8;v0=R(v0,29)*K3; ulong v1=h+(U64(d,o)*K2);o+=8;v1=R(v1,29)*K3;
                 v0^=R(v0*K0,21)+v1; v1^=R(v1*K3,21)+v0; h+=v1; rem=len-o; }
    if(rem>=8){ h+=U64(d,o)*K3;o+=8; h^=R(h,55)*K1; rem=len-o; }
    if(rem>=4){ h+=(ulong)U32(d,o)*K3;o+=4; h^=R(h,26)*K1; rem=len-o; }
    if(rem>=2){ h+=(ulong)U16(d,o)*K3;o+=2; h^=R(h,48)*K1; rem=len-o; }
    if(rem>=1){ h+=(ulong)d[o]*K3; h^=R(h,37)*K1; }
    h^=R(h,28); h*=K0; h^=R(h,29); return h; } }
}
'@
}
$dat = [IO.File]::ReadAllBytes($DatPath)
$count = [BitConverter]::ToUInt32($dat, 0)
"File: $DatPath"
"Length: $($dat.Length)  Records: $count  Expected length: $(12 + 3*$count)"
$build = (Select-String -Path $InfPath -Pattern '^HKR,,DriverBuildNumber,%REG_SZ%,(.+)$').Matches[0].Groups[1].Value.Trim()
$release = $build.Substring($build.IndexOf('-') + 1)
"INF DriverBuildNumber: $build"
"Release string used:   $release"
$stored = ($dat[4..11] | ForEach-Object { '{0:X2}' -f $_ }) -join ''
$input_ = [byte[]]($dat[12..($dat.Length-1)] + [Text.Encoding]::ASCII.GetBytes($release))
$calc = '{0:X16}' -f [LgoMetroHash64]::Hash($input_, 0)
"Header stored: $stored"
"Header calc:   $calc   -> self-test $(if ($stored -eq $calc) {'PASS (format understood)'} else {'FAIL'})"
$recs = for ($i = 0; $i -lt $count; $i++) { $p = 12 + 3*$i; [pscustomobject]@{ Dev = ([BitConverter]::ToUInt16($dat, $p) - $p) -band 0xFFFF; Rev = $dat[$p+2] } }
foreach ($dev in $ShowDevice) {
  $revs = ($recs | Where-Object Dev -eq $dev | ForEach-Object { '{0:X2}' -f $_.Rev }) -join ' '
  "DEV_{0:X4} revisions allowed: {1}" -f $dev, $(if ($revs) { $revs } else { '(none)' })
}
