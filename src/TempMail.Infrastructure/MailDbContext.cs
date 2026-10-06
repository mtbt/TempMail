using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using TempMail.Domain;
namespace TempMail.Infrastructure;
public sealed class MailDbContext(DbContextOptions<MailDbContext> options) : IdentityDbContext<IdentityUser>(options)
{
    public DbSet<MailDomain> Domains => Set<MailDomain>();
    public DbSet<Mailbox> Mailboxes => Set<Mailbox>();
    public DbSet<MailMessage> Messages => Set<MailMessage>();
    public DbSet<MailAttachment> Attachments => Set<MailAttachment>();
    public DbSet<MailEvent> Events => Set<MailEvent>();
    public DbSet<BlockRule> BlockRules => Set<BlockRule>();
    public DbSet<SmtpStatus> SmtpStatuses => Set<SmtpStatus>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
        b.Entity<MailDomain>().Property(x => x.DomainName).HasMaxLength(253);
        b.Entity<MailDomain>().HasIndex(x => x.DomainName).IsUnique();
        b.Entity<Mailbox>().Property(x => x.NormalizedAddress).HasMaxLength(294);
        b.Entity<Mailbox>().Property(x => x.LocalPart).HasMaxLength(40);
        b.Entity<Mailbox>().Property(x => x.AccessTokenHash).HasMaxLength(64);
        b.Entity<Mailbox>().Property(x => x.IpHash).HasMaxLength(64);
        b.Entity<Mailbox>().HasIndex(x => x.NormalizedAddress).IsUnique();
        b.Entity<Mailbox>().HasIndex(x => x.PublicId).IsUnique();
        b.Entity<Mailbox>().HasIndex(x => x.AccessTokenHash);
        b.Entity<Mailbox>().HasIndex(x => new { x.IpHash, x.ExpiresAt });
        b.Entity<Mailbox>().HasIndex(x => x.ExpiresAt);
        b.Entity<Mailbox>().HasOne(x => x.Domain).WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Restrict);
        b.Entity<MailMessage>().HasIndex(x => new { x.MailboxId, x.ReceivedAt });
        b.Entity<MailMessage>().HasIndex(x => x.ReceivedAt);
        b.Entity<MailMessage>().HasIndex(x => x.ExpiresAt);
        b.Entity<MailMessage>().HasOne(x => x.Mailbox).WithMany().HasForeignKey(x => x.MailboxId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MailAttachment>().HasOne(x => x.MailMessage).WithMany(x => x.Attachments).HasForeignKey(x => x.MailMessageId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<MailAttachment>().Property(x => x.StorageId).HasMaxLength(32);
        b.Entity<MailAttachment>().Property(x => x.FileName).HasMaxLength(255);
        b.Entity<MailAttachment>().Property(x => x.ContentType).HasMaxLength(255);
        b.Entity<MailAttachment>().HasIndex(x => x.StorageId);
        b.Entity<BlockRule>().Property(x => x.Kind).HasMaxLength(20);
        b.Entity<BlockRule>().Property(x => x.Value).HasMaxLength(320);
        b.Entity<BlockRule>().HasIndex(x => new { x.Kind, x.Value }).IsUnique();
        b.Entity<MailEvent>().HasIndex(x => x.CreatedAt);
        b.Entity<MailEvent>().HasIndex(x => new { x.DispatchedAt, x.Id });
        b.Entity<SmtpStatus>().Property(x => x.Id).ValueGeneratedNever();
    }
}
