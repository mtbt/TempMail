using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Serilog;
using TempMail.Application;
using TempMail.Infrastructure;
using TempMail.Web;
using TempMail.Web.Components;
using TempMail.Web.Security;
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = 65536; o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15); });
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true).AddEnvironmentVariables().AddCommandLine(args);
builder.Services.AddSerilog((services, logger) => logger.MinimumLevel.Information().MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning).WriteTo.Console().WriteTo.File(builder.Configuration["Logging:FilePath"] ?? "logs/web-.log", rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14, fileSizeLimitBytes: 50_000_000, rollOnFileSizeLimit: true));
builder.Services.AddMailInfrastructure(builder.Configuration);
builder.Services.AddOptions<SecurityOptions>().Bind(builder.Configuration.GetSection("Security")).ValidateDataAnnotations().ValidateOnStart();
var settings = builder.Configuration.GetSection("TempMail").Get<TempMailOptions>() ?? new();
var staticRoot = Path.GetFullPath(builder.Environment.WebRootPath ?? Path.Combine(builder.Environment.ContentRootPath, "wwwroot")) + Path.DirectorySeparatorChar;
foreach (var privatePath in new[] { settings.StoragePath, settings.DataProtectionPath })
    if (Path.IsPathFullyQualified(privatePath) && (Path.GetFullPath(privatePath) + Path.DirectorySeparatorChar).StartsWith(staticRoot, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Private storage and Data Protection keys must be outside wwwroot.");
var protection = builder.Services.AddDataProtection().SetApplicationName("TempMail.Web.v1");
if (!string.IsNullOrWhiteSpace(settings.DataProtectionPath)) protection.PersistKeysToFileSystem(new DirectoryInfo(settings.DataProtectionPath));
if (!string.IsNullOrWhiteSpace(settings.DataProtectionCertificateThumbprint)) protection.ProtectKeysWithCertificate(settings.DataProtectionCertificateThumbprint);
else if (OperatingSystem.IsWindows() && !builder.Environment.IsDevelopment()) protection.ProtectKeysWithDpapi();
builder.Services.AddIdentity<IdentityUser, IdentityRole>(o => { o.Password.RequiredLength = 14; o.Password.RequireNonAlphanumeric = true; o.Lockout.MaxFailedAccessAttempts = 5; o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15); o.User.RequireUniqueEmail = true; }).AddEntityFrameworkStores<MailDbContext>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(o => { o.Cookie.Name = "__Host-TempMail.Admin"; o.Cookie.SecurePolicy = CookieSecurePolicy.Always; o.Cookie.SameSite = SameSiteMode.Strict; o.ExpireTimeSpan = TimeSpan.FromMinutes(30); o.SlidingExpiration = false; o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; }; o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; }; });
builder.Services.AddAuthorization(o => o.AddPolicy("Admin", p => p.RequireRole("Admin")));
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always; o.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.AddSingleton<MailboxSession>();
builder.Services.AddProblemDetails();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSignalR(o => { o.MaximumReceiveMessageSize = 4096; o.EnableDetailedErrors = false; });
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    o.OnRejected = async (c, ct) => await Results.Problem(statusCode: 429, title: "Too many requests.").ExecuteAsync(c.HttpContext);
    foreach (var pair in new[] { ("create", settings.CreateRequestsPerMinute), ("delete", settings.DeleteRequestsPerMinute), ("read", settings.ReadRequestsPerMinute), ("login", 5) })
        o.AddPolicy(pair.Item1, c => RateLimitPartition.GetFixedWindowLimiter(c.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions { PermitLimit = pair.Item2, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
});
builder.Services.AddHealthChecks().AddCheck<ReadinessCheck>("database-storage");
builder.Services.AddHostedService<NotificationWorker>();
builder.Services.AddHostedService<CleanupService>();
var app = builder.Build();
if (args.Contains("--seed") || args.Contains("--bootstrap-admin")) { await Bootstrap.RunAsync(app.Services, app.Configuration, args.Contains("--bootstrap-admin"), CancellationToken.None); return; }
app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    context.Response.Headers.XFrameOptions = "SAMEORIGIN";
    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; frame-src 'self'; frame-ancestors 'self'; base-uri 'self'; object-src 'none'; form-action 'self'";
    if (context.Request.Path.StartsWithSegments("/api") || context.Request.Path.StartsWithSegments("/admin")) context.Response.Headers.CacheControl = "no-store";
    await next();
});
app.UseSerilogRequestLogging();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (MailPolicyException ex) { await Results.Problem(statusCode: ex.StatusCode, title: ex.Message).ExecuteAsync(context); }
    catch (AntiforgeryValidationException) { await Results.Problem(statusCode: 400, title: "Invalid anti-forgery token.").ExecuteAsync(context); }
});
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) { app.UseHsts(); app.UseHttpsRedirection(); }
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    if ((context.Request.Path.StartsWithSegments("/mailhub") || context.Request.Path.StartsWithSegments("/_blazor")) && context.Request.Headers.Origin.Count > 0)
    {
        var expected = $"{context.Request.Scheme}://{context.Request.Host}";
        if (!string.Equals(context.Request.Headers.Origin.ToString(), expected, StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = 403; return; }
    }
    await next();
});
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api") && HttpMethods.IsGet(context.Request.Method) == false && HttpMethods.IsHead(context.Request.Method) == false && HttpMethods.IsOptions(context.Request.Method) == false)
        await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context);
    await next();
});
app.MapMailApi();
app.MapHub<MailHub>("/mailhub").RequireRateLimiting("read");
app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }));
app.MapHealthChecks("/health").RequireRateLimiting("read");
app.MapHealthChecks("/health/ready").RequireRateLimiting("read");
app.MapGet("/api/admin/smtp-health", async (MailDbContext db, CancellationToken ct) => await db.SmtpStatuses.AnyAsync(x => x.LastHeartbeatAt > DateTime.UtcNow.AddSeconds(-30), ct) ? Results.Ok() : Results.StatusCode(503)).RequireAuthorization("Admin");
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();
app.Run();
public partial class Program { }
public sealed class ReadinessCheck(MailDbContext db, AttachmentStorage storage) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try { await db.Domains.AsNoTracking().OrderBy(x => x.Id).Take(1).ToArrayAsync(cancellationToken); await storage.ProbeAsync(cancellationToken); return HealthCheckResult.Healthy(); }
        catch (Exception) { return HealthCheckResult.Unhealthy("Database or storage unavailable."); }
    }
}
