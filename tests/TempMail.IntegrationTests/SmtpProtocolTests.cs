using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TempMail.Infrastructure;
using TempMail.Shared;
using TempMail.SmtpServer;
namespace TempMail.IntegrationTests;
public sealed class SmtpProtocolTests
{
    [Fact] public async Task AutomationRequiresCreationBeforeSmtpAndCannotRecoverRejectedMail()
    {
        using var factory = new MailFactory(); using var api = factory.Browser();
        api.DefaultRequestHeaders.Add("X-Api-Token", factory.ExternalToken);
        var worker = ActivatorUtilities.CreateInstance<SmtpWorker>(factory.Services);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        var serverClient = await listener.AcceptTcpClientAsync(ct); listener.Stop();
        var session = worker.HandleAsync(serverClient, "127.0.0.1", ct);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII);
        await using var writer = new StreamWriter(client.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        async Task<string> Read() => await reader.ReadLineAsync(ct) ?? throw new IOException("Unexpected EOF");
        async Task Expect(string command, string code) { await writer.WriteLineAsync(command); Assert.StartsWith(code, await Read()); }
        Assert.StartsWith("220", await Read());
        await writer.WriteLineAsync("EHLO test.example");
        string line; do { line = await Read(); Assert.StartsWith("250", line); } while (line.StartsWith("250-", StringComparison.Ordinal));
        await Expect("MAIL FROM:<sender@example.com>", "250");
        await Expect("RCPT TO:<automation@mail.example.com>", "550 Mailbox unavailable");
        await Expect("DATA", "503");
        var created = await api.PostAsJsonAsync("/api/mailbox", new { email = "automation@mail.example.com" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(new LatestCodeResponse(null), await api.GetFromJsonAsync<LatestCodeResponse>("/api/latest-code?email=automation@mail.example.com"));
        await Expect("RCPT TO:<automation@mail.example.com>", "250");
        await Expect("DATA", "354");
        await writer.WriteAsync("From: sender@example.com\r\nTo: automation@mail.example.com\r\nDate: Tue, 1 Jan 2002 00:00:00 +0000\r\nSubject: OTP 012345\r\nContent-Type: text/plain\r\n\r\nCode\r\n.\r\n");
        await writer.FlushAsync(ct); Assert.StartsWith("250 Message accepted", await Read());
        await Expect("QUIT", "221"); await session;
        Assert.Equal(new LatestCodeResponse("012345"), await api.GetFromJsonAsync<LatestCodeResponse>("/api/latest-code?email=automation@mail.example.com"));
    }
    [Fact] public async Task ActualTcpConversationStoresMimeAndCannotRelay()
    {
        using var factory = new MailFactory(); using var browser = factory.Browser(); await ApiTests.Session(browser);
        var response = await browser.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest("tcp-test", "mail.example.com")); response.EnsureSuccessStatusCode();
        var worker = ActivatorUtilities.CreateInstance<SmtpWorker>(factory.Services);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20)); var ct = timeout.Token;
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var client = new TcpClient(); await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct);
        var serverClient = await listener.AcceptTcpClientAsync(ct); listener.Stop();
        var session = worker.HandleAsync(serverClient, "127.0.0.1", ct);
        using var reader = new StreamReader(client.GetStream(), Encoding.ASCII); await using var writer = new StreamWriter(client.GetStream(), Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        async Task<string> Read() => await reader.ReadLineAsync(ct) ?? throw new IOException("Unexpected EOF");
        async Task Expect(string command, string code) { await writer.WriteLineAsync(command); Assert.StartsWith(code, await Read()); }
        Assert.StartsWith("220", await Read());
        await Expect("RCPT TO:<tcp-test@mail.example.com>", "503");
        await writer.WriteLineAsync("EHLO test.example"); Assert.StartsWith("250-", await Read()); Assert.StartsWith("250-SIZE", await Read()); Assert.Equal("250 8BITMIME", await Read());
        await Expect("AUTH LOGIN", "502"); await Expect("MAIL FROM:<sender@example.com>", "250");
        await Expect("RCPT TO:<victim@elsewhere.example>", "550 Relay denied"); await Expect("RCPT TO:<missing@mail.example.com>", "550 Mailbox unavailable");
        await Expect("RCPT TO:<tcp-test@mail.example.com>", "250"); await Expect("DATA", "354");
        await writer.WriteAsync("From: sender@example.com\r\nTo: tcp-test@mail.example.com\r\nSubject: Real TCP test\r\nContent-Type: text/plain; charset=utf-8\r\n\r\nHello\r\n..dot-stuffed\r\n.\r\n"); await writer.FlushAsync(ct);
        Assert.StartsWith("250 Message accepted", await Read()); await Expect("NOOP", "250"); await Expect("RSET", "250"); await Expect("QUIT", "221"); await session;
        var messages = await browser.GetFromJsonAsync<MessageSummary[]>("/api/messages"); var message = Assert.Single(messages!); Assert.Equal("Real TCP test", message.Subject);
        var detail = await browser.GetFromJsonAsync<MessageDto>($"/api/messages/{message.Id}"); Assert.Contains(".dot-stuffed", detail!.TextBody);
    }
}
