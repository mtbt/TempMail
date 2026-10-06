using AngleSharp.Html.Parser;
using TempMail.Domain;
namespace TempMail.Infrastructure;
public sealed class EmailRenderer(EmailHtml html, AttachmentStorage storage)
{
    public async Task<string> RenderAsync(MailMessage message, bool externalImages, CancellationToken ct)
    {
        var document = await new HtmlParser().ParseDocumentAsync(html.Sanitize(message.HtmlBody, externalImages, preserveCid: true), ct);
        foreach (var image in document.QuerySelectorAll("img"))
        {
            var src = image.GetAttribute("src") ?? "";
            if (string.IsNullOrWhiteSpace(src)) { image.Remove(); continue; }
            if (!src.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) { if (!externalImages) image.Remove(); continue; }
            var id = Uri.UnescapeDataString(src[4..]);
            var attachment = message.Attachments.FirstOrDefault(x => string.Equals(x.ContentId, id, StringComparison.Ordinal));
            if (attachment == null || attachment.ContentType is not ("image/png" or "image/jpeg" or "image/gif" or "image/webp")) { image.Remove(); continue; }
            byte[] bytes;
            try { bytes = await File.ReadAllBytesAsync(storage.GetPath(attachment.StorageId), ct); }
            catch (FileNotFoundException) { image.Remove(); continue; }
            if (!IsRaster(bytes, attachment.ContentType)) { image.Remove(); continue; }
            // Only vetted raster types receive a data URL after sanitization; arbitrary data/SVG remains blocked.
            image.SetAttribute("src", "data:" + attachment.ContentType + ";base64," + Convert.ToBase64String(bytes));
        }
        return document.Body?.InnerHtml ?? "";
    }
    public static bool IsRaster(ReadOnlySpan<byte> bytes, string type) => type switch
    {
        "image/png" => bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
        "image/jpeg" => bytes.StartsWith(new byte[] { 255, 216, 255 }),
        "image/gif" => bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8),
        "image/webp" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false
    };
}
