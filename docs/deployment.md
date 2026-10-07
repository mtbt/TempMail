# Windows Server 2022/2025 deployment

Run administrative commands from an elevated PowerShell console. Replace example paths, hostnames and certificates. Production deployment is an operator action; source code/build tests do not provision Windows, public DNS or a SQL Server instance.

## 1. Prerequisites

- Windows Server 2022/2025 x64, patched; IIS with WebSockets.
- .NET 10 **Hosting Bundle**, including ASP.NET Core Module V2, from https://dotnet.microsoft.com/download/dotnet/10.0. Install IIS before the bundle (repair the bundle if installed first). Verify Microsoft's digital signature. SDK is needed on the build/migration machine; runtime/Hosting Bundle on the server.
- SQL Server 2022+ or SQL Server Express. Express has a 10 GB database limit: choose an application quota well below that because text bodies, indexes, Identity and metadata also consume SQL space; attachments live outside SQL.
- Public DNS, static public IP, inbound TCP 25/443 and HTTPS certificate in LocalMachine\My.
- Separate private storage and key directories, enough disk, backups, monitoring. Production local config stays outside source control.

```powershell
Install-WindowsFeature Web-Server, Web-WebSockets, Web-Mgmt-Console -IncludeManagementTools
# Install the signed .NET 10 Hosting Bundle, then restart WAS/W3SVC during a maintenance window.
dotnet --list-runtimes
```

## 2. Publish

From the repository root:

```powershell
dotnet tool restore
dotnet restore --locked-mode
dotnet build -c Release
dotnet test -c Release
dotnet publish -c Release
# Separate output folders; never publish the whole solution into one shared -o folder.
dotnet publish src\TempMail.Web\TempMail.Web.csproj -c Release -o C:\Publish\TempMail.Web
dotnet publish src\TempMail.SmtpServer\TempMail.SmtpServer.csproj -c Release -r win-x64 --self-contained false -o C:\Publish\TempMail.Smtp
```

Web uses an in-process ASP.NET Core Module handler; generated/published web.config contains the correct executable/dll. SMTP is a framework-dependent Windows x64 executable with AddWindowsService; it runs independently of IIS.

## 3. SQL database and identities

Create database `TempMail` through SSMS or reviewed SQL. Use a migration identity with schema permissions, separate from Web/SMTP runtime identities. For local SQL Server Windows integrated security, create logins/users after the IIS pool/service identities exist:

```sql
USE [master];
CREATE DATABASE [TempMail];
GO
CREATE LOGIN [IIS APPPOOL\TempMail] FROM WINDOWS;
CREATE LOGIN [NT SERVICE\TempMailSmtp] FROM WINDOWS;
GO
USE [TempMail];
CREATE USER [IIS APPPOOL\TempMail] FOR LOGIN [IIS APPPOOL\TempMail];
CREATE USER [NT SERVICE\TempMailSmtp] FOR LOGIN [NT SERVICE\TempMailSmtp];
ALTER ROLE db_datareader ADD MEMBER [IIS APPPOOL\TempMail];
ALTER ROLE db_datawriter ADD MEMBER [IIS APPPOOL\TempMail];
ALTER ROLE db_datareader ADD MEMBER [NT SERVICE\TempMailSmtp];
ALTER ROLE db_datawriter ADD MEMBER [NT SERVICE\TempMailSmtp];
```

Review/tighten table-specific permissions if needed; SMTP does not need Identity-table access. Application locks use the current database's public principal. For remote SQL Server use properly configured gMSA/domain accounts and SPNs or securely injected SQL credentials. Never grant application accounts sysadmin. SQL service certificates should validate for the connection hostname; do not disable certificate validation in production.

Supply `ConnectionStrings__DefaultConnection` securely in the migration shell, for example an integrated-security connection to your configured SQL hostname:

```powershell
# This example contains no password. Use a hostname covered by your SQL TLS certificate.
$env:ConnectionStrings__DefaultConnection = 'Server=sql.example.internal;Database=TempMail;Integrated Security=true;Encrypt=true;TrustServerCertificate=false'
dotnet ef database update --project src\TempMail.Infrastructure
```

