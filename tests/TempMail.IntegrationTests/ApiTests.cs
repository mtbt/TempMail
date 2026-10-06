using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Domain;
using TempMail.Infrastructure;
using TempMail.Shared;
namespace TempMail.IntegrationTests;
public sealed class ApiTests : IClassFixture<MailFactory>
{
    private readonly MailFactory factory;
    public ApiTests(MailFactory factory) => this.factory = factory;
    private static string Unique() => "test" + Guid.NewGuid().ToString("N")[..12];
    internal static async Task Session(HttpClient c)
    {
        var session = await c.GetFromJsonAsync<SessionDto>("/api/session"); c.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); c.DefaultRequestHeaders.Add("X-CSRF-TOKEN", session!.RequestToken);
    }
    private static async Task<MailboxDto> Create(HttpClient c, string? name = null)
    {
        await Session(c); var response = await c.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest(name ?? Unique(), "mail.example.com"));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); return (await response.Content.ReadFromJsonAsync<MailboxDto>())!;
    }
    [Fact] public async Task CrossMailboxCannotReadMessageHtmlOrAttachmentOrDelete()
    {
        using var a = factory.Browser(); using var b = factory.Browser(); using var anonymous = factory.Browser();
        var boxA = await Create(a); await Create(b);
        Guid messageId, attachmentId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var receiver = scope.ServiceProvider.GetRequiredService<ReceiveService>();
            var raw = $"From: sender@example.com\r\nTo: {boxA.Address}\r\nSubject: private\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=x\r\n\r\n--x\r\nContent-Type: text/html\r\n\r\n<p onclick='evil()'>Secret</p><script>evil()</script><img src='https://track.example/pixel'>\r\n--x\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=secret.txt\r\nContent-Transfer-Encoding: base64\r\n\r\nc2VjcmV0\r\n--x--\r\n";
            await receiver.ReceiveAsync("sender@example.com", [boxA.Address], Encoding.UTF8.GetBytes(raw), default);
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            var msg = await db.Messages.Include(x => x.Attachments).SingleAsync(x => x.Mailbox.NormalizedAddress == boxA.Address); messageId = msg.Id; attachmentId = msg.Attachments.Single().Id;
            Assert.True(await db.Events.AnyAsync(x => x.MailboxPublicId == boxA.PublicId));
        }
        foreach (var path in new[] { $"/api/messages/{messageId}", $"/api/messages/{messageId}/html", $"/api/messages/{messageId}/html?images=true", $"/api/messages/{messageId}/attachments/{attachmentId}" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await b.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(path)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await a.GetAsync(path)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.NotFound, (await b.DeleteAsync($"/api/messages/{messageId}")).StatusCode);
        var detail = await a.GetFromJsonAsync<MessageDto>($"/api/messages/{messageId}");
        Assert.DoesNotContain("script", detail!.HtmlBody); Assert.DoesNotContain("onclick", detail.HtmlBody); Assert.DoesNotContain("https:", detail.HtmlBody);
        var attachment = await a.GetAsync($"/api/messages/{messageId}/attachments/{attachmentId}");
        Assert.Equal("secret", await attachment.Content.ReadAsStringAsync()); Assert.Equal("attachment", attachment.Content.Headers.ContentDisposition!.DispositionType); Assert.Equal("nosniff", attachment.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(HttpStatusCode.NoContent, (await a.DeleteAsync($"/api/messages/{messageId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await a.GetAsync($"/api/messages/{messageId}")).StatusCode);
    }
    [Fact] public async Task DuplicateAndReservedMailboxesAreRejected()
    {
        using var a = factory.Browser(); using var b = factory.Browser(); var name = Unique(); await Create(a, name); await Session(b);
        Assert.Equal(HttpStatusCode.Conflict, (await b.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest(name.ToUpperInvariant(), "mail.example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await b.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest("admin", "mail.example.com"))).StatusCode);
    }
    [Fact] public async Task WritesRequireCsrfAndAdminRequiresIdentity()
    {
        using var c = factory.Browser(); await c.GetAsync("/api/session");
        var response = await c.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest(Unique(), "mail.example.com"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.Unauthorized, (await c.GetAsync("/api/admin/dashboard")).StatusCode);
    }
    [Fact] public async Task SessionCookieIsSecureAndHttpOnly()
    {
        using var c = factory.Browser(); var r = await c.GetAsync("/api/session");
        var cookie = r.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("__Host-tm-session="));
        Assert.Contains("secure", cookie); Assert.Contains("httponly", cookie); Assert.Contains("samesite=strict", cookie);
    }
    [Fact] public async Task ExpiredMessagesAndMailboxBecomeUnavailable()
    {
        using var c = factory.Browser(); var box = await Create(c); Guid id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>(); var mailbox = await db.Mailboxes.SingleAsync(x => x.PublicId == box.PublicId);
            var msg = new MailMessage { MailboxId = mailbox.Id, Subject = "expired", ReceivedAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(-1) }; db.Messages.Add(msg); await db.SaveChangesAsync(); id = msg.Id;
        }
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/api/messages/{id}")).StatusCode);
        Assert.Empty((await c.GetFromJsonAsync<MessageSummary[]>("/api/messages"))!);
        await using (var scope = factory.Services.CreateAsyncScope()) { var db = scope.ServiceProvider.GetRequiredService<MailDbContext>(); await db.Mailboxes.Where(x => x.PublicId == box.PublicId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1))); }
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/mailboxes/current")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync("/api/messages")).StatusCode);
    }
    [Fact] public async Task SmtpRcptRejectsRelayUnknownExpiredAndDisabledDomains()
    {
        using var c = factory.Browser(); var box = await Create(c);
        await using var scope = factory.Services.CreateAsyncScope(); var receive = scope.ServiceProvider.GetRequiredService<ReceiveService>();
        Assert.Null(await receive.ValidateRecipientAsync(box.Address, default));
        Assert.Equal("550 Relay denied", await receive.ValidateRecipientAsync("someone@outside.example", default));
        Assert.Equal("550 Mailbox unavailable", await receive.ValidateRecipientAsync("unknown@mail.example.com", default));
        Assert.Equal("550 Mailbox unavailable", await receive.ValidateRecipientAsync("admin@mail.example.com", default));
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>(); var domain = new MailDomain { DomainName = "disabled.example.com", IsActive = false }; db.Domains.Add(domain); await db.SaveChangesAsync();
        Assert.Equal("550 Relay denied", await receive.ValidateRecipientAsync("someone@disabled.example.com", default));
    }
    [Fact] public async Task CleanupDeletesExpiredRowsAndPhysicalOrphans()
    {
        using var c = factory.Browser(); var box = await Create(c); string storageId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>(); var storage = scope.ServiceProvider.GetRequiredService<AttachmentStorage>(); var mailbox = await db.Mailboxes.SingleAsync(x => x.PublicId == box.PublicId);
            storageId = await storage.WriteAsync([1, 2, 3], default); File.SetLastWriteTimeUtc(storage.GetPath(storageId), DateTime.UtcNow.AddHours(-2));
            db.Messages.Add(new MailMessage { MailboxId = mailbox.Id, ReceivedAt = DateTime.UtcNow.AddDays(-2), ExpiresAt = DateTime.UtcNow.AddDays(-1), Attachments = [new MailAttachment { StorageId = storageId, Size = 3 }] }); await db.SaveChangesAsync();
        }
        var cleanup = ActivatorUtilities.CreateInstance<CleanupService>(factory.Services); await cleanup.SweepAsync(default);
        await using var verify = factory.Services.CreateAsyncScope(); var storageCheck = verify.ServiceProvider.GetRequiredService<AttachmentStorage>();
        Assert.False(File.Exists(storageCheck.GetPath(storageId))); Assert.False(await verify.ServiceProvider.GetRequiredService<MailDbContext>().Attachments.AnyAsync(x => x.StorageId == storageId));
    }
    [Fact] public async Task MissingInputReturnsProblemDetailsInsteadOfServerError()
    {
        using var browser = factory.Browser(); await Session(browser);
        var response = await browser.PostAsJsonAsync("/api/mailboxes", new { localPart = "valid-name" });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
    }
    [Fact] public async Task HealthAndBlazorPageRender()
    {
        using var c = factory.Browser(); Assert.Equal(HttpStatusCode.OK, (await c.GetAsync("/health/ready")).StatusCode);
        var page = await c.GetAsync("/"); Assert.Equal(HttpStatusCode.OK, page.StatusCode); Assert.Contains("TEMP MAIL", await page.Content.ReadAsStringAsync()); Assert.DoesNotContain("unsafe-eval", string.Join(";", page.Headers.GetValues("Content-Security-Policy")));
    }
}
