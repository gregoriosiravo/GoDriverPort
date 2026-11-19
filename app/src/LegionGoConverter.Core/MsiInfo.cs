using System.Runtime.InteropServices;
using System.Text;

namespace LegionGoConverter.Core;

/// <summary>Identity of an MSI package, read from its Property table via msi.dll (nothing hard-coded).</summary>
public sealed record MsiInfo(string ProductName, string ProductVersion, string ProductCode, string UpgradeCode = "")
{
    const uint IgnoreMachineState = 1; // MSIOPENPACKAGEFLAGS_IGNOREMACHINESTATE
    const uint ErrorSuccess = 0, ErrorMoreData = 234;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint MsiOpenPackageExW(string packagePath, uint options, out IntPtr hProduct);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint MsiGetPropertyW(IntPtr hInstall, string name, StringBuilder? value, ref uint size);

    [DllImport("msi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint MsiCloseHandle(IntPtr h);

    public static MsiInfo Read(string msiPath)
    {
        var rc = MsiOpenPackageExW(msiPath, IgnoreMachineState, out var h);
        if (rc != ErrorSuccess) throw new IOException($"Could not open MSI {msiPath} (msi.dll error {rc}).");
        try
        {
            return new MsiInfo(Get(h, "ProductName"), Get(h, "ProductVersion"), Get(h, "ProductCode"), Get(h, "UpgradeCode"));
        }
        finally { MsiCloseHandle(h); }
    }

    static string Get(IntPtr h, string name)
    {
        uint size = 0;
        var rc = MsiGetPropertyW(h, name, null, ref size);
        if (rc != ErrorSuccess && rc != ErrorMoreData) throw new IOException($"MsiGetProperty({name}) failed ({rc}).");
        var sb = new StringBuilder((int)size + 1);
        size = (uint)sb.Capacity;
        rc = MsiGetPropertyW(h, name, sb, ref size);
        if (rc != ErrorSuccess) throw new IOException($"MsiGetProperty({name}) failed ({rc}).");
        return sb.ToString();
    }
}
