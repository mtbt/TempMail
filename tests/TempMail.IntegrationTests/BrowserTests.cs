using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using TempMail.SmtpServer;
namespace TempMail.IntegrationTests;
public sealed class BrowserFactAttribute : FactAttribute
{
    public BrowserFactAttribute() { if (Environment.GetEnvironmentVariable("TEMPMAIL_BROWSER_SMOKE") != "1") Skip = "Opt-in Chromium smoke: set TEMPMAIL_BROWSER_SMOKE=1 and PLAYWRIGHT_MODULE (see README)."; }
}
public sealed class BrowserTests
{
    [BrowserFact]
    public async Task BrowserCreatesMailboxReceivesRealtimeMailAndAdminSignsIn()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var factory = new MailFactory(); factory.UseKestrel(o => o.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate))); factory.StartServer();
        var url = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var password = "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>(); Assert.True((await roles.CreateAsync(new IdentityRole("Admin"))).Succeeded);
            var users = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>(); var user = new IdentityUser { UserName = "admin@example.com", Email = "admin@example.com", EmailConfirmed = true };
            Assert.True((await users.CreateAsync(user, password)).Succeeded); Assert.True((await users.AddToRoleAsync(user, "Admin")).Succeeded);
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accept = listener.AcceptTcpClientAsync(timeout.Token).AsTask();
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var start = new ProcessStartInfo("node") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(Path.Combine(root, "tests/browser-smoke.cjs"));
        start.Environment["TEMPMAIL_TEST_URL"] = url; start.Environment["TEMPMAIL_TEST_SMTP_PORT"] = port.ToString(); start.Environment["TEMPMAIL_TEST_ADMIN_PASSWORD"] = password;
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        var exited = process.WaitForExitAsync(timeout.Token);
        var first = await Task.WhenAny(accept, exited);
        if (first == exited) { listener.Stop(); Assert.Fail(await stdout + "\n" + await stderr); }
        var peer = await accept; listener.Stop();
        var worker = ActivatorUtilities.CreateInstance<SmtpWorker>(factory.Services);
        await worker.HandleAsync(peer, "127.0.0.1", timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        Assert.True(process.ExitCode == 0, await stdout + "\n" + await stderr);
    }
}
