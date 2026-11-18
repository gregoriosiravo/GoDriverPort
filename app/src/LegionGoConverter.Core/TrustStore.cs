using System.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LegionGoConverter.Core;

public interface ITrustStore
{
    bool IsTrusted(string thumbprint);
    void Trust(X509Certificate2 cert);
    /// <summary>Removes the certificate from the trusted stores, but only when every stored copy has exactly the subject
    /// <paramref name="expectedSubject"/>; otherwise throws <see cref="TrustStoreException"/> and removes nothing.</summary>
    void Untrust(string thumbprint, string expectedSubject);
}

public class TrustStoreException : Exception
{
    public TrustStoreException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>The two operations the trust store needs from a certificate store, per store name (test seam).</summary>
internal interface ICertStoreOps
{
    /// <summary>True when the cert is present; a missing store counts as "not present".</summary>
    bool Contains(StoreName store, string thumbprint);
    /// <summary>Adds the (public-only) cert. Throws <see cref="TrustStoreException"/> on failure.</summary>
    void Add(StoreName store, X509Certificate2 publicCert);
    /// <summary>Removes the cert if present (opens for write only on a match). Throws <see cref="TrustStoreException"/> on failure.</summary>
    void Remove(StoreName store, string thumbprint);
    /// <summary>The subjects of the certificates with this thumbprint in the store (empty when none or no store).</summary>
    IReadOnlyList<string> Subjects(StoreName store, string thumbprint);
}

internal sealed class LocalMachineCertStoreOps : ICertStoreOps
{
    static TrustStoreException Wrap(StoreName s, Exception e) => new(
        $"Administrator rights are required to modify the LocalMachine\\{s} certificate store: {e.Message}", e);

    public bool Contains(StoreName store, string thumbprint)
    {
        try
        {
            using var s = new X509Store(store, StoreLocation.LocalMachine);
            s.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return s.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Count > 0;
        }
        catch (CryptographicException) { return false; } // store does not exist
    }

    public void Add(StoreName store, X509Certificate2 publicCert)
    {
        try
        {
            using var s = new X509Store(store, StoreLocation.LocalMachine);
            s.Open(OpenFlags.ReadWrite);
            s.Add(publicCert);
        }
        catch (Exception e) when (e is CryptographicException or UnauthorizedAccessException or SecurityException)
        { throw Wrap(store, e); }
    }

    public IReadOnlyList<string> Subjects(StoreName store, string thumbprint)
    {
        try
        {
            using var s = new X509Store(store, StoreLocation.LocalMachine);
            s.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
            return s.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false).Select(c => c.Subject).ToList();
        }
        catch (CryptographicException) { return Array.Empty<string>(); } // store does not exist
    }

    public void Remove(StoreName store, string thumbprint)
    {
        if (!Contains(store, thumbprint)) return;
        try
        {
            using var s = new X509Store(store, StoreLocation.LocalMachine);
            s.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);
            foreach (var c in s.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, false)) s.Remove(c);
        }
        catch (Exception e) when (e is CryptographicException or UnauthorizedAccessException or SecurityException)
        { throw Wrap(store, e); }
    }
}

/// <summary>LocalMachine Root + TrustedPublisher. Requires admin. Only the public certificate is ever stored.</summary>
public sealed class LocalMachineTrustStore : ITrustStore
{
    static readonly StoreName[] Stores = { StoreName.Root, StoreName.TrustedPublisher };
    readonly ICertStoreOps _ops;

    public LocalMachineTrustStore() : this(new LocalMachineCertStoreOps()) { }
    internal LocalMachineTrustStore(ICertStoreOps ops) { _ops = ops; }

    public bool IsTrusted(string thumbprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        return Stores.All(n => _ops.Contains(n, thumbprint));
    }

    public void Trust(X509Certificate2 cert)
    {
        ArgumentNullException.ThrowIfNull(cert);
        if (IsTrusted(cert.Thumbprint)) return;
        using var publicOnly = new X509Certificate2(cert.RawData); // never carries the private key
        var added = new List<StoreName>();
        try
        {
            foreach (var n in Stores)
            {
                if (_ops.Contains(n, cert.Thumbprint)) continue;
                _ops.Add(n, publicOnly);
                added.Add(n);
            }
        }
        catch
        {
            // roll back what this call added so the cert is never left half-trusted
            foreach (var n in added) { try { _ops.Remove(n, cert.Thumbprint); } catch (TrustStoreException) { } }
            throw;
        }
    }

    public void Untrust(string thumbprint, string expectedSubject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(thumbprint);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedSubject);
        // Check every store first, so a mismatch removes nothing anywhere.
        foreach (var n in Stores)
        {
            if (!_ops.Contains(n, thumbprint)) continue;
            var wrong = _ops.Subjects(n, thumbprint).Where(s => !string.Equals(s, expectedSubject, StringComparison.Ordinal)).ToList();
            if (wrong.Count > 0)
                throw new TrustStoreException(
                    $"Refusing to remove certificate {thumbprint} from LocalMachine\\{n}: its subject is '{string.Join("', '", wrong)}', " +
                    $"not '{expectedSubject}'. It is not this app's certificate, so it was left alone.");
        }
        foreach (var n in Stores)
            if (_ops.Contains(n, thumbprint)) _ops.Remove(n, thumbprint);
    }
}
