# Receive-only SMTP

Implemented commands: EHLO, HELO, STARTTLS, MAIL FROM, RCPT TO, DATA, RSET, NOOP, QUIT. EHLO advertises SIZE and 8BITMIME, plus STARTTLS only before TLS when a configured certificate is usable and within its validity period. AUTH, VRFY, EXPN, forwarding and sending are not implemented. Port 25 in production; use 127.0.0.1:2525 for local development. RFC CRLF and dot-stuffing are handled. Commands/data lines are bounded (512/1000 bytes), with command and connection lifetime limits.

## TLS configuration

Configure the SMTP process, independently of IIS HTTPS. Prefer Windows `LocalMachine\My` under the actual Windows Service identity:

```json
{
  "Smtp": {
    "RequireStartTls": false,
    "TlsHandshakeTimeoutSeconds": 15,
    "Tls": {
      "ServerName": "mail.example.com",
      "CertificateThumbprint": "<issued certificate thumbprint>",
      "StoreName": "My",
      "StoreLocation": "LocalMachine",
      "PfxPath": null,
      "PfxPassword": null
    }
  }
}
```

`ServerName` is the DNS name in the certificate SAN (normally the MX target). `StoreLocation` accepts `LocalMachine` or `CurrentUser`; the latter refers to the service account's profile, not the administrator. Whitespace in thumbprints is ignored. Grant private-key read access to the SMTP service identity. Only one source may be configured. Alternatively leave `CertificateThumbprint` null, set an absolute `PfxPath` outside web roots, and inject `Smtp__Tls__PfxPassword` from protected service configuration/secret management. PFX keys load ephemerally. Do not put passwords in Git, command-line arguments, or logs. Environment names use `__`, for example `Smtp__Tls__CertificateThumbprint`.

No certificate and `RequireStartTls=false` preserves plaintext receipt. Explicit invalid configuration fails startup, even in optional mode: missing/unreadable file/store match, ambiguous sources, missing private key, expired/not-yet-valid certificate, hostname mismatch, unsuitable EKU/key usage or inaccessible private key. Startup exercises RSA/ECDSA signing under the process identity. Trust-chain/revocation and public CA issuance are operator/client checks; a self-signed certificate can pass local suitability checks but will not be trusted by Internet clients. Install intermediate certificates in the OS store and verify the served chain externally. Certificates are loaded once; restart SMTP after renewal or configuration changes. Monitor expiry: an expired loaded certificate stops being advertised/accepted for new upgrades.

TLS permits only TLS 1.2 and TLS 1.3, with the highest mutually supported version selected by the OS. TLS 1.3 requires OS support; OS cipher policy applies. SSLv3, TLS 1.0/1.1 and renegotiation are not enabled. No client certificate or SMTP AUTH is required, and TLS grants no relay privileges.

STARTTLS requires EHLO and no arguments: 503 for wrong sequence/already encrypted, 501 for arguments, 454 if unavailable. After the 220 reply, the connection becomes TLS-only. All greeting/envelope/recipient state and buffered plaintext are discarded; the client must complete the handshake and send a new EHLO (HELO is insufficient). EHLO inside TLS does not advertise STARTTLS. Failed, disconnected or timed-out handshakes close the connection without plaintext fallback. The handshake deadline defaults to 15 seconds and is also bounded by the original connection lifetime; upgrading never resets connection/rate budgets.

`RequireStartTls=true` requires a valid certificate at startup and returns `530 5.7.0` for MAIL/RCPT/DATA before TLS. Its default is false because requiring TLS on a publicly referenced port-25 receiver prevents delivery from some Internet MTAs (RFC 3207). Optional STARTTLS cannot prevent an on-path attacker stripping STARTTLS before negotiation; authenticated transport enforcement such as MTA-STS/DANE is outside this change. Once upgraded, this receiver never downgrades the session. HTTPS protects the browser, not the SMTP hop.

Handshake logs contain start/success/failure, remote IP, elapsed time, negotiated protocol/cipher on success and exception type on failure. They do not log certificate passwords, keys, handshake bytes or exception details.

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

## TLS acceptance checks

With OpenSSL available on a separate test host:

```bash
openssl s_client -starttls smtp -connect mail.example.com:25 -servername mail.example.com -verify_hostname mail.example.com -verify_return_error -tls1_2
openssl s_client -starttls smtp -connect mail.example.com:25 -servername mail.example.com -verify_hostname mail.example.com -verify_return_error -tls1_3
```

After the handshake send `EHLO test.example`, then test a local mailbox and non-local RCPT (550). Test TLS 1.0/1.1 with a client actually capable of offering them and confirm the server refuses; a local client-policy error alone is not proof. In staging, test missing key ACL, expired/wrong-host certificates, handshake timeout, service restart/renewal, and `RequireStartTls` both ways. Telnet remains useful only for plaintext/advertisement checks; it cannot complete TLS. Automated integration tests use generated short-lived certificates with exact certificate pinning, real TCP/SslStream handshakes, state reset, buffered-command injection, failure/timeout, encrypted delivery, relay rejection and optional plaintext delivery.
