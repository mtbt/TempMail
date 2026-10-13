using System.ComponentModel.DataAnnotations;
namespace TempMail.Application;
public sealed class TempMailOptions
{
    public string[] Domains { get; set; } = [];
    [Range(1, 168)] public int MailboxLifetimeHours { get; set; } = 24;
    [Range(1, 168)] public int MessageLifetimeHours { get; set; } = 24;
    public bool AllowCustomAddress { get; set; } = true;
    public bool AllowMailboxExtension { get; set; } = true;
    [Range(1, 720)] public int MaxMailboxLifetimeHours { get; set; } = 72;
    [Required] public string StoragePath { get; set; } = "";
    [Required] public string DataProtectionPath { get; set; } = "";
    public string? DataProtectionCertificateThumbprint { get; set; }
    [Range(1, 10000)] public int MaxMessagesPerMailbox { get; set; } = 100;
    [Range(1, 1000)] public int MaxMailboxesPerIp { get; set; } = 10;
    [Range(20, 10000000)] public long MaxStorageMB { get; set; } = 10240;
    [Range(10, 3600)] public int CleanupIntervalSeconds { get; set; } = 60;
    [Range(1, 1000)] public int CreateRequestsPerMinute { get; set; } = 10;
    [Range(1, 1000)] public int DeleteRequestsPerMinute { get; set; } = 20;
    [Range(10, 10000)] public int ReadRequestsPerMinute { get; set; } = 120;
    public string[] ReservedNames { get; set; } = ["admin", "administrator", "root", "postmaster", "abuse", "support", "webmaster", "hostmaster"];
}
public sealed class SmtpOptions
{
    public string Host { get; set; } = "0.0.0.0";
    [Range(1, 65535)] public int Port { get; set; } = 25;
    [Range(1, 100)] public int MaxMessageSizeMB { get; set; } = 20;
    [Range(1, 100)] public int MaxAttachmentSizeMB { get; set; } = 10;
    [Range(1, 100)] public int MaxRecipients { get; set; } = 10;
    [Range(1, 100)] public int MaxConnectionsPerIp { get; set; } = 5;
    [Range(1, 1000)] public int MaxConnections { get; set; } = 100;
    [Range(1, 32)] public int MaxConcurrentMessages { get; set; } = 4;
    [Range(1, 1000)] public int MessagesPerMinutePerIp { get; set; } = 30;
    [Range(5, 300)] public int CommandTimeoutSeconds { get; set; } = 60;
    [Range(30, 1800)] public int ConnectionLifetimeSeconds { get; set; } = 300;
    public bool RequireStartTls { get; set; } = false;
    [Range(1, 120)] public int TlsHandshakeTimeoutSeconds { get; set; } = 15;
    public SmtpTlsOptions Tls { get; set; } = new();
    public bool RejectUnknownMailbox { get; set; } = true;
}

public sealed class SecurityOptions
{
    [Required, MinLength(32)] public string IpHashKey { get; set; } = "";
}

public sealed class SmtpTlsOptions
{
    public string? ServerName { get; set; }
    public string? CertificateThumbprint { get; set; }
    public string StoreName { get; set; } = "My";
    public string StoreLocation { get; set; } = "LocalMachine";
    public string? PfxPath { get; set; }
    public string? PfxPassword { get; set; }
}
