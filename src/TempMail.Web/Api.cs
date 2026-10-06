using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Domain;
using TempMail.Infrastructure;
using TempMail.Shared;
using TempMail.Web.Security;
namespace TempMail.Web;
public static class Api
{
    public static void MapMailApi(this WebApplication app)
    {
        app.MapGet("/api/session", (HttpContext c, MailboxSession sessions, IAntiforgery csrf) => { sessions.Ensure(c); return new SessionDto(csrf.GetAndStoreTokens(c).RequestToken!); }).RequireRateLimiting("read");
        app.MapGet("/api/domains", async (MailDbContext db, CancellationToken ct) => await db.Domains.Where(x => x.IsActive).OrderBy(x => x.DomainName).Select(x => x.DomainName).ToArrayAsync(ct)).RequireRateLimiting("read");
        app.MapPost("/api/mailboxes", async (CreateMailboxRequest request, HttpContext c, MailboxSession session, MailboxService service, IConfiguration config, CancellationToken ct) =>
        {
            // HMAC prevents cheap reversal of stored IPv4 hashes; key is supplied securely at deployment.
            var key = config["Security:IpHashKey"] ?? throw new InvalidOperationException("Security:IpHashKey is required.");
            if (key.Length < 32) throw new InvalidOperationException("Security:IpHashKey must contain at least 32 characters.");
            var ipHash = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(c.Connection.RemoteIpAddress?.ToString() ?? "unknown")));
            return Results.Ok(await service.CreateAsync(request, session.Require(c), ipHash, ct));
        }).RequireRateLimiting("create");
        app.MapGet("/api/mailboxes/current", async (HttpContext c, MailboxSession s, MailboxService m, CancellationToken ct) => m.Dto(await m.RequireAsync(s.Require(c), ct))).RequireRateLimiting("read");
        app.MapDelete("/api/mailboxes/current", async (HttpContext c, MailboxSession s, MailboxService m, CancellationToken ct) => { await m.DeleteAsync(s.Require(c), true, ct); return Results.NoContent(); }).RequireRateLimiting("delete");
        app.MapDelete("/api/mailboxes/current/messages", async (HttpContext c, MailboxSession s, MailboxService m, CancellationToken ct) => { await m.DeleteAsync(s.Require(c), false, ct); return Results.NoContent(); }).RequireRateLimiting("delete");
        app.MapPost("/api/mailboxes/current/extend", async (HttpContext c, MailboxSession s, MailboxService m, CancellationToken ct) => await m.ExtendAsync(s.Require(c), ct)).RequireRateLimiting("create");
        app.MapGet("/api/messages", async (HttpContext c, MailboxSession s, MailboxService m, CancellationToken ct) =>
            await (await m.MessagesAsync(s.Require(c), ct)).OrderByDescending(x => x.ReceivedAt).Select(x => new MessageSummary(x.Id, x.FromAddress, x.FromName, x.Subject, x.ReceivedAt, x.Size, x.Attachments.Any())).Take(1000).ToArrayAsync(ct)).RequireRateLimiting("read");
        app.MapGet("/api/messages/{id:guid}", async (Guid id, HttpContext c, MailboxSession s, MailboxService m, EmailHtml html, CancellationToken ct) =>
        {
            var message = await m.MessageAsync(s.Require(c), id, ct);
            return new MessageDto(message.Id, message.FromAddress, message.FromName, message.To, message.Cc, message.Subject, message.TextBody, html.Sanitize(message.HtmlBody), message.ReceivedAt, message.Attachments.Select(x => new AttachmentDto(x.Id, x.FileName, x.ContentType, x.Size, x.ContentId)).ToArray());
        }).RequireRateLimiting("read");
        app.MapGet("/api/messages/{id:guid}/html", async (Guid id, bool? images, HttpContext c, MailboxSession s, MailboxService m, EmailRenderer renderer, CancellationToken ct) =>
        {
            var message = await m.MessageAsync(s.Require(c), id, ct);
            c.Response.Headers.ContentSecurityPolicy = "default-src 'none'; img-src " + (images == true ? "data: https:" : "data:") + "; frame-ancestors 'self'; sandbox";
            return Results.Content("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"referrer\" content=\"no-referrer\"></head><body>" + await renderer.RenderAsync(message, images == true, ct) + "</body></html>", "text/html", Encoding.UTF8);
        }).RequireRateLimiting("read");
        app.MapDelete("/api/messages/{id:guid}", async (Guid id, HttpContext c, MailboxSession s, MailboxService m, MailDbContext db, CancellationToken ct) =>
        {
            var message = await m.MessageAsync(s.Require(c), id, ct);
            var box = await m.RequireAsync(s.Require(c), ct);
            db.Messages.Remove(message); db.Events.Add(new MailEvent { MailboxPublicId = box.PublicId }); await db.SaveChangesAsync(ct); return Results.NoContent();
        }).RequireRateLimiting("delete");
        app.MapGet("/api/messages/{id:guid}/attachments/{attachmentId:guid}", async (Guid id, Guid attachmentId, HttpContext c, MailboxSession s, MailboxService m, AttachmentStorage storage, CancellationToken ct) =>
        {
            var message = await m.MessageAsync(s.Require(c), id, ct);
            var a = message.Attachments.SingleOrDefault(x => x.Id == attachmentId) ?? throw new MailPolicyException("Attachment unavailable.", 404);
            try { return Results.File(await storage.OpenAsync(a.StorageId), "application/octet-stream", a.FileName, enableRangeProcessing: false); }
            catch (FileNotFoundException) { throw new MailPolicyException("Attachment unavailable.", 404); }
        }).RequireRateLimiting("read");
        app.MapPost("/api/admin/login", async (LoginRequest request, SignInManager<IdentityUser> signIn) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password) || request.Email.Length > 256 || request.Password.Length > 1024)
                return Results.Problem(statusCode: 400, title: "Invalid login request.");
            var result = await signIn.PasswordSignInAsync(request.Email.Trim(), request.Password, false, lockoutOnFailure: true);
            return result.Succeeded ? Results.NoContent() : Results.Problem(statusCode: 401, title: "Sign-in failed.");
        }).RequireRateLimiting("login");
        var admin = app.MapGroup("/api/admin").RequireAuthorization("Admin").RequireRateLimiting("read");
        admin.MapPost("/logout", async (SignInManager<IdentityUser> signIn) => { await signIn.SignOutAsync(); return Results.NoContent(); });
        admin.MapGet("/dashboard", async (MailDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow; var since = now.Date;
            var status = await db.SmtpStatuses.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, ct);
            var daily = await db.Messages.Where(x => x.ReceivedAt >= now.AddDays(-7)).GroupBy(x => x.ReceivedAt.Date).Select(g => new { Day = g.Key, Count = g.Count() }).ToArrayAsync(ct);
            var hourly = await db.Messages.Where(x => x.ReceivedAt >= since).GroupBy(x => x.ReceivedAt.Hour).Select(g => new { Hour = g.Key, Count = g.Count() }).ToArrayAsync(ct);
            return Results.Ok(new { ActiveMailboxes = await db.Mailboxes.CountAsync(x => x.ExpiresAt > now, ct), MessagesToday = await db.Messages.CountAsync(x => x.ReceivedAt >= since, ct), Messages = await db.Messages.CountAsync(ct), StorageBytes = await db.Attachments.SumAsync(x => (long?)x.Size, ct) ?? 0, SmtpConnections = status?.Connections ?? 0, SmtpHealthy = status?.LastHeartbeatAt > now.AddSeconds(-30), Daily = daily, Hourly = hourly });
        });
        admin.MapGet("/domains", async (MailDbContext db, CancellationToken ct) => await db.Domains.AsNoTracking().ToArrayAsync(ct));
        admin.MapPost("/domains", async (DomainRequest request, MailDbContext db, CancellationToken ct) =>
        {
            var name = request.DomainName?.Trim().ToLowerInvariant() ?? "";
            if (!AddressPolicy.IsValidDomain(name)) throw new MailPolicyException("Invalid domain.");
            if (await db.Domains.AnyAsync(x => x.DomainName == name, ct)) throw new MailPolicyException("Domain exists.", 409);
            db.Domains.Add(new MailDomain { DomainName = name, IsActive = request.IsActive }); await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        admin.MapPut("/domains/{id:guid}", async (Guid id, DomainRequest request, MailDbContext db, CancellationToken ct) =>
        {
            var domain = await db.Domains.FindAsync([id], ct) ?? throw new MailPolicyException("Domain unavailable.", 404);
            domain.IsActive = request.IsActive; await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        admin.MapDelete("/domains/{id:guid}", async (Guid id, MailDbContext db, CancellationToken ct) =>
        {
            if (await db.Mailboxes.AnyAsync(x => x.DomainId == id, ct)) throw new MailPolicyException("Disable this domain until its mailboxes expire.", 409);
            await db.Domains.Where(x => x.Id == id).ExecuteDeleteAsync(ct); return Results.NoContent();
        });
        admin.MapGet("/rules", async (MailDbContext db, IOptions<TempMailOptions> o, CancellationToken ct) => Results.Ok(new { Rules = await db.BlockRules.ToArrayAsync(ct), ConfiguredReservedNames = o.Value.ReservedNames }));
        admin.MapPost("/rules", async (RuleRequest request, MailDbContext db, CancellationToken ct) =>
        {
            var value = request.Value?.Trim().ToLowerInvariant() ?? "";
            if (request.Kind is not ("sender" or "sender-domain" or "recipient" or "reserved") || value.Length is < 1 or > 320 || value.Any(char.IsControl)) throw new MailPolicyException("Invalid rule.");
            if (!await db.BlockRules.AnyAsync(x => x.Kind == request.Kind && x.Value == value, ct)) { db.BlockRules.Add(new BlockRule { Kind = request.Kind, Value = value }); await db.SaveChangesAsync(ct); }
            return Results.NoContent();
        });
        admin.MapDelete("/rules/{id:int}", async (int id, MailDbContext db, CancellationToken ct) => { await db.BlockRules.Where(x => x.Id == id).ExecuteDeleteAsync(ct); return Results.NoContent(); });
    }
}
