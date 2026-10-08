using System.Security.Cryptography.X509Certificates;

namespace TempMail.Web.Security;

internal static class DataProtectionCertificate
{
    public static X509Certificate2 Load(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        var certificates = store.Certificates;
        try { return Find(certificates, thumbprint); }
        finally { foreach (var certificate in certificates) certificate.Dispose(); }
    }

    internal static X509Certificate2 Find(X509Certificate2Collection certificates, string thumbprint)
    {
        // This dedicated key-encryption certificate need not chain to a trusted root.
        var matches = certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
        if (matches.Count != 1)
            throw new InvalidOperationException("Data Protection requires exactly one certificate matching the configured thumbprint in LocalMachine\\My.");

        var certificate = matches[0];
        if (!certificate.HasPrivateKey)
            throw new InvalidOperationException("The Data Protection certificate must have a private key readable by the Web app-pool identity.");
        using var rsa = certificate.GetRSAPrivateKey();
        if (rsa is null)
            throw new InvalidOperationException("The Data Protection certificate must have an RSA private key.");

        // The caller owns this copy and must retain it for the host lifetime.
        return new X509Certificate2(certificate);
    }
}
