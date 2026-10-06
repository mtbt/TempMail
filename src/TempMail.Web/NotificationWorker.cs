using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using TempMail.Infrastructure;
namespace TempMail.Web;
public sealed class NotificationWorker(IServiceScopeFactory scopes, IHubContext<MailHub> hub, ILogger<NotificationWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
                    var events = await db.Events.Where(x => x.DispatchedAt == null).OrderBy(x => x.Id).Take(200).ToListAsync(ct);
                    foreach (var e in events)
                    {
                        await hub.Clients.Group("mailbox:" + e.MailboxPublicId).SendAsync("InboxChanged", ct);
                        e.DispatchedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(ct);
                    }
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogError(ex, "Notification poll failed; undispatched events retained for retry"); }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
