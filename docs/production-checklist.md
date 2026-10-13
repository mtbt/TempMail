# Production readiness: Windows Server 2022/2025

Release gate for .NET 10, Blazor on IIS, SQL Server/Express and one receive-only
SMTP Windows Service with the existing STARTTLS implementation. No outbound
SMTP, AUTH or relay is introduced. Use [deployment.md](deployment.md) for
commands and [smtp.md](smtp.md) for TLS dialogues.

**Status: Windows Server acceptance required.** An unchecked item is not a pass.
Cloud builds, Linux TLS tests and SQLite tests do not validate IIS, Windows
Service Control Manager, SQL Server, Certificate Store/private-key ACLs, Schannel
or inbound Internet TCP 25. Record server/OS version, release commit, operator,
date, sanitized evidence and result for every stage. Keep secrets out of evidence.

## 1. Clean Windows Server

- [ ] Patch Windows Server 2022/2025 x64; synchronize time; record reboot status.
- [ ] Reserve static public IPv4, DNS names and dedicated local NTFS directories.
  Examples: `C:\Sites\TempMail`, `C:\Services\TempMail.Smtp`,
  `D:\TempMailStorage`, `D:\TempMailKeys`. They must not overlap or contain
  junctions/symlinks. Keep private data outside every IIS site and virtual directory.
- [ ] Restrict RDP/admin access and verify the provider permits inbound TCP 25.
  Keep public traffic blocked until configuration and acceptance are complete.
- [ ] Use elevated **Windows PowerShell 5.1** for deployment scripts. Copy the
  entire `deployment` directory, including `common.ps1`. Review scripts before use.
  Installers manage dedicated TempMail directories, replace their ACLs recursively
  and stop both applications. Do not point them at shared organization folders.
  Back up existing data/configuration/ACLs before upgrades. Unexpected identities,
  site paths or bindings require a reviewed manual migration; do not force them.
- [ ] On staging, test preflight rejection of equal/parent/child overlaps with the
  existing SMTP executable directory and IIS site, invalid certificates, wrong
  identities, paths and bindings. Verify no service/site/pool state, application
  files or ACLs change on these validation failures. Prerequisite installation is
  a separate mutation phase and may require reboot; application deployment starts
  only after its checks pass. Test recovery from a copy/ACL failure using the
  documented rerun/backup procedure before explicitly activating both applications.

## 2. SQL Server / SQL Server Express and EF Core

- [ ] Install and patch SQL Server 2022+ or Express; keep database ports private.
  Use an explicit instance name/port and a trusted SQL server certificate whose
  hostname matches the connection. Require `Encrypt=true;TrustServerCertificate=false`.
- [ ] Create the database under the migration/operator identity. Allocate database,
  transaction log and attachment disks with free-space alerts. For Express, account
  for its 10 GB relational data limit; the MIME quota does not bound database size.
  Start with a conservative `MaxStorageMB` (for example 4096) and monitor actual use.
- [ ] Publish and create the IIS pool and SMTP service in stages 3–5, then create
  SQL logins/users for `IIS APPPOOL\TempMail` and `NT SERVICE\TempMailSmtp`.
  Use the local integrated-security examples in deployment.md. Remote SQL requires
  reviewed domain/gMSA identities and SPNs; installers deliberately reject a changed
  existing identity. Do not use LocalSystem, sa, db_owner or sysadmin at runtime.
- [ ] Grant runtime DML only; prefer table-specific grants for SMTP (no Identity
  tables). Verify `sp_getapplock` works in this database under both runtime identities.
  Keep migration/schema/backup privileges separate.
- [ ] Run `dotnet tool restore`, `dotnet ef migrations has-pending-model-changes
  --project src\TempMail.Infrastructure`, and generate/review an idempotent script:
  `dotnet ef migrations script --idempotent --project src\TempMail.Infrastructure
  -o artifacts\migration.sql`. Apply it under the migration identity, with backup
  first; check every SQL error. No runtime automatic migration is implemented.
- [ ] Inspect `__EFMigrationsHistory`; rerun the script on staging to verify
  idempotency. Run the existing `TempMailTest__SqlConnection` integration test
  against a disposable real SQL instance, using an identity permitted to create
  and drop only test databases. It creates a random `TempMailTest_*` database and
  deletes it afterward. Verify migration and concurrent duplicate allocation.
- [ ] Exercise create/delete/expiry, concurrent delivery, quotas, transaction failure
  and database restart under actual runtime logins. SQLite does not prove SQL Server
  locking, collation or permissions. **Windows Server acceptance required.**

## 3. IIS and HTTPS prerequisites

