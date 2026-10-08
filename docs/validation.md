# Validation record

## Production readiness audit (2026-10-07)

Started from a clean `feature/production-readiness` at STARTTLS commit
`aced063c372d812621782110bab0c48c1bbfdd1b`. No reset/fetch of the stale local
`origin/main` was used. STARTTLS implementation and all tests remain intact.

Current Linux Cloud results (.NET SDK 10.0.401 / runtime 10.0.12):

| Check | Current result |
|---|---|
| Restore | Passed; all projects restored/up to date with existing package cache |
| Release build | Passed: 0 warnings, 0 errors |
| Release tests | **Blocked by environment**, exit 1 before test execution |
| EF pending model changes | None |
| Idempotent SQL generation | Passed; byte-identical to tracked `deployment/migrate.sql` |
| Git whitespace / documentation links | Passed |
| Secret review | No private-key artifacts or actual secrets found in reviewed tracked files; production connection settings remain empty |
| PowerShell | Static source review only; no PowerShell runtime installed here; Windows execution required |

Restore, build and test were run sequentially, without `--no-restore`, using
`-m:1 -p:UseSharedCompilation=false` to avoid sandbox-denied MSBuild named-pipe
communication. This is an invocation-only adjustment, not a source change.
The earlier multi-process MSBuild failure was `SocketException (13): Permission
denied` in `NamedPipeServerStream`. Full current test logs show the same sandbox
restriction at VSTest `SocketServer.Start` / `TcpListener`, **before either test
assembly executes**. A network-enabled retry was cancelled before execution.
There is no current passing-test count and no evidence of a source regression
from this runner failure. Historical passing results below are not a new pass.

The two existing opt-in tests are unchanged: Chromium requires
`TEMPMAIL_BROWSER_SMOKE=1` plus Playwright/Chromium; actual SQL Server requires
`TempMailTest__SqlConnection` for a disposable test instance. Neither is configured
for this run. Because the runner aborts before discovery/execution, these are
expected skip conditions, not a newly observed skipped-test result. No test was
removed, disabled, filtered or changed to obtain a pass.

Full local logs: `/tmp/production-final-restore.log`,
`/tmp/production-final-build.log`, `/tmp/production-final-test.log` (ephemeral,
not release artifacts). Rerun all three commands on an executor permitting local
sockets and complete [production-checklist.md](production-checklist.md) before
release. IIS, Windows Service/recovery, real SQL Server, Windows Certificate Store,
private-key ACLs, DPAPI, Schannel and external TCP 25/delivery/restore are all
**Windows Server acceptance required**; none was validated by this audit in Cloud.

Changes reviewed: dedicated directory ACL replacement and path/reparse guards;
stopped IIS deployment with explicit activation; checked existing identities and
bindings; bounded copy retries; reconciled firewall rules; service SID/autostart/
recovery setup; correct `TempMailSmtp` SCM name and executable-relative SMTP logs;
private-key/backup ignore rules; ordered deployment and restore acceptance.

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

## Mailbox / OTP automation validation (2026-10-08)

Baseline: `9182224e8b3e7f34aed3d82be660589abb1f952b` (PR #2 HEAD).
See the [automation API contract](automation-api.md).

Final commands were run sequentially with .NET SDK 10.0.401:

- `dotnet restore`: passed without audit overrides or NU1900.
- `dotnet build -c Release`: passed, 0 warnings and 0 errors.
- `dotnet test -c Release`: 50 unit tests passed; 62 integration tests passed;
  2 existing opt-in tests skipped (SQL Server connection and Chromium smoke
  configuration absent). No tests removed, disabled, or filtered.
- `dotnet ef migrations has-pending-model-changes --project src/TempMail.Infrastructure`:
  no model changes since the last migration. No migration added.
- `git diff --check`: passed.
- Tracked/new-file scan: no private-key files, private-key blocks, or known
  credential-prefix findings; tracked `ExternalApi:Token` remains empty.
  Test credentials and documentation placeholders are nonproduction values.
- Relative README/docs links: all targets exist.

Added 16 OTP/extractor unit cases and 31 integration cases covering header-only
shared authentication, fail-closed configuration, token-free responses/logging,
input/domain/reserved-name validation, normalized idempotency, expiry/recreation,
quota/default lifetime, ten concurrent HTTP creators, independent rate limits,
latest-only selection and deterministic ties, response privacy, and a real TCP
SMTP reject-before-POST / accept-after-POST / GET-code workflow. The existing
SQL Server opt-in test now also exercises ten concurrent automation allocators;
that SQL Server extension was not executed in this environment.

One intermediate full run exposed an intermittent existing
`ApiTests.HealthAndBlazorPageRender` failure: `Home.DisposeAsync` attempts JavaScript
interop during static prerender disposal. The source at that frame is unchanged
from baseline. A separate baseline full run passed, and the final full feature
run also passed; the intermittent issue was not reproduced on baseline and was
not fixed or suppressed in this change. The initial new rate-limit tests also
caught early configuration capture; automation policies now resolve validated
options from DI and all those tests pass.

Network-enabled sandbox invocations were needed for NuGet and VSTest sockets.
The final runs did not encounter VSTest SocketException (13), use `--no-restore`,
or alter project configuration to suppress audit/network failures.

SMTP server, ReceiveService, cleanup, retention, Data Protection, Identity,
existing UI components, and migration/model files have no source diff from the
requested baseline. STARTTLS and anti-relay regression tests remain in the suite.
