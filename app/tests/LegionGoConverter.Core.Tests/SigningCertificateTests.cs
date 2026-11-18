using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LegionGoConverter.Core;

namespace LegionGoConverter.Core.Tests;

public class SigningCertificateTests
{

    [Fact]
    public void GetOrCreate_CalledTwice_ReturnsSameThumbprint()
    {
        var subject = TestCerts.NewSubject();
        try
        {
            using var a = SigningCertificate.GetOrCreate(subject);
            using var b = SigningCertificate.GetOrCreate(subject);
            Assert.Equal(a.Thumbprint, b.Thumbprint);
            Assert.Equal(subject, a.Subject);
            Assert.True(a.HasPrivateKey);
            Assert.True(a.NotAfter > DateTime.Now.AddYears(9));
            Assert.Equal(3072, a.GetRSAPublicKey()!.KeySize);
            var eku = a.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single();
            Assert.Contains(eku.EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.3");
            var ku = a.Extensions.OfType<X509KeyUsageExtension>().Single();
            Assert.True(ku.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature));
        }
        finally { TestCerts.RemoveFromCurrentUserMy(subject); }
    }

    [Fact]
    public void GetOrCreate_PersistsInCurrentUserMy()
    {
        var subject = TestCerts.NewSubject();
        try
        {
            using var a = SigningCertificate.GetOrCreate(subject);
            using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadOnly);
            Assert.Single(store.Certificates.Find(X509FindType.FindByThumbprint, a.Thumbprint, false));
        }
        finally { TestCerts.RemoveFromCurrentUserMy(subject); }
    }

    [Fact]
    public void Cleanup_DeletesPrivateKeyContainer_AndRefusesNonTestSubjects()
    {
        var subject = TestCerts.NewSubject();
        string keyName;
        using (var a = SigningCertificate.GetOrCreate(subject))
        {
            using var rsa = (RSACng)a.GetRSAPrivateKey()!;
            keyName = rsa.Key.KeyName!;
            Assert.True(CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey));
        }
        TestCerts.RemoveFromCurrentUserMy(subject);
        Assert.False(CngKey.Exists(keyName, CngProvider.MicrosoftSoftwareKeyStorageProvider, CngKeyOpenOptions.UserKey));
        Assert.Throws<InvalidOperationException>(() => TestCerts.RemoveFromCurrentUserMy(SigningCertificate.DefaultSubject));
    }

    [Fact]
    public void GetOrCreate_KeyIsNotExportable()
    {
        var subject = TestCerts.NewSubject();
        try
        {
            using var a = SigningCertificate.GetOrCreate(subject);
            using var rsa = (RSACng)a.GetRSAPrivateKey()!;
            Assert.False(rsa.Key.ExportPolicy.HasFlag(CngExportPolicies.AllowExport));
        }
        finally { TestCerts.RemoveFromCurrentUserMy(subject); }
    }

    [Fact]
    public void IsKeyExportable_FalseForTheAppsOwnKey_TrueForAnExportableOne_FalseWithoutKey()
    {
        // final review finding 11: the spike certificate (same subject) was made by New-SelfSignedCertificate, exportable
        var subject = TestCerts.NewSubject();
        try
        {
            using var own = SigningCertificate.GetOrCreate(subject);
            Assert.False(SigningCertificate.IsKeyExportable(own));
        }
        finally { TestCerts.RemoveFromCurrentUserMy(subject); }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=eph", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var eph = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var exportable = new X509Certificate2(eph.Export(X509ContentType.Pfx, "pw"), "pw", X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
        Assert.True(SigningCertificate.IsKeyExportable(exportable));
        using var publicOnly = new X509Certificate2(eph.RawData);
        Assert.False(SigningCertificate.IsKeyExportable(publicOnly));
    }

    [Fact]
    public void DefaultSubject_IsExpected() =>
        Assert.Equal("CN=Legion Go Driver Converter (local self-signed)", SigningCertificate.DefaultSubject);
}

public class TrustStoreTests
{
    sealed class FakeTrustStore : ITrustStore
    {
        public HashSet<string> Trusted = new(StringComparer.OrdinalIgnoreCase);
        public int Writes;
        public bool IsTrusted(string t) => Trusted.Contains(t);
        public void Trust(X509Certificate2 c) { if (IsTrusted(c.Thumbprint)) return; Trusted.Add(c.Thumbprint); Writes++; }
        public void Untrust(string t, string expectedSubject) { Trusted.Remove(t); }
    }

    static X509Certificate2 Ephemeral()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=eph", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void Trust_AlreadyTrusted_IsNoOp()
    {
        ITrustStore store = new FakeTrustStore();
        using var cert = Ephemeral();
        store.Trust(cert);
        store.Trust(cert);
        Assert.True(store.IsTrusted(cert.Thumbprint));
        Assert.Equal(1, ((FakeTrustStore)store).Writes);
        store.Untrust(cert.Thumbprint, cert.Subject);
        Assert.False(store.IsTrusted(cert.Thumbprint));
    }

    [Fact]
    public void LocalMachineTrustStore_ValidatesArguments_WithoutTouchingStores()
    {
        var store = new LocalMachineTrustStore();
        Assert.Throws<ArgumentNullException>(() => store.Trust(null!));
        Assert.Throws<ArgumentException>(() => store.IsTrusted(""));
        Assert.Throws<ArgumentException>(() => store.Untrust(" ", SigningCertificate.DefaultSubject));
        Assert.Throws<ArgumentException>(() => store.Untrust("AB", " "));
    }
}

