# Security model and operational review

## Controls

| Threat | Control |
|---|---|
| IDOR / cross-mailbox access | Every message/HTML/download/delete query resolves the protected token to an unexpired mailbox and filters by that mailbox. Public IDs confer no access. Integration test uses separate browsers A/B/anonymous. |
| Token prediction / DB leak | RandomNumberGenerator 256-bit tokens; SHA-256 token hashes; fixed-time check; separate random public GUIDs. Cookie is Data Protection encrypted/authenticated. |
| XSS / email HTML injection | MimeKit parse; HtmlSanitizer explicit allowlist; no scripts, SVG, CSS, forms, event handlers, links or embeds. HTML rendered in a sandboxed iframe with restrictive independent CSP. Plain text is Razor-encoded. |
| Tracking / SSRF | External images blocked by default. Optional HTTPS images load in the browser after explicit opt-in; server never fetches arbitrary URLs. Referrer is suppressed. Clicking opt-in exposes browser IP to the image host. |
| CSRF / WebSocket hijacking | SameSite=Strict cookies; ASP.NET Core antiforgery on every API write including login/logout; same-origin checks on hub/Blazor handshake. |
| Attachment execution / traversal | Random storage IDs validated as 32 hex characters; no user filename in physical paths; private storage; owner check; forced attachment, octet-stream, nosniff. Only signature-checked PNG/JPEG/GIF/WebP CID parts can be embedded as data URLs; SVG/HTML remains download-only. |
| SQL injection / duplicate names | EF LINQ/parameters, fixed SQL lock names only, unique normalized address index, serializable allocation transaction and database application lock. |
| SMTP TLS state injection / downgrade after upgrade | Discard buffered plaintext and envelope/greeting state; require fresh EHLO; close on handshake failure; no plaintext fallback or TLS renegotiation. |
| Relay abuse | Local active-domain check at RCPT and at DATA commit; no AUTH/forward/outbound code. Unknown local recipients reject by default. |
| Enumeration | No unauthenticated address lookup/inbox endpoint; generic unavailable responses; rate-limited creation. SMTP RCPT and custom-name conflicts necessarily reveal availability; they never reveal contents/access tokens. |
| DoS / bombing / storage exhaustion | Configurable connection/IP/message/recipient/byte/attachment/mailbox/global storage limits; MIME depth bound; command/connection deadlines; API fixed-window rate limits; cleanup/retention. |
| Admin takeover | ASP.NET Core Identity, 14-character minimum password, lockout, secure separate cookie, Admin role, short session, no public registration or hard-coded bootstrap credentials. |
| Browser content indexing | noindex/nofollow headers globally, meta robots, no shared caching of APIs/admin content. |

## Required operator controls

- HTTPS and correct AllowedHosts; persisted Data Protection keys with restrictive ACLs. On Windows production, keys use user-scope DPAPI unless a certificate thumbprint is supplied. IIS pool profile must load. For recoverable backups/migration use a certificate with private key ACL granted only to the Web identity; retain old keys/certificates while cookies depend on them. SMTP does not need cookie decryption keys.
- Configure a random `Security__IpHashKey` (at least 32 characters) through protected configuration. IP addresses stored for mailbox quotas use HMAC-SHA256. Rotating this key resets effective IP quota grouping; do so deliberately.
- Web/SMTP DB accounts get DML access only; migration/backup use separate privileged identities. Prefer local SQL Server integrated security or a gMSA for remote SQL Server. Never expose SQL Server publicly. Certificate-validated encrypted SQL connections in production.
- Private storage outside all web roots, ACL only service identities/admins; prohibit symlink/reparse-point creation by untrusted users. Quota is logical stored MIME size, not a filesystem hard quota. Attachment orphans can remain for one hour; use OS volume quotas/free-space alerts as a second boundary.
- SMTP supports STARTTLS with TLS 1.2/1.3; configure a valid certificate and private-key ACL as described in [smtp.md](smtp.md). Optional TLS defaults to plaintext compatibility and cannot prevent pre-handshake STARTTLS stripping. `RequireStartTls=true` rejects plaintext envelopes but can reduce Internet delivery. After upgrade all SMTP state is reset and handshake failure closes the connection. Monitor certificate expiry/renewal and verify public trust/chain externally. No inbound DKIM/SPF/DMARC verification or antivirus sandbox is included; sender display names are untrusted. Do not use disposable mail for sensitive accounts. Downloaded files can be malicious even when delivery/storage is safe.
- Limit public exposure of admin with IIS IP restrictions/VPN where practical. MFA is not implemented; provision a strong unique password. Bootstrap credentials are only used by the explicit CLI and should be removed afterward.
- Keep one Web worker and one SMTP process. Application rate limits are per process; they are not a distributed DDoS system. Do not trust arbitrary forwarded headers. Put infrastructure-level traffic limits in front if needed.
- Patch .NET, SQL Server, Windows and dependencies; run `dotnet list package --vulnerable --include-transitive`. Monitor 451/452 responses, failed login spikes, cleanup errors, health/SMTP heartbeat, file/SQL/log volume usage and backups.

## Retention and backup implications

Deletion/expiration immediately removes API access. Files are garbage-collected after a one-hour race-safety grace. Deleted data may remain in backups until backup retention expires. SQL backups, attachment storage, access metadata and Data Protection keys are sensitive. Persisting keys keeps sessions usable across restarts; deleting keys logs out all browsers. Server administrators/database/storage operators are trusted and can read mail at rest. Disk/backup encryption is an operator responsibility.
