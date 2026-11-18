# =====================================================================================================================
# HISTORICAL RESEARCH SCRIPT. UNSUPPORTED. Part of the early feasibility spike for Legion Go Driver Port.
# WARNING: THIS SCRIPT MAY CHANGE YOUR SYSTEM (driver store, certificate trust, installed software, display settings).
# Do not run it. It is kept only to document how the approach was explored. Use the application in app\ instead.
# No warranty. See ..\..\LICENSE.
# =====================================================================================================================
# Read-only: list active displays, orientation, current mode and every 32-bpp mode the driver offers.
Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices; using System.Collections.Generic;
public static class LgoDisp {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct DISPLAY_DEVICE { public int cb; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string DeviceName; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceString; public int StateFlags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceID; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=128)] public string DeviceKey; }
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct DEVMODE { [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmDeviceName; public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields; public int dmPositionX, dmPositionY; public int dmDisplayOrientation, dmDisplayFixedOutput; public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string dmFormName; public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency; public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight; }
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool EnumDisplayDevices(string dev, int i, ref DISPLAY_DEVICE dd, int flags);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern bool EnumDisplaySettingsEx(string dev, int mode, ref DEVMODE dm, int flags);
  public static List<string> Dump() { var o = new List<string>();
    for (int i=0;;i++){ var a=new DISPLAY_DEVICE(); a.cb=Marshal.SizeOf(a); if(!EnumDisplayDevices(null,i,ref a,0)) break; if((a.StateFlags&1)==0) continue;
      var m=new DISPLAY_DEVICE(); m.cb=Marshal.SizeOf(m); EnumDisplayDevices(a.DeviceName,0,ref m,0);
      var c=new DEVMODE(); c.dmSize=(short)Marshal.SizeOf(c); EnumDisplaySettingsEx(a.DeviceName,-1,ref c,0);
      o.Add(String.Format("== {0}  monitor='{1}'  primary={2}  current={3}x{4}@{5} orientation={6} (0=0,1=90,2=180,3=270)", a.DeviceName, m.DeviceString, (a.StateFlags&4)!=0, c.dmPelsWidth,c.dmPelsHeight,c.dmDisplayFrequency,c.dmDisplayOrientation));
      var set=new SortedSet<string>(); for(int k=0;;k++){ var d=new DEVMODE(); d.dmSize=(short)Marshal.SizeOf(d); if(!EnumDisplaySettingsEx(a.DeviceName,k,ref d,2)) break; if(d.dmBitsPerPel==32) set.Add(String.Format("{0,5}x{1,-5}@{2}", d.dmPelsWidth,d.dmPelsHeight,d.dmDisplayFrequency)); }
      o.Add("   modes: " + String.Join(" | ", set)); }
    return o; } }
'@
[LgoDisp]::Dump()
