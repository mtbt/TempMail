using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using TempMail.Application;
namespace TempMail.Web.Security;
public sealed record SessionPayload(string MailboxSessionId, string MailboxAccessToken, DateTime ExpiresAt);
public sealed class MailboxSession(IDataProtectionProvider provider, IWebHostEnvironment environment)
{
    private readonly IDataProtector protector = provider.CreateProtector("TempMail.MailboxSession.v1");
    private string CookieName => environment.IsDevelopment() ? "tm-session" : "__Host-tm-session";
    public SessionPayload? Read(HttpContext context)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var cookie)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<SessionPayload>(protector.Unprotect(cookie));
            return value is { MailboxAccessToken.Length: 64 } && value.ExpiresAt > DateTime.UtcNow ? value : null;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException) { return null; }
    }
    public SessionPayload Ensure(HttpContext context)
    {
        var existing = Read(context);
        if (existing != null) return existing;
        var value = new SessionPayload(Tokens.Create(), Tokens.Create(), DateTime.UtcNow.AddDays(31));
        context.Response.Cookies.Append(CookieName, protector.Protect(JsonSerializer.Serialize(value)), new CookieOptions { HttpOnly = true, Secure = !environment.IsDevelopment(), SameSite = SameSiteMode.Strict, Path = "/", Expires = value.ExpiresAt, IsEssential = true });
        return value;
    }
    public string Require(HttpContext context) => Read(context)?.MailboxAccessToken ?? throw new MailPolicyException("Mailbox unavailable.", 404);
}
