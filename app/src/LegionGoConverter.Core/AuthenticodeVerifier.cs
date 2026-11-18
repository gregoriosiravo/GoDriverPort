using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace LegionGoConverter.Core;

/// <summary>Checks that a file carries a valid embedded Authenticode signature by Microsoft Corporation.
/// Returns null when it does, otherwise a short reason. Injectable so tests need no signed files.</summary>
public interface IAuthenticodeVerifier
{
    string? Check(string path);
}

/// <summary>WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2, no UI, no revocation lookup: the app makes no network
/// connections; chain building is cache-only, so it fails closed when an intermediate certificate is missing and not cached on this machine), plus a signer subject check. wintrust.dll is loaded from System32 only.</summary>
public sealed class WinTrustMicrosoftVerifier : IAuthenticodeVerifier
{
    static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    struct WintrustFileInfo { public uint cbStruct; public IntPtr pcwszFilePath; public IntPtr hFile; public IntPtr pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct WintrustData
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData; public IntPtr pSIPClientData;
        public uint dwUIChoice; public uint fdwRevocationChecks; public uint dwUnionChoice; public IntPtr pFile;
        public uint dwStateAction; public IntPtr hWVTStateData; public IntPtr pwszURLReference;
        public uint dwProvFlags; public uint dwUIContext; public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WintrustData data);

    public string? Check(string path)
    {
        if (!File.Exists(path)) return "the file does not exist";
        var pPath = Marshal.StringToHGlobalUni(path);
        var pFile = IntPtr.Zero;
        try
        {
            var fi = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = pPath };
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(fi, pFile, false);
            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(), dwUIChoice = 2 /* WTD_UI_NONE */, fdwRevocationChecks = 0 /* WTD_REVOKE_NONE */,
                dwUnionChoice = 1 /* WTD_CHOICE_FILE */, pFile = pFile, dwStateAction = 0 /* WTD_STATEACTION_IGNORE */,
                dwProvFlags = 0x10 /* WTD_REVOCATION_CHECK_NONE */ | 0x1000 /* WTD_CACHE_ONLY_URL_RETRIEVAL: chain building never touches the network */,
            };
            var action = GenericVerifyV2;
            int rc = WinVerifyTrust(new IntPtr(-1) /* INVALID_HANDLE_VALUE: no UI */, ref action, ref data);
            if (rc != 0)
            {
                var name = (uint)rc switch
                {
                    0x80096010 => "TRUST_E_BAD_DIGEST: the file was modified after it was signed",
                    0x800B0100 => "TRUST_E_NOSIGNATURE: no embedded signature",
                    0x800B0109 => "CERT_E_UNTRUSTEDROOT",
                    0x800B010A => "CERT_E_CHAINING",
                    0x800B010C => "CERT_E_REVOKED",
                    _ => "see the WinVerifyTrust return codes",
                };
                return $"Authenticode verification failed (WinVerifyTrust 0x{rc:X8}, {name})";
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pPath);
            if (pFile != IntPtr.Zero) Marshal.FreeHGlobal(pFile);
        }
        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            var subject = cert.Subject;
            if (!subject.Split(',').Select(p => p.Trim()).Contains("O=Microsoft Corporation", StringComparer.OrdinalIgnoreCase))
                return $"the signer is '{subject}', not Microsoft Corporation";
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or IOException)
        {
            return "the signer could not be read: " + e.Message;
        }
        return null;
    }
}
