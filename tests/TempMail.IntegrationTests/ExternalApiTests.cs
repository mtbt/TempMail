using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Domain;
using TempMail.Infrastructure;
using TempMail.Shared;
namespace TempMail.IntegrationTests;
public sealed class ExternalApiTests : IClassFixture<MailFactory>
{
    private readonly MailFactory factory;
    public ExternalApiTests(MailFactory factory) => this.factory = factory;
    private static string Address() => "auto" + Guid.NewGuid().ToString("N")[..12] + "@mail.example.com";
    private HttpClient Client()
    {
        var c = factory.Browser(); c.DefaultRequestHeaders.Add("X-Api-Token", factory.ExternalToken); return c;
    }
    private static Task<HttpResponseMessage> Post(HttpClient c, string? email) => c.PostAsJsonAsync("/api/mailbox", new { email });
    private static Task<HttpResponseMessage> Get(HttpClient c, string email) => c.GetAsync("/api/latest-code?email=" + Uri.EscapeDataString(email));
    [Theory]
    [InlineData(null)] [InlineData("wrong-token")]
    public async Task BothEndpointsRejectMissingOrWrongToken(string? token)
    {
        using var c = factory.Browser(); if (token != null) c.DefaultRequestHeaders.Add("X-Api-Token", token);
        foreach (var response in new[] { await Post(c, Address()), await Get(c, Address()), await c.GetAsync("/api/latest-code?email=abc@mail.example.com&X-Api-Token=" + factory.ExternalToken) })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(factory.ExternalToken!, body); Assert.DoesNotContain("wrong-token", body);
        }
    }
    [Theory] [InlineData(null)] [InlineData("")] [InlineData("   ")]
    public async Task UnconfiguredTokenFailsClosed(string? configured)
    {
        using var f = new MailFactory { ExternalToken = configured }; using var c = f.Browser();
        c.DefaultRequestHeaders.Add("X-Api-Token", "automation-integration-test-only");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(c, Address())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, Address())).StatusCode);
    }
    [Fact] public async Task RequestLoggingDoesNotContainSuppliedOrConfiguredSecrets()
    {
        using var f = new MailFactory { ExternalToken = "test-only-" + Guid.NewGuid().ToString("N") };
        using var c = f.Browser(); var wrong = "wrong-test-only-" + Guid.NewGuid().ToString("N");
        c.DefaultRequestHeaders.Add("X-Api-Token", wrong);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get(c, Address())).StatusCode);
        c.DefaultRequestHeaders.Remove("X-Api-Token"); c.DefaultRequestHeaders.Add("X-Api-Token", f.ExternalToken);
        Assert.Equal(HttpStatusCode.Created, (await Post(c, Address())).StatusCode);
        var files = Directory.GetFiles(Path.Combine(f.Root, "logs"), "*.log"); Assert.NotEmpty(files);
        foreach (var file in files)
        {
            using var stream = File.Open(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream); var logs = await reader.ReadToEndAsync();
            Assert.DoesNotContain(f.ExternalToken!, logs); Assert.DoesNotContain(wrong, logs);
        }
    }
    [Fact] public async Task MultipleTokenHeadersAndMalformedUnauthenticatedBodyAreRejected()
    {
        using var c = Client(); c.DefaultRequestHeaders.Add("X-Api-Token", "another");
        Assert.Equal(HttpStatusCode.Unauthorized, (await Post(c, Address())).StatusCode);
        using var anonymous = factory.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/mailbox", new StringContent("{", Encoding.UTF8, "application/json"))).StatusCode);
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("invalid")] [InlineData("a@b@c.com")]
    [InlineData("ab@mail.example.com")] [InlineData("abc@localhost")] [InlineData("abc @mail.example.com")]
    public async Task InvalidAddressesAreBadRequests(string? email)
    {
        using var c = Client(); Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, email)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Get(c, email ?? "")).StatusCode);
    }
    [Fact] public async Task DomainAndReservedRulesApplyEvenToExistingMailboxes()
    {
        using var c = Client(); var address = Address(); (await Post(c, address)).EnsureSuccessStatusCode();
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        db.Domains.Add(new MailDomain { DomainName = "disabled.example.com", IsActive = false });
        db.BlockRules.Add(new BlockRule { Kind = "recipient", Value = address.Split('@')[0] }); await db.SaveChangesAsync();
        foreach (var email in new[] { "valid@unknown.example", "valid@disabled.example.com", "admin@mail.example.com", address })
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(c, email)).StatusCode);
    }
    [Fact] public async Task EnsureIsCanonicalIdempotentPrivateAndSmtpReady()
    {
        using var c = Client(); var address = Address();
        var created = await Post(c, " " + address.ToUpperInvariant() + " "); Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal(new EnsureMailboxResponse(address, true), await created.Content.ReadFromJsonAsync<EnsureMailboxResponse>());
        var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync()); Assert.Equal(2, json.RootElement.EnumerateObject().Count());
        Assert.False(created.Headers.Contains("Set-Cookie"));
        for (var i = 0; i < 3; i++)
        {
            var existing = await Post(c, address); Assert.Equal(HttpStatusCode.OK, existing.StatusCode);
            Assert.Equal(new EnsureMailboxResponse(address, false), await existing.Content.ReadFromJsonAsync<EnsureMailboxResponse>());
        }
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var box = await db.Mailboxes.SingleAsync(x => x.NormalizedAddress == address);
        var options = scope.ServiceProvider.GetRequiredService<IOptions<TempMailOptions>>().Value;
        Assert.Equal(TimeSpan.FromHours(options.MailboxLifetimeHours).TotalHours, (box.ExpiresAt - box.CreatedAt).TotalHours, 5);
        Assert.Equal(64, box.AccessTokenHash.Length); Assert.Equal(64, box.IpHash.Length);
        Assert.DoesNotContain(box.AccessTokenHash, await created.Content.ReadAsStringAsync());
        Assert.Null(await scope.ServiceProvider.GetRequiredService<ReceiveService>().ValidateRecipientAsync(address, default));
    }
    [Fact] public async Task TenConcurrentEnsuresCreateExactlyOneMailbox()
    {
        using var c = Client(); var address = Address();
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Post(c, address)));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created);
        Assert.Equal(9, responses.Count(x => x.StatusCode == HttpStatusCode.OK));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<MailDbContext>().Mailboxes.CountAsync(x => x.NormalizedAddress == address));
    }
    [Fact] public async Task GetNeverCreatesAndExpiryRequiresCleanupBeforeReuse()
    {
        using var c = Client(); var address = Address();
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, address)).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        Assert.False(await db.Mailboxes.AnyAsync(x => x.NormalizedAddress == address));
        (await Post(c, address)).EnsureSuccessStatusCode();
        Assert.Equal("{\"code\":null}", await (await Get(c, address)).Content.ReadAsStringAsync());
        await db.Mailboxes.Where(x => x.NormalizedAddress == address).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
        Assert.Equal(HttpStatusCode.NotFound, (await Get(c, address)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await Post(c, address)).StatusCode);
        await db.Mailboxes.Where(x => x.NormalizedAddress == address).ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.Created, (await Post(c, address)).StatusCode);
    }
    [Theory]
    [InlineData("Your verification code is 123456", "654321", "<p>112233</p>", "123456")]
    [InlineData("OTP: 012345", "", "", "012345")]
    [InlineData("１２３４５６", "Code: 123456", "<p>112233</p>", "123456")]
    [InlineData("١٢٣٤٥٦", "Code: 012345", "<p>112233</p>", "012345")]
    [InlineData("None", "Your OTP is 654321", "<p>112233</p>", "654321")]
    [InlineData("None", "None", "<p>Your code is <strong>112233</strong></p>", "112233")]
    [InlineData("1234567", "99123456", "<p>123456789</p>", null)]
    [InlineData("111111 and 222222", "", "", "111111")]
    [InlineData("None", "None", "<script>123456</script><p title='654321'>None</p>", null)]
    public async Task LatestCodeUsesSectionPriorityAndOnlyReturnsCode(string subject, string text, string html, string? expected)
    {
        using var c = Client(); var address = Address(); (await Post(c, address)).EnsureSuccessStatusCode();
        await AddMessages(address, new MailMessage { Subject = subject, TextBody = text, HtmlBody = html });
        var response = await Get(c, address); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new LatestCodeResponse(expected), await response.Content.ReadFromJsonAsync<LatestCodeResponse>());
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); Assert.Single(json.RootElement.EnumerateObject());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }
    [Theory] [InlineData("No code", null)] [InlineData("222222", "222222")]
    public async Task NeverFallsBackToAnOlderMessage(string latest, string? expected)
    {
        using var c = Client(); var address = Address(); (await Post(c, address)).EnsureSuccessStatusCode();
        await AddMessages(address, new MailMessage { Subject = "111111", ReceivedAt = DateTime.UtcNow.AddMinutes(-2) }, new MailMessage { Subject = latest });
        Assert.Equal(new LatestCodeResponse(expected), await (await Get(c, address)).Content.ReadFromJsonAsync<LatestCodeResponse>());
    }
    [Fact] public async Task TiedReceivedAtUsesDescendingId()
    {
        using var c = Client(); var address = Address(); (await Post(c, address)).EnsureSuccessStatusCode(); var now = DateTime.UtcNow;
        await AddMessages(address,
            new MailMessage { Id = Guid.Parse("00000000-0000-0000-0000-000000000001"), Subject = "111111", ReceivedAt = now },
            new MailMessage { Id = Guid.Parse("00000000-0000-0000-0000-000000000002"), Subject = "222222", ReceivedAt = now });
        Assert.Equal(new LatestCodeResponse("222222"), await (await Get(c, address)).Content.ReadFromJsonAsync<LatestCodeResponse>());
    }
    [Fact] public async Task QuotaAndCustomAddressPolicyRemainEnforced()
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var options = new TempMailOptions { MaxMailboxesPerIp = 1, MailboxLifetimeHours = 3 };
        var service = new MailboxService(db, Options.Create(options), new EmailAddressGenerator(), TimeProvider.System);
        var ip = Tokens.Hash(Guid.NewGuid().ToString()); var address = Address();
        Assert.True((await service.EnsureAsync(address, ip, default)).Created);
        Assert.False((await service.EnsureAsync(address, ip, default)).Created);
        var box = await db.Mailboxes.SingleAsync(x => x.NormalizedAddress == address);
        Assert.Equal(3, (box.ExpiresAt - box.CreatedAt).TotalHours, 5);
        Assert.Equal(429, (await Assert.ThrowsAsync<MailPolicyException>(() => service.EnsureAsync(Address(), ip, default))).StatusCode);
        options.AllowCustomAddress = false;
        Assert.Equal(400, (await Assert.ThrowsAsync<MailPolicyException>(() => service.EnsureAsync(Address(), Tokens.Hash("other-ip"), default))).StatusCode);
    }
    [Fact] public async Task AutomationRateLimitsAreIndependentAndNeverAffectSmtpValidation()
    {
        using var f = new MailFactory { CreateLimit = 1, ReadLimit = 10 }; using var c = f.Browser();
        c.DefaultRequestHeaders.Add("X-Api-Token", f.ExternalToken); var address = Address();
        Assert.Equal(HttpStatusCode.Created, (await Post(c, address)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Post(c, address)).StatusCode);
        for (var i = 0; i < 10; i++) Assert.Equal(HttpStatusCode.OK, (await Get(c, address)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await Get(c, address)).StatusCode);
        await using var scope = f.Services.CreateAsyncScope();
        Assert.Null(await scope.ServiceProvider.GetRequiredService<ReceiveService>().ValidateRecipientAsync(address, default));
    }
    [Theory]
    [InlineData("<p hidden>654321</p><p>Code: 112233</p>", null, "112233")]
    [InlineData("<div aria-hidden='true'>654321</div><p>112233</p>", null, "112233")]
    [InlineData("<p hidden>654321</p>", null, null)]
    [InlineData("<div hidden><span>654321</span></div><p>112233</p>", null, "112233")]
    [InlineData("<div aria-hidden='true'><p><strong>654321</strong></p></div><p>112233</p>", null, "112233")]
    [InlineData("<div ARIA-HIDDEN=' True \t'><span>654321</span></div><p>112233</p>", null, "112233")]
    [InlineData("<p hidden='false'>654321</p><p>112233</p>", null, "112233")]
    [InlineData("<script>654321</script><style>.x{width:654321px}</style><!--654321--><p data-code='654321' title='654321'>None</p><img src='https://example.com/654321'>", null, null)]
    [InlineData("<p>Your code is <strong>112233</strong></p>", null, "112233")]
    [InlineData("<p hidden>654321</p><p>112233</p>", "Code: 012345", "012345")]
    [InlineData("<div aria-hidden='false'><strong>112233</strong></div>", null, "112233")]
    [InlineData("<body hidden><p>654321</p></body>", null, null)]
    public async Task ReceivedMimeRemovesHiddenSubtreesBeforePersistenceAndCodeRead(string html, string? text, string? expected)
    {
        using var c = Client(); var address = Address(); (await Post(c, address)).EnsureSuccessStatusCode();
        using var mime = new MimeKit.MimeMessage();
        mime.From.Add(MimeKit.MailboxAddress.Parse("sender@example.com"));
        mime.To.Add(MimeKit.MailboxAddress.Parse(address));
        mime.Subject = "Verification";
        mime.Body = new MimeKit.BodyBuilder { HtmlBody = html, TextBody = text }.ToMessageBody();
        using var raw = new MemoryStream(); await mime.WriteToAsync(raw);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ReceiveService>().ReceiveAsync("sender@example.com", [address], raw.ToArray(), default);
            var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
            var stored = await db.Messages.AsNoTracking().SingleAsync(x => x.Mailbox.NormalizedAddress == address);
            // The fix must remove hidden descendants during ingestion, not just at GET.
            if (html.Contains("hidden", StringComparison.OrdinalIgnoreCase)) Assert.DoesNotContain("654321", stored.HtmlBody);
            if (html.Contains("112233", StringComparison.Ordinal)) Assert.Contains("112233", stored.HtmlBody);
        }
        var response = await Get(c, address); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new LatestCodeResponse(expected), await response.Content.ReadFromJsonAsync<LatestCodeResponse>());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
    }
    private async Task AddMessages(string address, params MailMessage[] messages)
    {
        await using var scope = factory.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var box = await db.Mailboxes.SingleAsync(x => x.NormalizedAddress == address);
        foreach (var message in messages) { message.MailboxId = box.Id; if (message.ReceivedAt == default) message.ReceivedAt = DateTime.UtcNow; message.ExpiresAt = DateTime.UtcNow.AddHours(1); }
        db.Messages.AddRange(messages); await db.SaveChangesAsync();
    }
}
