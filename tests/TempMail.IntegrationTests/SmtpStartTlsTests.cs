using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Shared;
using TempMail.SmtpServer;

namespace TempMail.IntegrationTests;

public sealed class SmtpStartTlsTests
{
    [Fact]
    public async Task NoCertificateDoesNotAdvertiseAndStartTlsIsUnavailable()
    {
        await using var peer = await Peer.Create(false);
        Assert.DoesNotContain("STARTTLS", await peer.Ehlo());
        await peer.Expect("STARTTLS", "454");
        await peer.Expect("MAIL FROM:<sender@example.com>", "250");
    }

    [Fact]
    public async Task OptionalTlsStillAcceptsPlaintextMail()
    {
        await using var peer = await Peer.Create();
        Assert.Contains("250-STARTTLS", await peer.Ehlo());
        await peer.Deliver();
    }

    [Theory]
    [InlineData(SslProtocols.Tls12)]
    [InlineData(SslProtocols.Tls13)]
    public async Task HandshakeRequiresNewEhloAndResetsEnvelopeWithoutAllowingRelay(SslProtocols protocol)
    {
        await using var peer = await Peer.Create();
        Assert.Contains("250-STARTTLS", await peer.Ehlo());
        await peer.Expect("MAIL FROM:<old@example.com>", "250");
        await peer.Expect("RCPT TO:<tls-test@mail.example.com>", "250");
        await peer.Upgrade(protocol);
        await peer.Expect("MAIL FROM:<sender@example.com>", "503");
        await peer.Expect("HELO client.example", "503");
        await peer.Expect("RCPT TO:<tls-test@mail.example.com>", "503");
        await peer.Expect("DATA", "503");
        Assert.DoesNotContain("STARTTLS", await peer.Ehlo());
        await peer.Expect("RCPT TO:<tls-test@mail.example.com>", "503");
        await peer.Expect("DATA", "503");
        await peer.Expect("STARTTLS", "503");
        await peer.Expect("AUTH LOGIN", "502");
        await peer.Expect("MAIL FROM:<sender@example.com>", "250");
        await peer.Expect("RCPT TO:<victim@external.example>", "550 Relay denied");
        await peer.Expect("DATA", "503");
        await peer.Deliver();
    }

    [Fact]
    public async Task RequiredTlsRejectsPlaintextEnvelopeThenAllowsEncryptedDelivery()
    {
        await using var peer = await Peer.Create(requireTls: true);
        await peer.Ehlo();
        foreach (var command in new[] { "MAIL FROM:<sender@example.com>", "RCPT TO:<tls-test@mail.example.com>", "DATA" })
            await peer.Expect(command, "530");
        await peer.Upgrade();
        await peer.Ehlo();
        await peer.Deliver();
    }

    [Fact]
    public async Task BufferedPlaintextCommandsCannotCrossTlsBoundary()
    {
        await using var peer = await Peer.Create();
        await peer.Ehlo();
        await peer.Upgrade(pipeline: "EHLO injected.example\r\nMAIL FROM:<injected@example.com>\r\nRCPT TO:<tls-test@mail.example.com>\r\n");
        await peer.Expect("MAIL FROM:<sender@example.com>", "503");
        await peer.Ehlo();
        await peer.Expect("DATA", "503");
    }

    [Fact]
    public async Task FailedHandshakeClosesConnectionWithoutPlaintextFallback()
    {
        await using var peer = await Peer.Create();
        await peer.Ehlo();
        await peer.Expect("STARTTLS", "220");
        await peer.Send("EHLO plaintext-after-220.example");
        await peer.Session.WaitAsync(peer.Ct);
        try { Assert.Null(await peer.Read()); }
        catch (IOException) { /* TCP reset is also a closed connection. */ }
    }

    [Fact]
    public async Task HandshakeTimeoutClosesConnection()
    {
        await using var peer = await Peer.Create();
        await peer.Ehlo();
        await peer.Expect("STARTTLS", "220");
        await peer.Session.WaitAsync(peer.Ct);
        Assert.Null(await peer.Read());
    }

