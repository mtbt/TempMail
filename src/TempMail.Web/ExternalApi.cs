using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Infrastructure;
using TempMail.Shared;
using TempMail.Web.Security;
namespace TempMail.Web;
public static class ExternalApi
{
    public static void MapExternalApi(this WebApplication app)
    {
        app.MapPost("/api/mailbox", async (EnsureMailboxRequest request, HttpContext context, MailboxService service, IOptions<SecurityOptions> security, CancellationToken ct) =>
        {
            var ipHash = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(security.Value.IpHashKey),
                Encoding.UTF8.GetBytes(context.Connection.RemoteIpAddress?.ToString() ?? "unknown")));
            var result = await service.EnsureAsync(request.Email, ipHash, ct);
            return Results.Json(result, statusCode: result.Created ? 201 : 200);
        }).WithMetadata(new ExternalApiAttribute()).AllowAnonymous().RequireRateLimiting("automation-create");
        app.MapGet("/api/latest-code", async (string? email, MailboxService service, EmailHtml html, CancellationToken ct) =>
            await service.LatestCodeAsync(email, html, ct))
            .WithMetadata(new ExternalApiAttribute()).AllowAnonymous().RequireRateLimiting("automation-read");
    }
}
