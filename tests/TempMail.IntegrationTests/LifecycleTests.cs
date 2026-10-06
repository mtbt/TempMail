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
public sealed class LifecycleTests
{
    [Fact] public async Task InlineCidRendersOnlyOwnedRasterAndBlocksSvgAndRemoteImages()
    {
        using var factory = new MailFactory(); using var browser = factory.Browser(); await ApiTests.Session(browser);
        var create = await browser.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest("inline-test", "mail.example.com")); create.EnsureSuccessStatusCode();
        Guid id;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var receiver = scope.ServiceProvider.GetRequiredService<ReceiveService>();
            var raw = "From: sender@example.com\r\nTo: inline-test@mail.example.com\r\nSubject: inline\r\nMIME-Version: 1.0\r\nContent-Type: multipart/related; boundary=foo\r\n\r\n--foo\r\nContent-Type: text/html\r\n\r\n<p>inline</p><img src='cid:logo'><img src='cid:unsafe'><img src='https://track.example/pixel'>\r\n--foo\r\nContent-Type: image/png\r\nContent-ID: <logo>\r\nContent-Transfer-Encoding: base64\r\n\r\niVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6agAAAABJRU5ErkJggg==\r\n--foo\r\nContent-Type: image/svg+xml\r\nContent-ID: <unsafe>\r\n\r\n<svg onload='evil()'></svg>\r\n--foo--\r\n";
            await receiver.ReceiveAsync("sender@example.com", ["inline-test@mail.example.com"], Encoding.UTF8.GetBytes(raw), default);
            id = await scope.ServiceProvider.GetRequiredService<MailDbContext>().Messages.Select(x => x.Id).SingleAsync();
        }
        var html = await browser.GetStringAsync($"/api/messages/{id}/html"); Assert.Contains("data:image/png;base64,", html); Assert.DoesNotContain("image/svg", html); Assert.DoesNotContain("https:", html); Assert.DoesNotContain("evil", html);
        var optedIn = await browser.GetStringAsync($"/api/messages/{id}/html?images=true"); Assert.Contains("https://track.example/pixel", optedIn);
    }
    [Fact] public async Task MailboxExtensionIsCappedAndUnknownAcceptDoesNotCreateMailbox()
    {
        using var factory = new MailFactory(); using var browser = factory.Browser(); await ApiTests.Session(browser);
        (await browser.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest("extend-test", "mail.example.com"))).EnsureSuccessStatusCode();
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var box = await db.Mailboxes.SingleAsync(); box.CreatedAt = DateTime.UtcNow.AddHours(-71); box.ExpiresAt = DateTime.UtcNow.AddMinutes(5); await db.SaveChangesAsync();
        var response = await browser.PostAsJsonAsync("/api/mailboxes/current/extend", new { }); response.EnsureSuccessStatusCode(); var extended = await response.Content.ReadFromJsonAsync<MailboxDto>();
        Assert.True(extended!.ExpiresAt <= box.CreatedAt.AddHours(72)); Assert.False(extended.CanExtend);
        var receiver = new ReceiveService(db, scope.ServiceProvider.GetRequiredService<IOptions<TempMailOptions>>(), Options.Create(new SmtpOptions { RejectUnknownMailbox = false }), scope.ServiceProvider.GetRequiredService<MimeParser>(), scope.ServiceProvider.GetRequiredService<AttachmentStorage>(), TimeProvider.System);
        Assert.Null(await receiver.ValidateRecipientAsync("unknown@mail.example.com", default));
        await receiver.ReceiveAsync("sender@example.com", ["unknown@mail.example.com"], Encoding.ASCII.GetBytes("From: sender@example.com\r\nSubject: discard\r\n\r\nHello"), default);
        Assert.Equal(1, await db.Mailboxes.CountAsync()); Assert.Empty(await db.Messages.ToArrayAsync());
    }
    [Fact] public async Task MessageQuotaAndSenderBlacklistRejectWithoutPartialDelivery()
    {
        using var factory = new MailFactory(); using var browser = factory.Browser(); await ApiTests.Session(browser);
        (await browser.PostAsJsonAsync("/api/mailboxes", new CreateMailboxRequest("quota-test", "mail.example.com"))).EnsureSuccessStatusCode();
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var receiver = new ReceiveService(db, Options.Create(new TempMailOptions { MaxMessagesPerMailbox = 1 }), Options.Create(new SmtpOptions()), scope.ServiceProvider.GetRequiredService<MimeParser>(), scope.ServiceProvider.GetRequiredService<AttachmentStorage>(), TimeProvider.System);
        byte[] raw = Encoding.ASCII.GetBytes("From: sender@example.com\r\nSubject: quota\r\n\r\nHello");
        await receiver.ReceiveAsync("sender@example.com", ["quota-test@mail.example.com"], raw, default);
        await Assert.ThrowsAsync<MailPolicyException>(() => receiver.ReceiveAsync("sender@example.com", ["quota-test@mail.example.com"], raw, default));
        Assert.Equal(1, await db.Messages.CountAsync());
        db.BlockRules.Add(new BlockRule { Kind = "sender-domain", Value = "blocked.example" }); await db.SaveChangesAsync();
        Assert.True(await receiver.SenderBlockedAsync("person@blocked.example", default));
    }
}