- [ ] Install IIS, WebSockets and Application Initialization. Install the signed
  .NET 10 Hosting Bundle **after** IIS; repair/restart WAS/W3SVC or reboot as directed
  by the installer. Verify .NET 10 / ASP.NET Core 10 runtimes and AspNetCoreModuleV2.
- [ ] Import a valid public HTTPS certificate for the Web hostname in
  `LocalMachine\My`, including intermediates and private key. Verify hostname,
  server-auth EKU, trust chain, revocation access and renewal procedure.
- [ ] Keep one IIS worker, No Managed Code, ApplicationPoolIdentity, profile loaded,
  AlwaysRunning, zero idle timeout and non-overlapping recycle. Verify WebSockets
  and Blazor reconnection through the actual IIS/proxy path.

## 4. Publish and configure Web

- [ ] On the build host run `dotnet restore`, `dotnet build -c Release`,
  `dotnet test -c Release`; also use locked restore for release reproducibility.
  Preserve the lockfiles and record skipped tests with reasons.
- [ ] Publish Web separately from SMTP; never combine solution outputs into one
  directory. Run `install-iis.ps1` with the published directory, Web DNS hostname,
  HTTPS thumbprint and dedicated paths. It leaves pool/site stopped and disables
  auto-start until explicit activation. Recheck binaries after deployment; `/E`
  preserves extra files, so review/remove obsolete release files in maintenance.
- [ ] Create protected `appsettings.Local.json` only in the deployed application
  directory. Configure exact `AllowedHosts`, common domains/quotas, SQL connection,
  absolute attachment/key paths and a cryptographically random persistent
  `Security:IpHashKey` of at least 32 characters. Do not use sample placeholders.
- [ ] Confirm Production environment (not Development/Testing), HSTS/HTTPS redirect,
  secure cookies and same-origin checks. IIS terminates HTTPS; do not trust arbitrary
  forwarded headers. Verify remote client IPs before relying on per-IP quotas.
- [ ] Use `--seed` / `--bootstrap-admin` under a controlled operator identity as
  described in deployment.md; clear bootstrap variables immediately afterward.
  There is no default admin or public registration. Restrict admin via VPN/IP policy.

## 5. Publish and install SMTP Windows Service

- [ ] Publish `src\TempMail.SmtpServer` with `-c Release -r win-x64
  --self-contained false`. Run `install-smtp-service.ps1` after the IIS installer;
  supply the same StoragePath and KeyPath (including non-default paths).
- [ ] Verify `sc.exe qc TempMailSmtp`: quoted executable path, virtual identity
  `NT SERVICE\TempMailSmtp`, delayed automatic start; verify `sc.exe qsidtype`,
  `sc.exe qfailure` and `sc.exe qfailureflag`. Installer preserves local config/logs,
  bounds copy retries, checks native-command exit codes and leaves the service stopped.
- [ ] On Windows PowerShell 5.1, install and rerun with both default paths and paths
  containing spaces. Verify exact quoted `Win32_Service.PathName`, virtual account,
  `StartMode=Auto`, registry `Start=2` and `DelayedAutoStart=1`. Exercise the legacy
  unquoted-path repair for the same binary/account; reject different executables,
  added command-line arguments and different accounts before stopping production.
  Registration uses CIM, not native `sc.exe` path quoting. Verify a new service is
  manual during setup and remains stopped until explicit activation.
- [ ] Configure the same SQL database/storage and synchronized quota/lifetime rules
  as Web. Keep `RejectUnknownMailbox=true`; bind TCP 25 to the intended interface.
  The shared options require an absolute DataProtectionPath, but SMTP must have
  **no access** to its keys. No SMTP AUTH/outbound transport is needed.
- [ ] Restart/reboot under the actual identity and verify automatic startup. In
  isolated staging force a process crash and verify recovery delays (5/15/60 seconds).
  A graceful stop is not a crash. Also simulate listener/bind and background-worker
  failures: verify SCM observes failure and alerts detect a stopped service;
  recovery settings alone do not prove recovery from every managed failure.
  **Windows Service acceptance required.**

## 6. STARTTLS certificate and private-key access

- [ ] Prefer `LocalMachine\My` for SMTP. Configure `Smtp:Tls:ServerName` to the MX
  hostname, CertificateThumbprint, StoreName and StoreLocation. The SMTP certificate
  is separate from the IIS HTTPS binding; configuring one does not configure the other.
- [ ] In `certlm.msc` → certificate → All Tasks → Manage Private Keys, grant **Read**
  only to the SMTP identity plus trusted administrators/system as required. Verify
  CNG/CSP key access under that identity, not an administrator's interactive session.
  The startup certificate check signs a probe and fails closed for bad configuration.
