# Mailbox / OTP automation API

These privileged endpoints use only the `X-Api-Token` HTTP header. They do not
require Identity login, cookies, CSRF tokens, or mailbox access tokens. Possession
of this shared key permits mailbox creation and OTP reads across mailboxes.
Use HTTPS and restrict access to the protected configuration file.

## Configuration

The tracked Web `appsettings.json` contains `"ExternalApi": { "Token": "" }`.
Set a random production secret in the Web host's ignored, ACL-protected
`appsettings.Local.json`, or use the existing environment override
`ExternalApi__Token`. For example (placeholder only):

```json
{ "ExternalApi": { "Token": "REPLACE_WITH_RANDOM_SECRET" } }
```

Missing, null, empty, or whitespace configuration disables access: both routes
return 401. Missing, incorrect, or multiple token headers also return 401.
Query-string tokens are not accepted. Tokens are never stored in the database,
returned in responses, or added to application logs. Do not configure a reverse
proxy or HTTP diagnostics to record this header. Comparison reuses `Tokens`:
SHA-256 hashes are represented as fixed-length 64-byte ASCII values and compared
using `CryptographicOperations.FixedTimeEquals`.

## Required workflow

1. Call `POST /api/mailbox` with the desired address.
2. Wait for a successful response.
3. Ask the external service to send its OTP.
4. Wait for SMTP to receive and persist the email.
5. Poll `GET /api/latest-code` within the read rate limit.
6. Use the OTP when `code != null`.

If the OTP is sent before POST creates the mailbox, SMTP can reject the unknown
recipient. Creating the mailbox afterwards cannot recover the rejected message;
request a new OTP after creation succeeds. SMTP still uses
`RejectUnknownMailbox = true` and never auto-creates mailboxes.

```sh
curl -X POST https://tempmail.example.com/api/mailbox \
  -H "X-Api-Token: YOUR_TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"email":"abc@example.com"}'

curl "https://tempmail.example.com/api/latest-code?email=abc@example.com" \
  -H "X-Api-Token: YOUR_TOKEN"
```

`YOUR_TOKEN` is a placeholder. Provision the real token through protected local
configuration/environment; avoid putting real secrets in shell history.
`example.com` must be replaced by an existing enabled receiving domain.

## Contracts and lifecycle

| Route | Request | Success |
| --- | --- | --- |
| `POST /api/mailbox` | JSON `{ "email": "abc@example.com" }` | New: 201 `{ "email": "abc@example.com", "created": true }`; active existing: 200 with `created: false` |
| `GET /api/latest-code?email=abc@example.com` | Email query parameter | 200 `{ "code": "012345" }` or `{ "code": null }` |

Both normalize outer whitespace and casing, validate the existing local-part
syntax (3–40 ASCII characters) and DNS domain syntax, and return 400 for invalid
email input. POST reuses the UI allocation service, enabled-domain lookup,
custom-address setting, reserved/blocked-name checks, IP quota, and configured
mailbox lifetime. It does not create domains. Missing/disabled POST domains return
400. Existing active mailboxes are returned without changing access, ownership,
expiry, or counting as a new mailbox against the caller's quota. They must still
pass the current address/domain rules. The response contains no access secret.

Expired rows reserve their address until existing cleanup removes them: POST
returns 409 while such a row remains. A deleted/cleaned-up address can be created
again using the same rules. GET returns 404 for absent or expired mailboxes and
never creates one. Existing message retention applies; expired messages are
excluded, as in the inbox UI. A live mailbox with no retained message returns
`code: null`. Quota exhaustion returns 429.

Allocation uses the existing serializable transaction and, on SQL Server, the
transaction-owned `TempMail.MailboxAllocation` application lock shared with UI
creators. The existing unique address index remains the final database safeguard.
An address-index collision on SQL Server is rolled back and the active winner
is read again; unrelated automation database errors are not swallowed. SQLite
tests use the provider's serializable write transaction. No schema changes or
migrations are needed.

## Latest message and extraction

Only one retained message is selected with server-generated `ReceivedAt DESC,
Id DESC`. The sender's Date header does not choose the latest message. The query
projects only Subject, TextBody, and HtmlBody; it does not load attachments or
an entire inbox. There is no fallback to an older message without an OTP in the
latest selected message.

The first match of `(?<!\p{Nd})[0-9]{6}(?!\p{Nd})` wins, in this order: subject,
plain text, then sanitized HTML text. Codes contain exactly six ASCII digits and
remain strings, preserving leading zeroes. Adjacent Unicode decimal digits also
block a match: `١123456` and `123456７` return no code. Unicode-only codes such as
`１２３４５６` are ignored; `１２３４５６ then 123456` yields `123456`.
HTML is parsed inertly with the existing AngleSharp dependency and `EmailHtml`
sanitizer. Scripts, styles, templates, comments, attributes, and hidden elements
are not searched; HTML entities are decoded and block boundaries separate text.
No browser, JavaScript, or external resources run. Text corresponds to the
sanitized HTML, not a CSS/layout rendering of the original email.

Before sanitization strips attributes on MIME ingestion, entire subtrees marked
`hidden` (regardless of its value) or `aria-hidden="true"` (case-insensitive,
ignoring surrounding whitespace) are removed. Visible siblings remain intact;
`aria-hidden="false"` remains visible. This does not attempt CSS layout detection.
The guarantee applies to mail received after deploying the fix to the SMTP host.
Previously stored sanitized HTML may already have lost hidden markers; their
original visibility cannot be recovered reliably. Existing mail is not rewritten
or heuristically filtered, and no schema change or migration is required.

Responses contain only the documented fields and use `Cache-Control: no-store`.
No sender, body, subject, message metadata, attachment, Data Protection token, or
mailbox access token is returned.

## Limits and regression scope

The built-in fixed-window limiter uses separate `automation-create` and
`automation-read` policies per client IP, with
`TempMail:CreateRequestsPerMinute` and `TempMail:ReadRequestsPerMinute` respectively
(defaults 10 and 120). No queuing; rejected requests receive 429. Authentication
runs before these endpoint limiters. SMTP is unaffected by HTTP rate limiting.

STARTTLS, TLS/certificates, fresh EHLO and envelope reset, anti-relay, receive-only
SMTP, lack of AUTH/outbound delivery, cleanup, retention, Data Protection, admin
login, UI CSRF protection, and message rendering remain unchanged.

See [security](security.md), [SMTP](smtp.md), and [validation](validation.md).
