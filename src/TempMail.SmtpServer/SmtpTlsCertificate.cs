using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using TempMail.Application;

namespace TempMail.SmtpServer;

/// <summary>Owns a startup snapshot. Invalid explicit configuration fails closed.</summary>
public sealed class SmtpTlsCertificate : IDisposable
{
    public X509Certificate2? Certificate { get; }
    public bool IsAvailable => Certificate is { } cert &&
        DateTime.UtcNow >= cert.NotBefore.ToUniversalTime() && DateTime.UtcNow < cert.NotAfter.ToUniversalTime();

    public SmtpTlsCertificate(SmtpOptions options)
    {
        X509Certificate2? certificate = null;
        try
        {
            var tls = options.Tls;
            var storeConfigured = !string.IsNullOrWhiteSpace(tls.CertificateThumbprint);
            var fileConfigured = !string.IsNullOrWhiteSpace(tls.PfxPath);
            if (!storeConfigured && !fileConfigured)
            {
                if (options.RequireStartTls) throw new InvalidOperationException();
                return;
            }
            if (storeConfigured && fileConfigured || string.IsNullOrWhiteSpace(tls.ServerName)) throw new InvalidOperationException();
            if (storeConfigured)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                if (!Enum.TryParse<StoreLocation>(tls.StoreLocation, out var location) || !Enum.IsDefined(location)) throw new InvalidOperationException();
                using var store = new X509Store(tls.StoreName, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var thumbprint = string.Concat(tls.CertificateThumbprint!.Where(c => !char.IsWhiteSpace(c)));
                var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                try
                {
                    if (matches.Count != 1) throw new InvalidOperationException();
                    certificate = new X509Certificate2(matches[0]);
                }
                finally { foreach (var match in matches) match.Dispose(); }
            }
            else
            {
                if (!Path.IsPathFullyQualified(tls.PfxPath!)) throw new InvalidOperationException();
                // Schannel needs an OS-backed key handle for SslStream. Do not make
                // imported keys exportable or persist them beyond certificate disposal.
                var keyStorageFlags = OperatingSystem.IsWindows()
                    ? X509KeyStorageFlags.DefaultKeySet
                    : X509KeyStorageFlags.EphemeralKeySet;
                certificate = X509CertificateLoader.LoadPkcs12FromFile(tls.PfxPath!, tls.PfxPassword, keyStorageFlags);
            }
            if (!certificate.HasPrivateKey || DateTime.UtcNow < certificate.NotBefore.ToUniversalTime() ||
                DateTime.UtcNow >= certificate.NotAfter.ToUniversalTime() || !certificate.MatchesHostname(tls.ServerName!)) throw new InvalidOperationException();
            foreach (var eku in certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>())
                if (!eku.EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value is "1.3.6.1.5.5.7.3.1" or "2.5.29.37.0")) throw new InvalidOperationException();
            foreach (var usage in certificate.Extensions.OfType<X509KeyUsageExtension>())
                if (!usage.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)) throw new InvalidOperationException();
            // Exercise private-key access under the actual service identity before listening.
            var probe = RandomNumberGenerator.GetBytes(32);
            using var rsa = certificate.GetRSAPrivateKey();
            using var ecdsa = certificate.GetECDsaPrivateKey();
            if (rsa != null) rsa.SignData(probe, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            else if (ecdsa != null) ecdsa.SignData(probe, HashAlgorithmName.SHA256);
            else throw new InvalidOperationException();
            Certificate = certificate;
        }
        catch
        {
            certificate?.Dispose();
            // Do not expose provider exceptions, file paths or supplied secrets in host logs.
            throw new InvalidOperationException("SMTP TLS configuration is invalid. Check certificate source, hostname, validity, server-auth usage and private-key permissions.");
        }
    }

    public void Dispose() => Certificate?.Dispose();
}