- [ ] For PFX instead, configure an absolute PfxPath outside application/data/key
  directories; restrict file read to SMTP/system/admins and protect PfxPassword in
  service-specific secret configuration. Never configure both Store and PFX sources.
  Keep passwords outside Git, logs and command lines; keep the PFX file ACL-protected.
  Windows uses `DefaultKeySet` because Schannel requires an OS-backed key handle
  for `SslStream`; imports are neither marked exportable nor given `PersistKeySet`.
  Verify default key-storage access and real STARTTLS handshakes under the actual
  service account, plus certificate disposal on shutdown (temporary key cleanup).
- [ ] Verify TLS 1.2 and 1.3, old-protocol rejection, hostname/chain/public trust,
  fresh EHLO/envelope reset, handshake failure closure and encrypted local delivery.
  Use external `openssl s_client -starttls smtp -connect mail.example.com:25
  -servername mail.example.com -verify_hostname mail.example.com -verify_return_error`.
  Repeat with `-tls1_2` and `-tls1_3`; inspect results rather than port-open alone.
- [ ] Keep RequireStartTls=false for normal Internet inbound compatibility unless
  the operator explicitly accepts delivery loss. Optional STARTTLS cannot prevent
  pre-handshake stripping. There is no plaintext fallback after a failed upgrade.
- [ ] Renew before expiry, update thumbprint/PFX and ACL, restart SMTP (certificate
  loaded at startup), recheck externally. Retain a valid rollback certificate.
  **Certificate Store, private-key ACL and Schannel: Windows Server acceptance required.**

## 7. Directory ACLs and Data Protection

- [ ] Review effective permissions (including group membership) against this matrix.
  Installers reset dedicated trees, including explicit child ACLs; custom backup
  identities must be reviewed and re-applied after each installation.

| Resource | Web identity | SMTP identity | Administrators / SYSTEM |
|---|---|---|---|
| Web binaries and local configuration | Read/execute | None | Full control |
| SMTP binaries and local configuration | None | Read/execute | Full control |
| Web logs | Modify | None | Full control |
| SMTP logs | None | Modify | Full control |
| Attachment storage | Modify | Modify | Full control |
| Persistent Data Protection key directory | Modify | None | Full control |
| SMTP private key / PFX | None unless explicitly shared | Read | Controlled administrative access |
| Dedicated Data Protection private key | Read | None | Controlled administrative access |

- [ ] Confirm ordinary Users/Everyone/IIS_IUSRS cannot read private data/configuration
  and runtime identities cannot modify binaries or local configuration. Check root
  and existing descendants with `icacls`; prohibit untrusted reparse-point creation.
- [ ] Keep attachments and key rings outside all web roots/virtual mappings. For
  external log paths, provision equivalent per-process ACLs manually. Default
  `logs` directories are handled by installers; SMTP relative logs resolve against
  its executable directory, even when SCM's working directory is System32.
- [ ] Persist Data Protection keys and keep the application name stable. On Windows
  production, the default is user-scope DPAPI: stable pool identity/profile is needed.
  For restore to another server, prefer a dedicated certificate, securely back up its
  private key and grant Web read. Confirm the framework resolves the configured
  thumbprint in the intended store under the pool identity. Never give SMTP that key.
- [ ] Test cookie/mailbox access across pool recycle, service restart and reboot;
  restore onto an isolated replacement server. Backing up DPAPI-encrypted XML alone
  is not sufficient for cross-machine recovery. Retain old keys/certificates while
  existing cookies need them. **Windows Server acceptance required.**

## 8. Firewall

- [ ] Review `firewall.ps1 -WhatIf`, then apply TCP 25/80/443 rules to the intended
  profiles (default Any). Existing named rules are updated rather than silently
  left disabled. Verify effective Windows/GPO rules, provider firewall and NAT too.
- [ ] TCP 80 is for HTTPS redirect/approved certificate challenges; TCP 443 is Web,
  TCP 25 is inbound SMTP. Do not expose SQL 1433 or administrative ports publicly.
  Uninstalling SMTP preserves data and firewall rules; retire rules separately only
  after checking whether Web still needs 80/443.

## 9. DNS A / MX

- [ ] Set Web A and MX-target A to the real public IPv4. Each recipient domain
  (the portion after @) needs MX pointing to the SMTP hostname, not an IP/CNAME.
- [ ] Set the MX target to DNS Only in Cloudflare. Publish AAAA only if IPv6 binding,
  routing, firewall and external delivery are all verified. Check authoritative and
  public recursive DNS; document TTL/rollback. See [dns.md](dns.md).

## 10. Activation, health checks and observability

After migrations, seed, protected configuration, certificate and ACL checks:

