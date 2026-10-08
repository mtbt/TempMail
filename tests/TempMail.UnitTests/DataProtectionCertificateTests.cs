using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using TempMail.Web.Security;

namespace TempMail.UnitTests;

public sealed class DataProtectionCertificateTests
{
    [Fact]
    public void SelfSignedRsaCertificateProtectsPersistedKeysAcrossProviders()
    {
        using var original = CreateRsaCertificate();
        var certificates = new X509Certificate2Collection(original);
        Assert.Empty(certificates.Find(X509FindType.FindByThumbprint, original.Thumbprint, validOnly: true));
        using var selected = DataProtectionCertificate.Find(certificates, original.Thumbprint);
        original.Dispose(); // The selected certificate survives disposal of the store snapshot.
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        try
        {
            string protectedValue;
            using (var services = CreateProvider(directory, selected))
                protectedValue = services.GetRequiredService<IDataProtectionProvider>().CreateProtector("regression").Protect("mailbox-token");

            var keyXml = File.ReadAllText(Assert.Single(directory.GetFiles("key-*.xml")).FullName);
            Assert.Contains("encryptedSecret", keyXml);
            Assert.DoesNotContain("<masterKey", keyXml);
            using var restarted = CreateProvider(directory, selected);
            Assert.Equal("mailbox-token", restarted.GetRequiredService<IDataProtectionProvider>().CreateProtector("regression").Unprotect(protectedValue));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void RejectsMissingOrAmbiguousMatches(int count)
    {
        using var certificate = CreateRsaCertificate();
        var certificates = new X509Certificate2Collection();
        for (var i = 0; i < count; i++) certificates.Add(certificate);
        var exception = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificate.Find(certificates, certificate.Thumbprint));
        Assert.Contains("exactly one", exception.Message);
    }

    [Fact]
    public void RejectsCertificateWithoutPrivateKey()
    {
        using var certificate = CreateRsaCertificate();
        using var publicOnly = X509CertificateLoader.LoadCertificate(certificate.Export(X509ContentType.Cert));
        var exception = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificate.Find(new(publicOnly), publicOnly.Thumbprint));
        Assert.Contains("private key", exception.Message);
    }

    [Fact]
    public void RejectsNonRsaPrivateKey()
    {
        using var key = ECDsa.Create();
        using var certificate = new CertificateRequest("CN=TempMail DP test", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        var exception = Assert.Throws<InvalidOperationException>(() => DataProtectionCertificate.Find(new(certificate), certificate.Thumbprint));
        Assert.Contains("RSA", exception.Message);
    }

    private static X509Certificate2 CreateRsaCertificate()
    {
        using var key = RSA.Create(2048);
        return new CertificateRequest("CN=TempMail DP test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    private static ServiceProvider CreateProvider(DirectoryInfo directory, X509Certificate2 certificate)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("TempMail.Web.v1")
            .PersistKeysToFileSystem(directory).ProtectKeysWithCertificate(certificate);
        return services.BuildServiceProvider();
    }
}
