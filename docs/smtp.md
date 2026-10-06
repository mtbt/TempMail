# Receive-only SMTP

Implemented commands: EHLO, HELO, MAIL FROM, RCPT TO, DATA, RSET, NOOP, QUIT. EHLO advertises SIZE and 8BITMIME. AUTH, STARTTLS, VRFY, EXPN, forwarding and sending are not implemented. Port 25 in production; use 127.0.0.1:2525 for local development. RFC CRLF and dot-stuffing are handled. Commands/data lines are bounded (512/1000 bytes), with command and connection lifetime limits.

SMTP STARTTLS is not present in this version. Public SMTP can fall back to plaintext; senders requiring transport encryption will not deliver. If inbound TLS is a deployment requirement, place a vetted receive-only SMTP gateway with TLS in front, preserving local-only recipient validation and no outbound relay, or implement/test STARTTLS as a separate change. HTTPS protects the browser, not the SMTP hop. This limitation must be accepted explicitly by the operator before deployment.

Responses: 550 Relay denied for non-local domains; 550 Mailbox unavailable for missing/expired/blocked names; 452 quota/recipient pressure; 552 oversize DATA; 554 MIME/policy rejection; 451 transient persistence failure. A success 250 is sent only after durable database commit. If a connection drops after commit but before 250 reaches the sender, the sender may retry and duplicate mail; the application does not discard mail solely based on potentially forged Message-ID.

`RejectUnknownMailbox=false` means accept-and-discard unknown recipients in active local domains. It never creates unowned inboxes and never relays. Create the mailbox in the browser before sending a test. Reserved addresses remain rejected. This is a temporary-mail receiver, not a general-purpose RFC postmaster service.

## Test

```powershell
Test-NetConnection mail.example.com -Port 25
# Enable Telnet Client separately if desired:
Install-WindowsFeature Telnet-Client
telnet mail.example.com 25
```

Conversation (create `namhoang@mail.example.com` first):

```text
S: 220 TempMail receive-only ESMTP
C: EHLO test.com
S: 250-TempMail
S: 250-SIZE 20971520
S: 250 8BITMIME
C: MAIL FROM:<sender@example.com>
S: 250 OK
C: RCPT TO:<namhoang@mail.example.com>
S: 250 OK
C: DATA
S: 354 End with <CRLF>.<CRLF>
C: From: sender@example.com
C: To: namhoang@mail.example.com
C: Subject: Test
C:
C: Hello Temp Mail
C: .
S: 250 Message accepted
C: QUIT
S: 221 Bye
```

Negative checks: RCPT outside configured domains must return 550; unknown mailbox must return 550 by default; AUTH must return 502; DATA before RCPT must return 503. Integration tests exercise a real TCP socket, parser, database and inbox API.

Limits: connections globally/per-IP, messages per minute/IP, maximum recipients, message bytes, attachment bytes/count (100), mailbox count/IP, messages/mailbox and total logical message storage. DATA/parse/storage concurrency is limited separately (default 4), returning 452 under pressure. MIME depth is limited to 30 and more than 1000 boundary-like lines are rejected before parsing. Data is bounded in memory; size memory for MaxConcurrentMessages × MaxMessageSizeMB plus parser/body/storage overhead. Use lower limits for a small VPS. SMTP logs include remote IP/envelope sender/recipient/result/bytes/duration, never body, tokens or cookies. Apply log retention/ACLs as these logs contain personal metadata.
