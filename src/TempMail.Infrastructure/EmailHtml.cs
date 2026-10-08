using Ganss.Xss;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using System.Text;
namespace TempMail.Infrastructure;
public sealed class EmailHtml
{
    public string VisibleText(string html)
    {
        // Parse inertly: no browsing context, scripting, or resource loader is enabled.
        var parser = new HtmlParser();
        using var source = parser.ParseDocument(html);
        foreach (var hidden in source.QuerySelectorAll("script,style,template,noscript,head,[hidden],[aria-hidden=true]")) hidden.Remove();
        using var safe = parser.ParseDocument(Sanitize(source.Body?.InnerHtml ?? ""));
        var text = new StringBuilder();
        if (safe.Body != null) AppendText(safe.Body, text);
        return text.ToString();
    }
    private static void AppendText(INode node, StringBuilder text)
    {
        if (node is IText value) { text.Append(value.Data); return; }
        var block = node is IElement e && e.LocalName is "p" or "br" or "div" or "blockquote" or "pre" or "ul" or "ol" or "li" or "table" or "thead" or "tbody" or "tr" or "td" or "th" or "hr" or "h1" or "h2" or "h3" or "h4";
        if (block) text.Append('\n');
        foreach (var child in node.ChildNodes) AppendText(child, text);
        if (block) text.Append('\n');
    }
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
