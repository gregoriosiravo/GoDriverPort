using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LegionGoConverter.Core.Tests;

/// <summary>Cleanup for test certificates. Refuses anything that is not a GUID-subject test cert.</summary>
public static class TestCerts
{
    public static string NewSubject() => $"CN=LGO Test {Guid.NewGuid():N}";

    static void AssertTestSubject(string subject)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(subject, @"^CN=LGO Test [0-9a-f]{32}$"))
            throw new InvalidOperationException($"Refusing to delete non-test certificate: {subject}");
    }

    /// <summary>Removes test certs from CurrentUser\My and deletes their persisted private keys.</summary>
    public static void RemoveFromCurrentUserMy(string subject)
    {
        AssertTestSubject(subject);
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var c in store.Certificates.Find(X509FindType.FindBySubjectDistinguishedName, subject, false))
        {
            using (c)
            {
                AssertTestSubject(c.Subject);
                using var rsa = c.GetRSAPrivateKey();
                if (rsa is RSACng cng) cng.Key.Delete(); // removes the key file, not just the store entry
                else (rsa as IDisposable)?.Dispose();
                store.Remove(c);
            }
        }
    }
}