There is no automatic migration at Web or SMTP startup. Review `deployment/migrate.sql` (initial idempotent migration script) or generate a fresh script for subsequent upgrades:

```powershell
dotnet ef migrations script --idempotent --project src\TempMail.Infrastructure -o artifacts\migration.sql
# Apply reviewed script with an appropriate migration identity after backup.
```

## 4. Secure configuration

Both processes need the **same SQL database and attachment StoragePath**. The application loads appsettings.json, optional appsettings.Local.json, then environment variables/command line. Local files are gitignored. Never put actual connection strings containing passwords, admin passwords or secret keys into tracked JSON, command-line arguments, web.config committed to Git, logs or scripts.

Create an ACL-protected `appsettings.Local.json` in each deployed application directory (not under wwwroot). Minimum Web overrides:

```json
{
  "ConnectionStrings": { "DefaultConnection": "<securely supplied>" },
  "AllowedHosts": "tempmail.example.com",
  "TempMail": {
    "Domains": ["mail.example.com"],
    "StoragePath": "D:\\TempMailStorage",
    "DataProtectionPath": "D:\\TempMailKeys",
    "MaxStorageMB": 4096
  },
  "Security": { "IpHashKey": "<random secret, at least 32 characters>" },
  "Logging": { "FilePath": "D:\\TempMailLogs\\web-.log" }
}
```

SMTP overrides:

```json
{
  "ConnectionStrings": { "DefaultConnection": "<same database, SMTP identity>" },
  "TempMail": {
    "StoragePath": "D:\\TempMailStorage",
    "DataProtectionPath": "D:\\TempMailKeys",
    "MaxStorageMB": 4096
  },
  "Smtp": { "Host": "0.0.0.0", "Port": 25, "RejectUnknownMailbox": true },
  "Logging": { "FilePath": "D:\\TempMailLogs\\smtp-.log" }
}
```

Keep common quotas/lifetimes/reserved-name options synchronized. SMTP uses no Data Protection keys but shared option validation requires an absolute path. Alternatively use process/service environment settings such as `TempMail__StoragePath`, `Security__IpHashKey`, `ConnectionStrings__DefaultConnection`. Restart the affected process after configuration changes.

Generate the HMAC key using a cryptographic generator, and write it directly into your secret manager/protected configuration rather than terminal output/history. Persist it across restarts. For IIS environment settings use IIS Configuration Editor/app-pool environment variables or a secure deployment tool. Do not set sensitive environment variables for unrelated machine-wide processes unnecessarily.

## 5. IIS site and ACL

```powershell
.\deployment\install-iis.ps1 -PublishPath C:\Publish\TempMail.Web `
  -HostName tempmail.example.com -CertificateThumbprint '<HTTPS thumbprint>' `
  -SitePath C:\Sites\TempMail -StoragePath D:\TempMailStorage -KeyPath D:\TempMailKeys
```

The script installs IIS features, optionally installs a supplied signed Hosting Bundle, creates **No Managed Code** application pool, configures profile loading, a single worker and non-overlapping recycling, copies published files preserving local settings/logs, binds 80/443 with SNI and grants minimum folder access. Set the site to start only after configuration/migrations/bootstrap. `AllowedHosts` must match the site. HTTPS redirection/HSTS are enabled outside Development/Testing. Bind a valid public HTTPS certificate and configure automatic renewal.

For production Data Protection, Windows user-scope DPAPI protects persisted keys by default; the app-pool identity/profile must remain stable. For portability and disaster recovery, configure `TempMail__DataProtectionCertificateThumbprint` with a dedicated certificate in the Windows certificate store and grant its private key read permission to the Web pool. Back up/export this certificate securely. Restrict keys to the Web identity, SYSTEM and administrators. Do not give SMTP access to keys. Never place keys under the application/static-content directory.

ACL the private storage and log directories to required Web/SMTP identities only. Remove inherited broad Users/Everyone permissions if the parent volume grants them. Runtime identities need RX on binaries, Modify on their logs, Web Modify on keys, and both Modify on attachment storage (cleanup runs in Web). Protect appsettings.Local.json from ordinary local users; only administrators should modify deployment/configuration. The script does not infer or change all existing organization ACL policies.

