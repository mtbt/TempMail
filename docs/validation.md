# Validation record

## STARTTLS change (2026-10-07)

Validated in Linux with .NET SDK 10.0.401 / runtime 10.0.12:

- `dotnet restore`: passed.
- `dotnet build -c Release`: passed, 0 warnings / 0 errors.
- `dotnet test -c Release`: **65 passed, 0 failed, 2 skipped** (34 unit + 31 integration passed). The two unchanged opt-in skips are Chromium and actual SQL Server; no tests were removed or disabled for this change.
- All 18 new STARTTLS cases passed: certificate advertisement/absence and invalid configuration, TLS 1.2 and TLS 1.3 handshakes, required fresh EHLO, envelope reset, buffered plaintext isolation, repeat STARTTLS rejection, failed/timed-out handshake closure, encrypted delivery, no relay/AUTH and optional plaintext receipt.
- Certificate Store lookup/private-key ACLs, Schannel/PFX compatibility, real certificate chain/revocation/renewal, Windows Service restart, IIS coexistence, old-protocol rejection under Windows policy and external Internet delivery still require Windows Server staging acceptance. No database schema or outbound mail changes.

## Initial implementation validation (historical)

Validated in the supplied Linux cloud workspace with .NET SDK 10.0.401 / runtime 10.0.12. These results establish build/test capability and application behavior in the test host; they do not establish Windows production readiness.

| Check | Result |
|---|---|
| `dotnet restore` and frozen-lockfile restore | Passed |
| `dotnet build` and `dotnet build -c Release` | Passed, 0 warnings / 0 errors |
| `dotnet test -c Release` with Chromium opt-in | **48 passed, 0 failed, 1 skipped** (34 unit + 14 integration passed) |
| Actual browser flow | Passed: Blazor mailbox creation, real TCP SMTP delivery, a relational SQLite test database, outbox/SignalR update within 10 seconds, sanitized sandbox HTML, no default tracking-image fetch, mobile layout, anonymous cross-mailbox denial and Identity admin sign-in |
| `dotnet publish -c Release` | Passed; Web and Worker have separate publish output directories; test projects are not publishable |
| Windows x64 SMTP framework-dependent publish | Passed; executable at `artifacts/publish/TempMail.Smtp/TempMail.SmtpServer.exe` |
| EF migration and idempotent SQL script generation | Passed; initial SQL Server migration included; EF reports no pending model changes |
| `dotnet list package --vulnerable --include-transitive` | No vulnerable packages reported by current NuGet sources |
| Cloud setup script | Executed successfully; shell syntax validated |
| JavaScript syntax | Validated with Node |

Before STARTTLS, default `dotnet test` ran 47 passing tests and explicitly skips two opt-in checks (Chromium and SQL Server). Chromium was separately enabled and passed in the final full run. The remaining **one skipped test** is actual SQL Server migration/concurrent-allocation acceptance, which requires `TempMailTest__SqlConnection`. SQLite integration tests do not prove SQL Server locking/collation behavior.

The cloud could read the Microsoft SQL Server image manifest but downloading layers from `centralus.data.mcr.microsoft.com` returned HTTP 403 under the environment network policy. That required host was saved in the environment draft, along with install/start instructions. Draft persistence does not apply/publish network configuration. No SQL Server credentials were requested, invented or committed.

Not executed here: SQL Server schema application against a real server, Windows PowerShell/IIS installation, Windows Service registration/recovery, production Data Protection certificate/DPAPI recovery, external DNS/MX/firewall reachability or Gmail/Outlook delivery. Complete those staging acceptance checks before public launch. STARTTLS is now implemented; see the current validation above and smtp.md for TLS deployment checks.

Generated evidence (ignored build/test artifacts): `tests/*/TestResults/*.trx`, `artifacts/inbox-desktop.png`, `artifacts/inbox-mobile.png`, `artifacts/admin-mobile.png`. Production deployment guidance and threat controls are in deployment.md and security.md.