    [Fact]
    public async Task StartTlsRequiresEhloAndRejectsArguments()
    {
        await using var peer = await Peer.Create();
        await peer.Expect("STARTTLS", "503");
        await peer.Expect("HELO client.example", "250");
        await peer.Expect("STARTTLS", "503");
        await peer.Ehlo();
        await peer.Expect("STARTTLS unexpected", "501");
        await peer.Upgrade();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("hostname")]
    [InlineData("eku")]
    [InlineData("password")]
    [InlineData("missing")]
    [InlineData("both")]
    [InlineData("public-only")]
    public void InvalidExplicitCertificateConfigurationFailsClosed(string mode)
    {
        using var fixture = new CertificateFixture(mode);
        var error = Assert.Throws<InvalidOperationException>(() => new SmtpTlsCertificate(fixture.Options));
        Assert.DoesNotContain(fixture.Password, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void RequiredTlsWithoutCertificateFailsClosed() =>
        Assert.Throws<InvalidOperationException>(() => new SmtpTlsCertificate(new SmtpOptions { RequireStartTls = true }));

    private sealed class CertificateFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string Password { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        public X509Certificate2 Certificate { get; }
        public SmtpOptions Options { get; }
        public CertificateFixture(string mode = "valid")
        {
            Directory.CreateDirectory(directory);
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(mode == "eku" ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, false));
            var now = DateTimeOffset.UtcNow;
            Certificate = request.CreateSelfSigned(now.AddDays(mode == "future" ? 1 : -2), now.AddDays(mode == "expired" ? -1 : 2));
            var path = Path.Combine(directory, "server.pfx");
            if (mode == "public-only")
            {
                using var publicOnly = X509CertificateLoader.LoadCertificate(Certificate.RawData);
                File.WriteAllBytes(path, publicOnly.Export(X509ContentType.Pfx, Password));
            }
            else File.WriteAllBytes(path, Certificate.Export(X509ContentType.Pfx, Password));
            Options = new SmtpOptions { TlsHandshakeTimeoutSeconds = 2, Tls = new SmtpTlsOptions {
                ServerName = mode == "hostname" ? "wrong.example" : "localhost",
                PfxPath = mode == "missing" ? path + ".missing" : path,
                PfxPassword = mode == "password" ? "incorrect" : Password,
                CertificateThumbprint = mode == "both" ? Certificate.Thumbprint : null
            } };
        }
        public void Dispose() { Certificate.Dispose(); Directory.Delete(directory, true); }
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly MailFactory factory = new();
        private readonly CertificateFixture certificate = new();
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        private readonly TcpClient client = new();
        private SmtpWorker worker = null!;
        private HttpClient browser = null!;
        private Stream stream = null!;
        private StreamReader reader = null!;
        private StreamWriter writer = null!;
        public Task Session { get; private set; } = Task.CompletedTask;
        public CancellationToken Ct => timeout.Token;
        public static async Task<Peer> Create(bool withCertificate = true, bool requireTls = false)
        {
            var peer = new Peer();
            try
            {
                peer.browser = peer.factory.Browser(); await ApiTests.Session(peer.browser);
                var response = await peer.browser.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest("tls-test", "mail.example.com"));
                response.EnsureSuccessStatusCode();
                var options = withCertificate ? peer.certificate.Options : new SmtpOptions(); options.RequireStartTls = requireTls;
                peer.worker = new SmtpWorker(peer.factory.Services.GetRequiredService<IServiceScopeFactory>(), Options.Create(options), NullLogger<SmtpWorker>.Instance);
                using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
                await peer.client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, peer.Ct);
                var server = await listener.AcceptTcpClientAsync(peer.Ct);
                peer.Session = peer.worker.HandleAsync(server, "127.0.0.1", peer.Ct);
                peer.SetStream(peer.client.GetStream());
                Assert.StartsWith("220", await peer.Read());
                return peer;
            }
            catch { await peer.DisposeAsync(); throw; }
        }
        private void SetStream(Stream value)
        {
            stream = value;
            reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
            writer = new StreamWriter(stream, Encoding.ASCII, 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        }
        public async Task<string?> Read() => await reader.ReadLineAsync(Ct);
        public async Task Send(string command) => await writer.WriteLineAsync(command.AsMemory(), Ct);
        public async Task Expect(string command, string code) { await Send(command); Assert.StartsWith(code, await Read()); }
        public async Task<string> Ehlo()
        {
            await Send("EHLO client.example"); var lines = new List<string>();
            string line;
            do { line = await Read() ?? throw new IOException("Unexpected EOF"); Assert.StartsWith("250", line); lines.Add(line); } while (line.StartsWith("250-", StringComparison.Ordinal));
            return string.Join("\n", lines);
        }
        public async Task Upgrade(SslProtocols protocol = SslProtocols.Tls12 | SslProtocols.Tls13, string pipeline = "")
        {
            await writer.WriteAsync(("STARTTLS\r\n" + pipeline).AsMemory(), Ct); await writer.FlushAsync(Ct);
            Assert.StartsWith("220", await Read());
            reader.Dispose(); await writer.DisposeAsync();
            var ssl = new SslStream(stream, false, (_, cert, _, _) => cert?.GetCertHashString() == certificate.Certificate.Thumbprint);
            stream = ssl;
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost", EnabledSslProtocols = protocol }, Ct);
            SetStream(ssl);
            Assert.True(ssl.IsEncrypted);
            Assert.True(ssl.SslProtocol is SslProtocols.Tls12 or SslProtocols.Tls13);
            if (protocol is SslProtocols.Tls12 or SslProtocols.Tls13) Assert.Equal(protocol, ssl.SslProtocol);
        }
        public async Task Deliver()
        {
            await Expect("MAIL FROM:<sender@example.com>", "250");
            await Expect("RCPT TO:<tls-test@mail.example.com>", "250");
            await Expect("DATA", "354");
            await Expect("From: sender@example.com\r\nTo: tls-test@mail.example.com\r\nSubject: TLS delivery\r\n\r\nHello\r\n.", "250 Message accepted");
            Assert.Single((await browser.GetFromJsonAsync<MessageSummary[]>("/api/messages", Ct))!);
        }
        public async ValueTask DisposeAsync()
        {
            await timeout.CancelAsync(); client.Dispose();
            await Session;
            reader?.Dispose(); stream?.Dispose(); worker?.Dispose(); browser?.Dispose();
            factory.Dispose(); certificate.Dispose(); timeout.Dispose();
        }
    }
}