```powershell
Import-Module WebAdministration
Set-ItemProperty 'IIS:\AppPools\TempMail' -Name autoStart -Value $true
Set-ItemProperty 'IIS:\Sites\TempMail' -Name serverAutoStart -Value $true
Start-WebAppPool TempMail
Start-Website TempMail
Start-Service TempMailSmtp
Invoke-WebRequest https://tempmail.example.com/health/ready -UseBasicParsing
```

- [ ] `/health/live` returns 200 for process liveness; `/health/ready` returns 200 only
  when a schema query and storage write/delete succeed. It does not validate SMTP,
  every table, certificate validity, DNS or backup success. Simulate DB/storage
  outages on staging and confirm 503, recovery and monitoring notifications.
- [ ] Authenticated `/api/admin/smtp-health` returns 200 with heartbeat younger than
  30 seconds; stop SMTP and verify 503 after that interval. Also probe real SMTP/TLS
  externally; database heartbeat alone is insufficient for end-to-end availability.
- [ ] Verify logs under actual identities, TLS failures, 451/452, cleanup errors,
  login failures and Windows Event Log/SCM events. Avoid public detailed errors.
- [ ] Serilog rotates daily and at 50,000,000 bytes, retaining **14 files, not 14 days**.
  Multiple rolls can shorten history. Bound IIS W3C/HTTPERR/Event Log retention
  separately; leave ANCM stdout logging disabled except brief diagnosis. Log mail
  addresses/IP metadata is sensitive: restrict access and choose retention deliberately.
- [ ] Alert on volume space, database/log growth, certificate expiry, service status,
  stalled heartbeat, backups and externally observed availability. Test an alert.

## 11. Real Internet mail and relay rejection

- [ ] From outside the hosting network verify TCP 25/443 and public TLS trust. A
  localhost test does not establish inbound Internet TCP 25. **External acceptance required.**
- [ ] Create an unexpired mailbox through HTTPS, send from Gmail and Outlook (or
  independent external MTAs), then verify real-time inbox, text/HTML isolation,
  attachments, cross-mailbox denial, delete/expiry and reconnect after IIS recycle.
- [ ] Before and after STARTTLS, attempt RCPT to a non-local domain: expect 550 Relay
  denied; AUTH must not be offered/accepted. Verify unknown and inactive local
  recipients are rejected. Delivery rechecks active domains at DATA commit.
- [ ] Verify TLS failure cannot resume plaintext delivery; repeat renewal checks.
  Record sanitized SMTP reply codes and TLS protocol, never message content/tokens.

## 12. Backup, restore, upgrades and rollback

- [ ] Define RPO/RTO and retention. SQL Express has no SQL Agent: schedule backups
  through Task Scheduler/approved backup tooling and monitor exit status.
- [ ] For a consistent backup, block new traffic, stop SMTP, stop IIS site **and pool**
  (cleanup runs in the Web process), wait for termination, then back up SQL using
  `BACKUP DATABASE ... WITH CHECKSUM` and attachments as one identified set. SQL
  Server's backup identity needs access to the backup destination. In FULL recovery
  schedule transaction-log backups as well; monitor log growth.
- [ ] Securely capture configuration, full Data Protection key ring, required private
  certificates/keys, identity/profile recovery material, ACLs, release binaries,
  migration version, DNS/firewall and service settings. Encrypt backups with
  separately controlled keys. Never store these in Git or under a served directory.
- [ ] Run `RESTORE VERIFYONLY ... WITH CHECKSUM`, then perform an actual isolated
  restore; VERIFYONLY is not a restore drill. Restore SQL/attachments from the same
  set, certificates and key ring; reapply identity mappings and ACLs before starting
  Web/SMTP. Run DB integrity checks, readiness, mailbox/attachment access and cookie
  recovery. Restore tests must not accept real Internet mail accidentally.
- [ ] Measure recovery time and data loss against RPO/RTO. Document DPAPI limitations,
  backup deletion/retention and what happens to mail that expired during an outage.
- [ ] Upgrade in a maintenance window: backup, stop both processes, reviewed migration,
  deploy, verify readiness, start SMTP, verify heartbeat/Internet delivery. Re-enable
  IIS auto-start explicitly after installer runs. Roll back binaries only when schema
  compatible; otherwise restore the coordinated backup set. No automatic destructive
  down-migration. **Real SQL/Windows backup and restore acceptance required.**

## Sign-off

- [ ] Record release commit, all checks, skipped/unavailable checks and named owner.
- [ ] No unresolved launch blockers; each required Windows/external acceptance has
  evidence. A successful Cloud build alone is not authorization to mark production ready.