## 6. Seed and initial admin

Run once from the **deployed Web directory** under an account with appropriate DB permissions and configuration:

```powershell
Set-Location C:\Sites\TempMail
# Read initial credentials without placing them in command history.
$credential = Get-Credential -Message 'Initial TempMail admin (username must be an email address)'
$env:AdminBootstrap__Email = $credential.UserName
$env:AdminBootstrap__Password = $credential.GetNetworkCredential().Password
try {
    dotnet .\TempMail.Web.dll --bootstrap-admin
    if ($LASTEXITCODE -ne 0) { throw 'Bootstrap failed.' }
} finally {
    Remove-Item Env:AdminBootstrap__Email -ErrorAction SilentlyContinue
    Remove-Item Env:AdminBootstrap__Password -ErrorAction SilentlyContinue
    $credential = $null
}
```

The command seeds configured domains and Admin role and creates one admin; it does not start Web, migrate schema, print credentials or reset an existing account. Password must satisfy Identity's policy (14+ characters, upper/lower/digit/symbol). Use a unique random password. `dotnet TempMail.Web.dll --seed` seeds domains/role without an account and is repeatable. Existing domains are not re-enabled by reseeding. Domain changes can then be managed in `/admin`. There is no public registration, default password or email-based password reset in a receive-only system.

```powershell
Start-Website TempMail
Invoke-WebRequest https://tempmail.example.com/health/ready
```

## 7. SMTP Windows Service

```powershell
.\deployment\install-smtp-service.ps1 -PublishPath C:\Publish\TempMail.Smtp `
  -ServicePath C:\Services\TempMail.Smtp -StoragePath D:\TempMailStorage
