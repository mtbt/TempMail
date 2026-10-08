using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Domain;
using TempMail.Shared;
namespace TempMail.Infrastructure;
public sealed class MailboxService(MailDbContext db, IOptions<TempMailOptions> options, IEmailAddressGenerator generator, TimeProvider clock)
{
    private readonly TempMailOptions o = options.Value;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    public async Task<Mailbox> RequireAsync(string token, CancellationToken ct)
    {
        var hash = Tokens.Hash(token);
        var mailbox = await db.Mailboxes.FirstOrDefaultAsync(x => x.AccessTokenHash == hash && x.ExpiresAt > Now, ct);
        if (mailbox == null || !Tokens.Matches(token, mailbox.AccessTokenHash)) throw new MailPolicyException("Mailbox unavailable.", 404);
        if (mailbox.LastAccessAt < Now.AddMinutes(-5))
        {
            mailbox.LastAccessAt = Now;
            await db.SaveChangesAsync(ct);
        }
        return mailbox;
    }
    public MailboxDto Dto(Mailbox box) => new(box.PublicId, box.NormalizedAddress, box.ExpiresAt, o.AllowMailboxExtension && box.ExpiresAt < box.CreatedAt.AddHours(o.MaxMailboxLifetimeHours));
    public async Task<MailboxDto> CreateAsync(CreateMailboxRequest request, string token, string ipHash, CancellationToken ct)
    {
        var result = await AllocateAsync(request, token, ipHash, false, ct);
        return Dto(result.Box);
    }
    public async Task<EnsureMailboxResponse> EnsureAsync(string? email, string ipHash, CancellationToken ct)
    {
        var address = AddressPolicy.NormalizeAddress(email);
        var at = address.IndexOf('@');
        var result = await AllocateAsync(new CreateMailboxRequest(address[..at], address[(at + 1)..]), Tokens.Create(), ipHash, true, ct);
        return new(result.Box.NormalizedAddress, result.Created);
    }
    private async Task<(Mailbox Box, bool Created)> AllocateAsync(CreateMailboxRequest request, string token, string ipHash, bool ensure, CancellationToken ct)
    {
        var local = AddressPolicy.NormalizeLocal(request.LocalPart ?? "");
        if (local.Length == 0) local = generator.Generate();
        else if (!o.AllowCustomAddress) throw new MailPolicyException("Custom addresses are disabled.");
        if (!AddressPolicy.IsValidLocal(local) || AddressPolicy.IsReserved(local, o.ReservedNames) || await db.BlockRules.AnyAsync(x => (x.Kind == "recipient" || x.Kind == "reserved") && x.Value == local, ct)) throw new MailPolicyException("Address is not allowed.");
        var domainName = request.Domain?.Trim().ToLowerInvariant() ?? "";
        var domain = await db.Domains.FirstOrDefaultAsync(x => x.DomainName == domainName && x.IsActive, ct) ?? throw new MailPolicyException("Domain unavailable.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sp_getapplock @Resource=N'TempMail.MailboxAllocation', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 50001, 'Allocation lock unavailable', 1;", ct);
        var address = local + "@" + domainName;
        // The existing serializable allocation transaction and SQL Server application lock
        // serialize all creators (UI and automation) before the address lookup/insert.
        var existing = await db.Mailboxes.FirstOrDefaultAsync(x => x.NormalizedAddress == address, ct);
        if (ensure && existing != null)
        {
            if (existing.ExpiresAt <= Now) throw new MailPolicyException("Address unavailable.", 409);
            await tx.CommitAsync(ct);
            return (existing, false);
        }
        var hash = Tokens.Hash(token);
        if (await db.Mailboxes.AnyAsync(x => x.AccessTokenHash == hash && x.ExpiresAt > Now, ct)) throw new MailPolicyException("Delete the current mailbox before changing address.", 409);
        if (await db.Mailboxes.CountAsync(x => x.IpHash == ipHash && x.ExpiresAt > Now, ct) >= o.MaxMailboxesPerIp) throw new MailPolicyException("Mailbox limit reached.", 429);
        if (existing != null) throw new MailPolicyException("Address unavailable.", 409);
        var box = new Mailbox { LocalPart = local, DomainId = domain.Id, NormalizedAddress = address, AccessTokenHash = hash, IpHash = ipHash, CreatedAt = Now, LastAccessAt = Now, ExpiresAt = Now.AddHours(o.MailboxLifetimeHours) };
        db.Mailboxes.Add(box);
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 } sql &&
            (!ensure || sql.Message.Contains("IX_Mailboxes_NormalizedAddress", StringComparison.Ordinal)))
        {
            await tx.RollbackAsync(ct);
            await tx.DisposeAsync();
            db.Entry(box).State = EntityState.Detached;
            if (ensure)
            {
                var winner = await db.Mailboxes.AsNoTracking().FirstOrDefaultAsync(x => x.NormalizedAddress == address && x.ExpiresAt > Now, ct);
                if (winner != null) return (winner, false);
            }
            throw new MailPolicyException("Address unavailable.", 409);
        }
        return (box, true);
    }
    public async Task<LatestCodeResponse> LatestCodeAsync(string? email, EmailHtml html, CancellationToken ct)
    {
        var address = AddressPolicy.NormalizeAddress(email);
        var box = await db.Mailboxes.AsNoTracking().Where(x => x.NormalizedAddress == address && x.ExpiresAt > Now).Select(x => new { x.Id }).FirstOrDefaultAsync(ct)
            ?? throw new MailPolicyException("Mailbox unavailable.", 404);
        var latest = await db.Messages.AsNoTracking().Where(x => x.MailboxId == box.Id && x.ExpiresAt > Now)
            .OrderByDescending(x => x.ReceivedAt).ThenByDescending(x => x.Id)
            .Select(x => new { x.Subject, x.TextBody, x.HtmlBody }).FirstOrDefaultAsync(ct);
        return new(latest == null ? null : OtpCode.Find(latest.Subject) ?? OtpCode.Find(latest.TextBody) ?? OtpCode.Find(html.VisibleText(latest.HtmlBody)));
    }
    public async Task<MailboxDto> ExtendAsync(string token, CancellationToken ct)
    {
        var box = await RequireAsync(token, ct);
        if (!o.AllowMailboxExtension) throw new MailPolicyException("Extension is disabled.", 403);
        var max = box.CreatedAt.AddHours(o.MaxMailboxLifetimeHours);
        box.ExpiresAt = new[] { max, new[] { Now.AddHours(o.MailboxLifetimeHours), box.ExpiresAt }.Max() }.Min();
        box.LastAccessAt = Now;
        await db.SaveChangesAsync(ct);
        return Dto(box);
    }
    public async Task DeleteAsync(string token, bool mailbox, CancellationToken ct)
    {
        var box = await RequireAsync(token, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (mailbox) db.Mailboxes.Remove(box);
        else await db.Messages.Where(x => x.MailboxId == box.Id).ExecuteDeleteAsync(ct);
        db.Events.Add(new MailEvent { MailboxPublicId = box.PublicId });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        // Physical files are reclaimed by the retryable orphan sweep after the DB commit.
    }
    public async Task<IQueryable<MailMessage>> MessagesAsync(string token, CancellationToken ct)
    {
        var box = await RequireAsync(token, ct);
        return db.Messages.Where(x => x.MailboxId == box.Id && x.ExpiresAt > Now);
    }
    public async Task<MailMessage> MessageAsync(string token, Guid id, CancellationToken ct) =>
        await (await MessagesAsync(token, ct)).Include(x => x.Attachments).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new MailPolicyException("Message unavailable.", 404);
}
