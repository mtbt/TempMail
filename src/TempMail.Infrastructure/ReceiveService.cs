using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Domain;
namespace TempMail.Infrastructure;
public sealed class ReceiveService(MailDbContext db, IOptions<TempMailOptions> mailOptions, IOptions<SmtpOptions> smtpOptions, MimeParser parser, AttachmentStorage storage, TimeProvider clock)
{
    public async Task<string?> ValidateRecipientAsync(string address, CancellationToken ct)
    {
        var at = address.LastIndexOf('@');
        if (at < 1 || !AddressPolicy.IsValidLocal(address[..at])) return "550 Mailbox unavailable";
        var domain = address[(at + 1)..];
        if (!await db.Domains.AnyAsync(x => x.DomainName == domain && x.IsActive, ct)) return "550 Relay denied";
        var local = address[..at];
        if (AddressPolicy.IsReserved(local, mailOptions.Value.ReservedNames) || await db.BlockRules.AnyAsync(x => (x.Kind == "recipient" || x.Kind == "reserved") && x.Value == local, ct)) return "550 Mailbox unavailable";
        if (smtpOptions.Value.RejectUnknownMailbox && !await db.Mailboxes.AnyAsync(x => x.NormalizedAddress == address && x.ExpiresAt > clock.GetUtcNow().UtcDateTime, ct)) return "550 Mailbox unavailable";
        return null;
    }
    public async Task<bool> SenderBlockedAsync(string address, CancellationToken ct)
    {
        var domain = address.Contains('@') ? address[(address.LastIndexOf('@') + 1)..] : "";
        return await db.BlockRules.AnyAsync(x => (x.Kind == "sender" && x.Value == address) || (x.Kind == "sender-domain" && x.Value == domain), ct);
    }
    public async Task ReceiveAsync(string envelopeFrom, IReadOnlyList<string> recipients, byte[] raw, CancellationToken ct)
    {
        if (recipients.Count == 0 || recipients.Count > smtpOptions.Value.MaxRecipients) throw new MailPolicyException("Invalid recipient count.");
        var parsed = await parser.ParseAsync(raw, ct);
        if (await SenderBlockedAsync(envelopeFrom, ct) || await SenderBlockedAsync(parsed.From.ToLowerInvariant(), ct)) throw new MailPolicyException("Sender rejected.");
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        if (db.Database.IsSqlServer())
            await db.Database.ExecuteSqlRawAsync("DECLARE @result int; EXEC @result = sp_getapplock @Resource=N'TempMail.MessageAllocation', @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=10000; IF @result < 0 THROW 50001, 'Allocation lock unavailable', 1;", ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var boxes = new List<Mailbox>();
        foreach (var recipient in recipients.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var error = await ValidateRecipientAsync(recipient, ct);
            if (error != null) throw new MailPolicyException(error);
            var box = await db.Mailboxes.FirstOrDefaultAsync(x => x.NormalizedAddress == recipient && x.ExpiresAt > now, ct);
            if (box == null) continue; // Configured accept-and-discard, never relay or auto-create an unowned mailbox.
            if (await db.Messages.CountAsync(x => x.MailboxId == box.Id, ct) >= mailOptions.Value.MaxMessagesPerMailbox) throw new MailPolicyException("Mailbox quota exceeded.");
            boxes.Add(box);
        }
        var used = await db.Messages.SumAsync(x => (long?)x.Size, ct) ?? 0;
        if (used + (long)raw.Length * boxes.Count > mailOptions.Value.MaxStorageMB * 1024 * 1024) throw new MailPolicyException("Storage quota exceeded.", 507);
        foreach (var box in boxes)
        {
            var msg = new MailMessage { MailboxId = box.Id, InternetMessageId = parsed.MessageId, FromAddress = parsed.From, FromName = parsed.FromName, To = parsed.To, Cc = parsed.Cc, Subject = parsed.Subject, TextBody = parsed.Text, HtmlBody = parsed.Html, RawHeaders = parsed.Headers, Size = raw.Length, ReceivedAt = now, ExpiresAt = now.AddHours(mailOptions.Value.MessageLifetimeHours) };
            foreach (var part in parsed.Attachments)
            {
                var storageId = await storage.WriteAsync(part.Bytes, ct);
                msg.Attachments.Add(new MailAttachment { StorageId = storageId, FileName = part.FileName, ContentType = part.ContentType, ContentId = part.ContentId, Size = part.Bytes.Length, CreatedAt = now });
            }
            db.Messages.Add(msg);
            db.Events.Add(new MailEvent { MailboxPublicId = box.PublicId, CreatedAt = now });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }
}
