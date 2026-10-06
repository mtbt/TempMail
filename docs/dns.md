# DNS and Internet reachability

Example documentation IP `203.0.113.10` must be replaced with your public IPv4 address.

| Type | Record | Value | Priority |
|---|---|---|---|
| A | tempmail.example.com | 203.0.113.10 | — |
| A | mail.example.com | 203.0.113.10 | — |
| MX | mail.example.com | mail.example.com | 10 |

MX points to a **hostname, never an IP address**. Create the MX at the domain appearing after `@`, not automatically at the apex `example.com`. Each configured mail domain needs its own MX. MX targets should have A/AAAA records, not be CNAME aliases. Publish AAAA only if IPv6 SMTP works and the service listens on IPv6.

Cloudflare: `mail.example.com` and every MX target must be **DNS Only** (gray cloud). The standard HTTP proxy does not carry SMTP. The website can be proxied separately, but do not trust arbitrary X-Forwarded-For values; configure known proxies before using their client IPs for quotas.

Receive-only servers do not need DKIM signing, outbound SMTP or SMTP AUTH. You can publish `v=spf1 -all` for mail domains to discourage forged outbound use, with a suitable DMARC policy. This does not authenticate inbound senders or stop spoofing. Never create an outbound relay as a reachability workaround.

## Verify from outside your server/network

```powershell
nslookup tempmail.example.com
nslookup mail.example.com
nslookup -type=mx mail.example.com
Test-NetConnection mail.example.com -Port 25
Test-NetConnection tempmail.example.com -Port 443
```

Check public DNS propagation and authoritative DNS if answers differ. Some VPS/cloud providers block inbound and/or outbound port 25. This application requires **inbound TCP 25 from the Internet**. A successful localhost test or an outbound port test does not prove inbound access. Ask the provider to unblock inbound 25 if necessary. DNS cannot specify an alternative SMTP port; 2525 is only for local development.

Checklist: SMTP service running; bind address correct; Windows firewall; provider firewall/security group; NAT port forwarding where applicable; public A record; correct MX; no Cloudflare proxy; external TCP probe; external SMTP dialogue; mailbox created and not expired; then receive a test email from Gmail/Outlook.