public class LocalMachineTrustStoreSeamTests
{
    sealed class FakeOps : ICertStoreOps
    {
        public Dictionary<StoreName, HashSet<string>> Stores = new()
        {
            [StoreName.Root] = new(StringComparer.OrdinalIgnoreCase),
            [StoreName.TrustedPublisher] = new(StringComparer.OrdinalIgnoreCase),
        };
        public StoreName? FailAddOn;
        public List<string> Log = new();
        public Dictionary<string, string> SubjectOf = new(StringComparer.OrdinalIgnoreCase);
        public bool Contains(StoreName s, string t) => Stores[s].Contains(t);
        public void Add(StoreName s, X509Certificate2 c)
        {
            Log.Add($"add {s}");
            Assert.False(c.HasPrivateKey);
            if (s == FailAddOn) throw new TrustStoreException($"Administrator rights are required to modify the LocalMachine\\{s} certificate store");
            Stores[s].Add(c.Thumbprint);
            SubjectOf[c.Thumbprint] = c.Subject;
        }
        public void Remove(StoreName s, string t) { Log.Add($"remove {s}"); Stores[s].Remove(t); }
        public IReadOnlyList<string> Subjects(StoreName s, string t) =>
            Stores[s].Contains(t) ? new[] { SubjectOf.GetValueOrDefault(t, "") } : Array.Empty<string>();
    }

    [Fact]
    public void Untrust_SubjectDiffers_RefusesAndRemovesNothing()
    {
        // final review finding 3: a planted manifest must not be able to untrust an arbitrary certificate
        var ops = new FakeOps(); var s = new LocalMachineTrustStore(ops); using var c = Cert();
        s.Trust(c);
        var ex = Assert.Throws<TrustStoreException>(() => s.Untrust(c.Thumbprint, SigningCertificate.DefaultSubject));
        Assert.Contains("CN=eph", ex.Message);
        Assert.Contains(SigningCertificate.DefaultSubject, ex.Message);
        Assert.DoesNotContain(ops.Log, l => l.StartsWith("remove"));
        Assert.True(s.IsTrusted(c.Thumbprint));
    }

    [Fact]
    public void Untrust_SubjectDiffersInOnlyOneStore_RefusesAndRemovesNothing()
    {
        var ops = new FakeOps(); var s = new LocalMachineTrustStore(ops); using var c = Cert();
        s.Trust(c);
        var mixed = new MixedOps(ops, StoreName.TrustedPublisher, "CN=someone else"); // Root matches, TrustedPublisher does not
        var ex = Assert.Throws<TrustStoreException>(() => new LocalMachineTrustStore(mixed).Untrust(c.Thumbprint, "CN=eph"));
        Assert.Contains("someone else", ex.Message);
        Assert.DoesNotContain(ops.Log, l => l.StartsWith("remove"));
    }

    sealed class MixedOps(FakeOps inner, StoreName odd, string oddSubject) : ICertStoreOps
    {
        public bool Contains(StoreName s, string t) => inner.Contains(s, t);
        public void Add(StoreName s, X509Certificate2 c) => inner.Add(s, c);
        public void Remove(StoreName s, string t) => inner.Remove(s, t);
        public IReadOnlyList<string> Subjects(StoreName s, string t) => s == odd ? new[] { oddSubject } : inner.Subjects(s, t);
    }

    static X509Certificate2 Cert()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=eph", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    [Fact]
    public void Trust_AddsToBothStores_ThenIsNoOp()
    {
        var ops = new FakeOps(); var s = new LocalMachineTrustStore(ops); using var c = Cert();
        s.Trust(c); s.Trust(c);
        Assert.True(s.IsTrusted(c.Thumbprint));
        Assert.Equal(2, ops.Log.Count(l => l.StartsWith("add")));
    }

    [Fact]
    public void Trust_SecondStoreFails_RollsBackFirstAndThrowsAdminMessage()
    {
        var ops = new FakeOps { FailAddOn = StoreName.TrustedPublisher };
        var s = new LocalMachineTrustStore(ops); using var c = Cert();
        var ex = Assert.Throws<TrustStoreException>(() => s.Trust(c));
        Assert.Contains("Administrator", ex.Message);
        Assert.Contains("TrustedPublisher", ex.Message);
        Assert.Empty(ops.Stores[StoreName.Root]);
        Assert.Contains("remove Root", ops.Log);
    }

    [Fact]
    public void Trust_FirstStoreFails_NothingToRollBack()
    {
        var ops = new FakeOps { FailAddOn = StoreName.Root };
        var s = new LocalMachineTrustStore(ops); using var c = Cert();
        Assert.Throws<TrustStoreException>(() => s.Trust(c));
        Assert.DoesNotContain(ops.Log, l => l.StartsWith("remove"));
    }

    [Fact]
    public void Untrust_NotTrusted_IsSilentNoOp_WithNoRemoveCalls()
    {
        var ops = new FakeOps(); var s = new LocalMachineTrustStore(ops);
        s.Untrust("ABCDEF", "CN=eph");
        Assert.Empty(ops.Log);
    }

    [Fact]
    public void Untrust_Trusted_RemovesFromBoth()
    {
        var ops = new FakeOps(); var s = new LocalMachineTrustStore(ops); using var c = Cert();
        s.Trust(c); s.Untrust(c.Thumbprint, c.Subject);
        Assert.False(s.IsTrusted(c.Thumbprint));
        Assert.Empty(ops.Stores[StoreName.Root]);
        Assert.Empty(ops.Stores[StoreName.TrustedPublisher]);
    }
}
