using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LegionGoConverter.Core;

public static class SigningCertificate
{
    // Legacy identifier kept for compatibility: the subject keeps its pre-rename name so a certificate trusted by an earlier build is found and reused.
    public const string DefaultSubject = "CN=Legion Go Driver Converter (local self-signed)";

    /// <summary>True when the certificate's private key may be exported (e.g. a certificate with this subject made earlier by
    /// <c>New-SelfSignedCertificate</c>, whose default key policy is exportable). False without a private key.</summary>
    public static bool IsKeyExportable(X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(cert);
        if (!cert.HasPrivateKey) return false;
        try
        {
            using var key = cert.GetRSAPrivateKey();
            return key switch
            {
                RSACng cng => (cng.Key.ExportPolicy & (CngExportPolicies.AllowExport | CngExportPolicies.AllowPlaintextExport)) != 0,
                RSACryptoServiceProvider csp => csp.CspKeyContainerInfo.Exportable,
                _ => false,
            };
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>Returns a valid existing code-signing cert with this subject from CurrentUser\My, or creates and stores one.</summary>
    public static X509Certificate2 GetOrCreate(string subject = DefaultSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var now = DateTime.Now;
        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, subject, false)
            .Cast<X509Certificate2>()
            .Where(c => c.HasPrivateKey && c.NotBefore <= now && c.NotAfter > now)
            .OrderByDescending(c => c.NotAfter)
            .FirstOrDefault();
        if (existing != null) return existing;

        using var rsa = RSA.Create(3072);
        var req = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, critical: false));
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, critical: true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
        using var ephemeral = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));

        // Round-trip through PFX so the private key is persisted in the user's key container.
        var pw = Guid.NewGuid().ToString("N");
        var pfx = ephemeral.Export(X509ContentType.Pfx, pw);
        var cert = new X509Certificate2(pfx, pw,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        store.Add(cert);
        return cert;
    }
}
