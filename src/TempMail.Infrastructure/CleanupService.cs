using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TempMail.Application;
namespace TempMail.Infrastructure;
public sealed class CleanupService(IServiceScopeFactory scopes, IOptions<TempMailOptions> options, TimeProvider clock, ILogger<CleanupService> log) : BackgroundService
{
    public async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<AttachmentStorage>();
        var now = clock.GetUtcNow().UtcDateTime;
        await db.Messages.Where(x => x.ExpiresAt <= now || x.Mailbox.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        await db.Mailboxes.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        await db.Events.Where(x => x.CreatedAt < now.AddDays(-1)).ExecuteDeleteAsync(ct);
        // One-hour grace prevents deleting files written by an in-flight SMTP transaction.
        foreach (var path in Directory.EnumerateFiles(storage.Root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(path);
            if (id.Length != 32 || !id.All(char.IsAsciiHexDigit) || File.GetLastWriteTimeUtc(path) > now.AddHours(-1)) continue;
            if (!await db.Attachments.AnyAsync(x => x.StorageId == id, ct)) storage.Delete(id);
        }
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.CleanupIntervalSeconds));
        do
        {
            try { await SweepAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { log.LogError(ex, "Cleanup failed; retrying next interval"); }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
