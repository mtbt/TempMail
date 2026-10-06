# Validation record

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

Default `dotnet test` runs 47 passing tests and explicitly skips two opt-in checks (Chromium and SQL Server). Chromium was separately enabled and passed in the final full run. The remaining **one skipped test** is actual SQL Server migration/concurrent-allocation acceptance, which requires `TempMailTest__SqlConnection`. SQLite integration tests do not prove SQL Server locking/collation behavior.

The cloud could read the Microsoft SQL Server image manifest but downloading layers from `centralus.data.mcr.microsoft.com` returned HTTP 403 under the environment network policy. That required host was saved in the environment draft, along with install/start instructions. Draft persistence does not apply/publish network configuration. No SQL Server credentials were requested, invented or committed.

Not executed here: SQL Server schema application against a real server, Windows PowerShell/IIS installation, Windows Service registration/recovery, production Data Protection certificate/DPAPI recovery, external DNS/MX/firewall reachability or Gmail/Outlook delivery. Complete those staging acceptance checks before public launch. SMTP STARTTLS is not implemented; see smtp.md for the limitation and deployment implications.

Generated evidence (ignored build/test artifacts): `tests/*/TestResults/*.trx`, `artifacts/inbox-desktop.png`, `artifacts/inbox-mobile.png`, `artifacts/admin-mobile.png`. Production deployment guidance and threat controls are in deployment.md and security.md.
