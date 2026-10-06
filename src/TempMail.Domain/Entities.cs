namespace TempMail.Domain;

public sealed class MailDomain
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string DomainName { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public sealed class Mailbox
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public string LocalPart { get; set; } = "";
    public Guid DomainId { get; set; }
    public MailDomain Domain { get; set; } = null!;
    public string NormalizedAddress { get; set; } = "";
    public string AccessTokenHash { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastAccessAt { get; set; }
    public string IpHash { get; set; } = "";
}
public sealed class MailMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MailboxId { get; set; }
    public Mailbox Mailbox { get; set; } = null!;
    public string InternetMessageId { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public string To { get; set; } = "";
    public string Cc { get; set; } = "";
    public string Subject { get; set; } = "";
    public string TextBody { get; set; } = "";
    public string HtmlBody { get; set; } = "";
    public string RawHeaders { get; set; } = "";
    public long Size { get; set; }
    public DateTime ReceivedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public List<MailAttachment> Attachments { get; set; } = [];
}
public sealed class MailAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MailMessageId { get; set; }
    public MailMessage MailMessage { get; set; } = null!;
    public string StorageId { get; set; } = "";
    public string FileName { get; set; } = "attachment";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string? ContentId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
// Transactional outbox: contains invalidations only, never message contents or access tokens.
public sealed class MailEvent
{
    public DateTime? DispatchedAt { get; set; }
    public long Id { get; set; }
    public Guid MailboxPublicId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
public sealed class BlockRule
{
    public int Id { get; set; }
    public string Kind { get; set; } = "sender";
    public string Value { get; set; } = "";
}
public sealed class SmtpStatus
{
    public int Id { get; set; } = 1;
    public DateTime LastHeartbeatAt { get; set; }
    public long Connections { get; set; }
    public long AcceptedMessages { get; set; }
}
