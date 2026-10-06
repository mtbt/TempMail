using Ganss.Xss;
namespace TempMail.Infrastructure;
public sealed class EmailHtml
{
    // A small allowlist: no CSS, forms, navigation, SVG, remote fonts, or active content.
    public string Sanitize(string html, bool externalImages = false, bool preserveCid = false)
    {
        var s = new HtmlSanitizer();
        s.AllowedTags.Clear();
        foreach (var tag in new[] { "p", "br", "div", "span", "b", "strong", "i", "em", "u", "blockquote", "pre", "code", "ul", "ol", "li", "table", "thead", "tbody", "tr", "td", "th", "hr", "h1", "h2", "h3", "h4" }) s.AllowedTags.Add(tag);
        s.AllowedAttributes.Clear();
        s.AllowedCssProperties.Clear();
        s.AllowedSchemes.Clear();
        if (externalImages || preserveCid)
        {
            s.AllowedTags.Add("img");
            s.AllowedAttributes.Add("src");
            s.AllowedAttributes.Add("alt");
            if (externalImages) s.AllowedSchemes.Add("https");
            if (preserveCid) s.AllowedSchemes.Add("cid");
            s.FilterUrl += (_, e) =>
            {
                if (preserveCid && e.OriginalUrl.StartsWith("cid:", StringComparison.OrdinalIgnoreCase)) return;
                if (!externalImages || !Uri.TryCreate(e.OriginalUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo)) e.SanitizedUrl = "";
            };
        }
        return s.Sanitize(html);
    }
}
