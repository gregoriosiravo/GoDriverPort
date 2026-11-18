# =====================================================================================================================
# HISTORICAL RESEARCH SCRIPT. UNSUPPORTED. Part of the early feasibility spike for Legion Go Driver Port.
# WARNING: THIS SCRIPT MAY CHANGE YOUR SYSTEM (driver store, certificate trust, installed software, display settings).
# Do not run it. It is kept only to document how the approach was explored. Use the application in app\ instead.
# No warranty. See ..\..\LICENSE.
# =====================================================================================================================
<#
Dot-source helper for AMD's amdgcf.dat (device-ID -> revision allowlist read by the kernel driver).
Format (documented by the MIT-licensed LeGo1-graphic-driver-toolkit; verified by header self-test on AMD 26.10.2 and Lenovo 25.30.44):
  [0..3]  uint32 record count
  [4..11] MetroHash64 (seed 0), big-endian, over (record bytes + ASCII release string)
  [12..]  3-byte records sorted by (device, revision): uint16 storedOffset (= device + recordPosition), byte revision
Release string = INF DriverBuildNumber without its leading timestamp segment.
#>
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

function Get-AmdReleaseString([string]$InfPath) {
  $m = Select-String -Path $InfPath -Pattern '^HKR,,DriverBuildNumber,%REG_SZ%,(.+)$' | Select-Object -First 1
  if (-not $m) { throw "DriverBuildNumber not found in $InfPath" }
  $b = $m.Matches[0].Groups[1].Value.Trim()
  return $b.Substring($b.IndexOf('-') + 1)
}

function Get-AmdGcfHeader([byte[]]$Dat, [string]$Release) {
  $in = [byte[]]($Dat[12..($Dat.Length-1)] + [Text.Encoding]::ASCII.GetBytes($Release))
  $h = [LgoMetroHash64]::Hash($in, 0)
  $be = [BitConverter]::GetBytes([uint64]$h); [Array]::Reverse($be); return ,$be
}

function Read-AmdGcf([string]$Path, [string]$Release) {
  $dat = [IO.File]::ReadAllBytes($Path)
  $count = [BitConverter]::ToUInt32($dat, 0)
  if ($dat.Length -ne 12 + 3*$count) { throw "amdgcf.dat length $($dat.Length) does not match $count records" }
  $calc = Get-AmdGcfHeader $dat $Release
  if ((Compare-Object $calc $dat[4..11] -SyncWindow 0)) { throw 'amdgcf.dat header self-test FAILED: format or release string not understood' }
  $recs = for ($i = 0; $i -lt $count; $i++) { $p = 12 + 3*$i; [pscustomobject]@{ Dev = ([BitConverter]::ToUInt16($dat, $p) - $p) -band 0xFFFF; Rev = [int]$dat[$p+2] } }
  return ,@($recs)
}

function Write-AmdGcf([object[]]$Records, [string]$Release, [string]$Path) {
  $sorted = @($Records | Sort-Object Dev, Rev)
  $out = [byte[]]::new(12 + 3*$sorted.Count)
  [Array]::Copy([BitConverter]::GetBytes([uint32]$sorted.Count), 0, $out, 0, 4)
  for ($i = 0; $i -lt $sorted.Count; $i++) {
    $p = 12 + 3*$i; $so = $sorted[$i].Dev + $p
    if ($so -gt 0xFFFF) { throw 'stored offset overflow' }
    $b = [BitConverter]::GetBytes([uint16]$so); $out[$p] = $b[0]; $out[$p+1] = $b[1]; $out[$p+2] = [byte]$sorted[$i].Rev
  }
  [Array]::Copy((Get-AmdGcfHeader $out $Release), 0, $out, 4, 8)
  [IO.File]::WriteAllBytes($Path, $out)
}
