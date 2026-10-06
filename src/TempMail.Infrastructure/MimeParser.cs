using Microsoft.Extensions.Options;
using MimeKit;
using TempMail.Application;
namespace TempMail.Infrastructure;
public sealed record ParsedAttachment(string FileName, string ContentType, string? ContentId, byte[] Bytes);
public sealed record ParsedEmail(string MessageId, string From, string FromName, string To, string Cc, string Subject, string Text, string Html, string Headers, List<ParsedAttachment> Attachments);
public sealed class MimeParser(IOptions<SmtpOptions> options, EmailHtml html)
{
    public async Task<ParsedEmail> ParseAsync(byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length > options.Value.MaxMessageSizeMB * 1024L * 1024) throw new MailPolicyException("Message too large.");
        // Bound MIME structural expansion before MimeKit allocates its entity tree.
        var boundaryLines = 0;
        for (var i = 0; i + 3 < bytes.Length; i++)
            if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == '-' && bytes[i + 3] == '-' && ++boundaryLines > 1000)
                throw new MailPolicyException("Message has too many MIME boundary-like lines.");
        using var input = new MemoryStream(bytes, false);
        using var message = await MimeMessage.LoadAsync(new ParserOptions { MaxMimeDepth = 30, MaxAddressGroupDepth = 10 }, input, ct);
        var attachments = new List<ParsedAttachment>();
        long decodedSize = 0;
        foreach (var entity in message.BodyParts.Where(x => x.IsAttachment || x.ContentId != null || x is MessagePart))
        {
            if (attachments.Count >= 100) throw new MailPolicyException("Too many attachments.");
            await using var content = new LimitedMemoryStream(options.Value.MaxAttachmentSizeMB * 1024L * 1024);
            if (entity is MimePart { Content: not null } part) await part.Content.DecodeToAsync(content, ct);
            else if (entity is MessagePart { Message: not null } nested) await nested.Message.WriteToAsync(content, ct);
            else continue;
            decodedSize += content.Length;
            if (decodedSize > options.Value.MaxMessageSizeMB * 1024L * 1024) throw new MailPolicyException("Decoded attachments too large.");
            var fileName = entity.ContentDisposition?.FileName ?? entity.ContentType.Name ?? "attachment";
            fileName = new string(fileName.Replace('\\', '/').Split('/').Last().Where(c => !char.IsControl(c)).Take(200).ToArray());
            attachments.Add(new(string.IsNullOrWhiteSpace(fileName) ? "attachment" : fileName, entity.ContentType.MimeType, entity.ContentId, content.ToArray()));
        }
        var sender = message.From.Mailboxes.FirstOrDefault();
        return new(message.MessageId ?? "", sender?.Address ?? "", sender?.Name ?? "", message.To.ToString(), message.Cc.ToString(), message.Subject ?? "", message.TextBody ?? "", html.Sanitize(message.HtmlBody ?? "", true, preserveCid: true), message.Headers.ToString() ?? "", attachments);
    }
}
internal sealed class LimitedMemoryStream(long limit) : MemoryStream
{
    private void Check(int count) { if (Length + count > limit) throw new MailPolicyException("Attachment too large."); }
    public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) { Check(count); return base.WriteAsync(buffer, offset, count, ct); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { Check(buffer.Length); return base.WriteAsync(buffer, ct); }
}
