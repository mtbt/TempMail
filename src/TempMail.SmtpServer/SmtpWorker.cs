using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Domain;
using TempMail.Infrastructure;
namespace TempMail.SmtpServer;
public sealed class SmtpWorker(IServiceScopeFactory scopes, IOptions<SmtpOptions> options, ILogger<SmtpWorker> log) : BackgroundService
{
    private readonly SmtpTlsCertificate tlsCertificate = new(options.Value);
    public override void Dispose() { base.Dispose(); tlsCertificate.Dispose(); processing.Dispose(); }
    private readonly SmtpOptions o = options.Value;
    private readonly ConcurrentDictionary<long, Task> clients = new();
    private readonly Dictionary<string, IpBudget> budgets = new();
    private long connections, accepted, sequence;
    private readonly SemaphoreSlim processing = new(options.Value.MaxConcurrentMessages);
    private sealed class IpBudget { public int Connections; public int Messages; public DateTime Window = DateTime.UtcNow; }
    private bool Budget(string ip, bool message, int change = 0)
    {
        lock (budgets)
        {
            var now = DateTime.UtcNow;
            foreach (var key in budgets.Where(x => x.Value.Connections == 0 && x.Value.Window < now.AddMinutes(-1)).Select(x => x.Key).ToArray()) budgets.Remove(key);
            if (!budgets.TryGetValue(ip, out var b))
            {
                if (budgets.Count >= 10000) return false;
                budgets[ip] = b = new IpBudget();
            }
            if (b.Window < now.AddMinutes(-1)) { b.Window = now; b.Messages = 0; }
            if (message) return ++b.Messages <= o.MessagesPerMinutePerIp;
            if (change > 0 && (b.Connections >= o.MaxConnectionsPerIp || clients.Count >= o.MaxConnections)) return false;
            b.Connections += change;
            return true;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var listener = new TcpListener(IPAddress.Parse(o.Host), o.Port);
        listener.Start(o.MaxConnections);
        log.LogInformation("Receive-only SMTP listening on {Host}:{Port}", o.Host, o.Port);
        var heartbeat = HeartbeatAsync(ct);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                var ip = ((IPEndPoint)client.Client.RemoteEndPoint!).Address.ToString();
                if (!Budget(ip, false, 1)) { client.Dispose(); continue; }
                Interlocked.Increment(ref connections);
                var id = Interlocked.Increment(ref sequence);
                var task = HandleAsync(client, ip, ct);
                clients[id] = task;
                _ = task.ContinueWith(_ => { clients.TryRemove(id, out var removed); Budget(ip, false, -1); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { listener.Stop(); await Task.WhenAll(clients.Values); await heartbeat; }
    }
    private async Task HeartbeatAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<MailDbContext>();
                    var status = await db.SmtpStatuses.SingleOrDefaultAsync(x => x.Id == 1, ct);
                    if (status == null) db.SmtpStatuses.Add(status = new SmtpStatus());
                    status.LastHeartbeatAt = DateTime.UtcNow;
                    status.Connections += Interlocked.Exchange(ref connections, 0);
                    status.AcceptedMessages += Interlocked.Exchange(ref accepted, 0);
                    await db.SaveChangesAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogError(ex, "SMTP heartbeat persistence failed"); }
            } while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async Task HandleAsync(TcpClient client, string ip, CancellationToken stoppingToken)
    {
        using (client)
        using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
        {
            lifetime.CancelAfter(TimeSpan.FromSeconds(o.ConnectionLifetimeSeconds));
            var ct = lifetime.Token;
            Stream stream = client.GetStream();
            SslStream? tlsStream = null;
            var reader = new SmtpLineReader(stream);
            async Task Reply(string value) => await stream.WriteAsync(Encoding.ASCII.GetBytes(value + "\r\n"), ct);
            async Task<byte[]?> Read(int max)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(o.CommandTimeoutSeconds));
                return await reader.ReadAsync(max, timeout.Token);
            }
            log.LogInformation("SMTP connected {RemoteIp}", ip);
            try
            {
                await Reply("220 TempMail receive-only ESMTP");
                bool greeted = false, extendedGreeting = false, secure = false;
                string? from = null;
                var recipients = new List<string>();
                while (await Read(512) is { } commandBytes)
                {
                    if (commandBytes.Any(b => b > 127 || b == 0)) { await Reply("500 ASCII commands required"); continue; }
                    var line = Encoding.ASCII.GetString(commandBytes);
                    var split = line.IndexOf(' ');
                    var command = (split < 0 ? line : line[..split]).ToUpperInvariant();
                    var arg = split < 0 ? "" : line[(split + 1)..].Trim();
                    if (o.RequireStartTls && !secure && command is "MAIL" or "RCPT" or "DATA")
                    { await Reply("530 5.7.0 Must issue STARTTLS first"); continue; }
                    switch (command)
                    {
                        case "EHLO": case "HELO":
                            if (arg.Length == 0) { await Reply("501 Hostname required"); break; }
                            if (secure && !greeted && command != "EHLO") { await Reply("503 Send EHLO after STARTTLS"); break; }
                            greeted = true; extendedGreeting = command == "EHLO"; from = null; recipients.Clear();
                            await Reply(command == "EHLO" ? $"250-TempMail\r\n250-SIZE {o.MaxMessageSizeMB * 1024L * 1024}\r\n{(!secure && tlsCertificate.IsAvailable ? "250-STARTTLS\r\n" : "")}250 8BITMIME" : "250 TempMail"); break;
                        case "STARTTLS":
                            if (arg.Length != 0) { await Reply("501 STARTTLS takes no arguments"); break; }
                            if (secure) { await Reply("503 TLS already active"); break; }
                            if (!extendedGreeting) { await Reply("503 Send EHLO first"); break; }
                            if (!tlsCertificate.IsAvailable) { await Reply("454 4.7.0 TLS unavailable"); break; }
                            // RFC 3207: discard all pre-TLS knowledge, including buffered plaintext.
                            greeted = false; extendedGreeting = false; from = null; recipients.Clear();
                            await Reply("220 2.0.0 Ready to start TLS");
                            tlsStream = new SslStream(stream, leaveInnerStreamOpen: false);
                            stream = tlsStream;
                            reader = new SmtpLineReader(stream);
                            using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct))
                            {
                                handshake.CancelAfter(TimeSpan.FromSeconds(o.TlsHandshakeTimeoutSeconds));
                                var watch = System.Diagnostics.Stopwatch.StartNew();
                                log.LogInformation("SMTP TLS handshake started {RemoteIp}", ip);
                                try
                                {
                                    await tlsStream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                                    {
                                        ServerCertificate = tlsCertificate.Certificate,
                                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                                        ClientCertificateRequired = false,
                                        AllowRenegotiation = false
                                    }, handshake.Token);
                                    secure = true;
                                    log.LogInformation("SMTP TLS handshake succeeded {RemoteIp} {Protocol} {CipherSuite} {ElapsedMs}", ip, tlsStream.SslProtocol, tlsStream.NegotiatedCipherSuite, watch.ElapsedMilliseconds);
                                }
                                catch (Exception ex)
                                {
                                    log.LogWarning("SMTP TLS handshake failed {RemoteIp} {ErrorType} {ElapsedMs}", ip, ex.GetType().Name, watch.ElapsedMilliseconds);
                                    return; // Never send plaintext or resume SMTP after a failed handshake.
                                }
                            }
                            break;
                        case "NOOP": await Reply("250 OK"); break;
                        case "QUIT": await Reply("221 Bye"); return;
                        case "RSET": from = null; recipients.Clear(); await Reply("250 Reset"); break;
                        case "MAIL":
                            if (!greeted) { await Reply("503 Send EHLO first"); break; }
                            from = null; recipients.Clear();
                            var sender = ParsePath(arg, "FROM:", true);
                            if (sender == null) { await Reply("501 Invalid reverse path"); break; }
                            var parameters = arg[(arg.IndexOf('>') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            if (parameters.Any(p => !p.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase) && !p.Equals("BODY=8BITMIME", StringComparison.OrdinalIgnoreCase) && !p.Equals("BODY=7BIT", StringComparison.OrdinalIgnoreCase))) { await Reply("555 Unsupported MAIL parameter"); break; }
                            var sizeParameter = parameters.FirstOrDefault(p => p.StartsWith("SIZE=", StringComparison.OrdinalIgnoreCase));
                            if (sizeParameter != null && (!long.TryParse(sizeParameter[5..], out var declaredSize) || declaredSize < 0 || declaredSize > o.MaxMessageSizeMB * 1024L * 1024)) { await Reply("552 Invalid or excessive message size"); break; }
                            await using (var scope = scopes.CreateAsyncScope())
                            {
                                if (await scope.ServiceProvider.GetRequiredService<ReceiveService>().SenderBlockedAsync(sender, ct)) { await Reply("550 Sender rejected"); break; }
                            }
                            from = sender;
                            log.LogInformation("SMTP MAIL FROM {Sender} {RemoteIp}", sender, ip);
                            await Reply("250 OK"); break;
                        case "RCPT":
                            if (from == null) { await Reply("503 Send MAIL first"); break; }
                            if (recipients.Count >= o.MaxRecipients) { await Reply("452 Too many recipients"); break; }
                            var recipient = ParsePath(arg, "TO:", false);
                            if (recipient == null) { await Reply("501 Invalid recipient"); break; }
                            await using (var scope = scopes.CreateAsyncScope())
                            {
                                var error = await scope.ServiceProvider.GetRequiredService<ReceiveService>().ValidateRecipientAsync(recipient, ct);
                                log.LogInformation("SMTP RCPT TO {Recipient} {Result} {RemoteIp}", recipient, error ?? "accepted", ip);
                                if (error != null) { await Reply(error); break; }
                            }
                            if (!recipients.Contains(recipient)) recipients.Add(recipient);
                            await Reply("250 OK"); break;
                        case "DATA":
                            if (from == null || recipients.Count == 0) { await Reply("503 Recipient required"); break; }
                            if (arg.Length != 0) { await Reply("501 DATA takes no arguments"); break; }
                            if (!Budget(ip, true)) { await Reply("421 Message rate exceeded"); return; }
                            if (!await processing.WaitAsync(0, ct)) { await Reply("452 Receiver busy; retry later"); from = null; recipients.Clear(); break; }
                            try
                            {
                            await Reply("354 End with <CRLF>.<CRLF>");
                            using (var data = new MemoryStream())
                            {
                                while (true)
                                {
                                    var bytes = await Read(1000);
                                    if (bytes == null) return;
                                    if (bytes.Length == 1 && bytes[0] == '.') break;
                                    var offset = bytes.Length > 0 && bytes[0] == '.' ? 1 : 0;
                                    if (data.Length + bytes.Length - offset + 2 > o.MaxMessageSizeMB * 1024L * 1024) { await Reply("552 Message too large"); return; }
                                    data.Write(bytes, offset, bytes.Length - offset); data.Write("\r\n"u8);
                                }
                                var watch = System.Diagnostics.Stopwatch.StartNew();
                                try
                                {
                                    await using var scope = scopes.CreateAsyncScope();
                                    await scope.ServiceProvider.GetRequiredService<ReceiveService>().ReceiveAsync(from, recipients, data.ToArray(), ct);
                                    Interlocked.Increment(ref accepted);
                                    await Reply("250 Message accepted");
                                    log.LogInformation("SMTP stored {Size} bytes {Recipients} recipients in {ElapsedMs} ms", data.Length, recipients.Count, watch.ElapsedMilliseconds);
                                }
                                catch (MailPolicyException ex) { await Reply(ex.StatusCode == 507 ? "452 Storage quota exceeded" : "554 Message rejected by policy"); log.LogWarning("SMTP message rejected by policy {RemoteIp}", ip); }
                                catch (FormatException) { await Reply("554 Malformed MIME message"); }
                                catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogError("SMTP storage failed ({ErrorType}); sender must retry", ex.GetType().Name); await Reply("451 Temporary storage failure"); }
                            }
                            from = null; recipients.Clear();
                            }
                            finally { processing.Release(); }
                            break;
                        default: await Reply("502 Command not implemented"); break;
                    }
                }
            }
            catch (InvalidDataException) { await Reply("500 Line too long or invalid framing"); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { log.LogInformation("SMTP connection timed out or stopped {RemoteIp}", ip); }
            catch (IOException) { log.LogInformation("SMTP peer disconnected {RemoteIp}", ip); }
            catch (Exception ex) { log.LogError("SMTP session failed {ErrorType} {RemoteIp}", ex.GetType().Name, ip); }
            finally { tlsStream?.Dispose(); log.LogInformation("SMTP disconnected {RemoteIp}", ip); }
        }
    }
    public static string? ParsePath(string arg, string prefix, bool allowEmpty)
    {
        if (!arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var value = arg[prefix.Length..].Trim();
        if (!value.StartsWith('<')) return null;
        var close = value.IndexOf('>');
        if (close < 1 || (!allowEmpty && !string.IsNullOrWhiteSpace(value[(close + 1)..]))) return null;
        var address = value[1..close].ToLowerInvariant();
        if (address.Length == 0) return allowEmpty ? "" : null;
        if (address.Length > 320 || address.Any(c => c <= 32 || c >= 127 || c is '<' or '>') || address.Count(c => c == '@') != 1) return null;
        return address;
    }
}
internal sealed class SmtpLineReader(Stream stream)
{
    private readonly byte[] buffer = new byte[4096];
    private int position, count;
    public async Task<byte[]?> ReadAsync(int limit, CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (position == count) { count = await stream.ReadAsync(buffer, ct); position = 0; if (count == 0) return null; }
            var b = buffer[position++];
            if (b == 10)
            {
                var bytes = line.ToArray();
                if (bytes.Length == 0 || bytes[^1] != 13) throw new InvalidDataException("CRLF required.");
                return bytes[..^1];
            }
            line.WriteByte(b);
            if (line.Length > limit) throw new InvalidDataException("Line too long.");
        }
    }
}