# Finish protected configuration and SQL permissions, then:
Start-Service TempMailSmtp
Get-Service TempMailSmtp
```

Display name: **TempMail SMTP Service**. The script creates service `TempMailSmtp` under its virtual service account and configures delayed auto-start and restart after failure. It copies only published files, preserves local config/logs, grants private storage access and does not run as LocalSystem. For remote SQL use an appropriate domain service identity. Manual equivalent:

```powershell
sc.exe create TempMailSmtp binPath= '"C:\Services\TempMail.Smtp\TempMail.SmtpServer.exe"' start= delayed-auto obj= 'NT SERVICE\TempMailSmtp' DisplayName= 'TempMail SMTP Service'
sc.exe failure TempMailSmtp reset= 86400 actions= restart/5000/restart/15000/restart/60000
sc.exe failureflag TempMailSmtp 1
```

SMTP uses AppContext.BaseDirectory, so it does not accidentally read configuration relative to `C:\Windows\System32`. Check `logs/smtp-*.log` or configured absolute log path. Admin dashboard and authenticated `/api/admin/smtp-health` show the database heartbeat (fresh within 30 seconds). Public `/health/ready` checks Web DB schema access and real storage write/delete; `/health/live` is only process liveness.

### SMTP STARTTLS certificate (separate from IIS)

IIS HTTPS bindings do not configure SMTP. Install a publicly trusted certificate covering the MX hostname (e.g. `mail.example.com`) with its private key in **Certificates (Local Computer) → Personal → Certificates** (`certlm.msc`). Install the issuer's intermediates in Intermediate Certification Authorities. Use Server Authentication EKU and a modern RSA/ECDSA key. In **All Tasks → Manage Private Keys**, grant Read to `NT SERVICE\TempMailSmtp` (or the configured gMSA); do not grant broad Users/Everyone access or give SMTP access to the Web Data Protection key.

Put `Smtp:Tls:ServerName`, `CertificateThumbprint`, `StoreName=My`, `StoreLocation=LocalMachine` in the SMTP service's protected `appsettings.Local.json`; see the full [TLS configuration](smtp.md#tls-configuration). Keep `RequireStartTls=false` for normal public inbound SMTP. A certificate configuration error blocks service startup instead of silently dropping TLS. Check service logs under the real identity, not only an administrator's interactive session.

For PFX deployments, use an absolute path outside both applications/web roots, restrict file read to the service identity and administrators, and supply `Smtp__Tls__PfxPassword` through a protected service-specific environment or secret deployment system. Never commit the password or pass it on a command line. Do not configure both PFX and a thumbprint. Test ephemeral-key import on the target Windows/Schannel version; prefer the Certificate Store for Windows production.

Windows Server 2022/2025 Schannel provides TLS 1.3 subject to OS policy. The application allows only TLS 1.2/1.3; do not weaken OS TLS/cipher policy to accommodate legacy clients. Verify negotiated protocol, certificate hostname and complete trust chain from an external host using the OpenSSL commands in [smtp.md](smtp.md#tls-acceptance-checks). Verify TLS 1.2 and 1.3, old-protocol rejection, local delivery and non-local relay rejection. IIS HTTPS must also remain valid independently.

Automate certificate renewal with your approved CA tooling, monitor expiry and handshake failures, then update the configured thumbprint/PFX, grant the new private key ACL, and `Restart-Service TempMailSmtp`. The certificate is a startup snapshot; changing a file/store/config alone does not reload it. Verify advertisement and external handshake after each renewal. Do not remove the old certificate until the new one has passed checks; rollback by restoring the previous valid configuration and restarting. No database migration is needed for STARTTLS.

## 8. Firewall, DNS, end-to-end test

```powershell
.\deployment\firewall.ps1
```

Open inbound 25 for SMTP, 443 for Web, 80 for HTTPS redirect/certificate HTTP challenges where needed. Configure provider firewall/NAT too. Do not expose 1433. Follow [dns.md](dns.md) and [smtp.md](smtp.md), including an external port-25 probe. Create a mailbox in the UI, send real mail, confirm inbox update without page reload, inspect HTML and attachments and verify expiry. Verify non-local RCPT gets 550. Before public launch, configure and externally verify SMTP STARTTLS and explicitly choose the optional/required TLS policy described in [smtp.md](smtp.md).

## 9. Backup, upgrade and rollback

- Back up SQL database plus attachment storage as a consistent set. Pause receiving/cleanup or take coordinated snapshots; file writes precede DB commit. Encrypt backups and limit retention for disposable mail.
- Back up protected config, Data Protection key ring and any certificate/private key required to restore it. Record service identities/ACLs, certificate renewal, firewall and DNS. Test restoration onto a non-public host.
- For upgrades: build/test/publish to staging, review migration SQL, back up, stop SMTP and IIS site, apply migration with migration identity, copy new binaries while preserving local configuration/keys/storage, start Web, verify readiness, start SMTP, verify heartbeat and external delivery. Do not overwrite key rings. Keep previous binaries and compatible database backup.
- Rollback only when schema compatibility is known; otherwise restore the tested backup set. Never apply automatic destructive down-migrations to recover a failed release.
- SQL Express has no SQL Agent; schedule SQL backups through Task Scheduler or your backup system. Monitor backup success and free space.

## Troubleshooting

| Symptom | Check |
|---|---|
| IIS 500.30 / 500.31 | Hosting Bundle/runtime version, correct app pool, Event Viewer, app settings, required absolute storage/key paths. Temporarily enable ANCM stdout logs only with restricted ACLs, then disable. |
| 503 readiness | SQL connection/certificate/login/schema, migration applied, storage directory/ACL/full disk. |
| Empty inbox after sender success | Same domain/database/storage in both processes, created unexpired mailbox, SMTP logs, event table/dispatcher; do not infer delivery from port-open alone. |
| SignalR reconnect loop | IIS WebSockets, HTTPS proxy forwarding, same-origin host, one worker, application not idling, CSP/browser console. |
| Cookie lost after recycle | Persistent keys, stable app-pool identity/profile, DPAPI/certificate private-key access, clock synchronization. |
| SMTP inaccessible | Service bind, port conflict, Windows/provider firewall, NAT, DNS Only MX target, provider inbound-25 restrictions. |
| SMTP 451 | SQL/storage outage, transaction lock/deadlock, quota/full disk, permissions; sender should retry. |
| SMTP 550 | Recipient domain active and configured, mailbox created/unexpired, reserved/blacklist rules. |
| Files remain after delete | One-hour orphan grace plus cleanup interval; inspect cleanup logs and ACLs. API access should already be revoked. |
