using System.Text;
using Microsoft.Extensions.Options;
using TempMail.Application;
using TempMail.Infrastructure;
using TempMail.SmtpServer;
namespace TempMail.UnitTests;
public sealed class PolicyTests
{
    [Theory]
    [InlineData("namhoang", true)] [InlineData("a.b-c_d", true)] [InlineData("ab", false)]
    [InlineData("a..bc", false)] [InlineData(".abc", false)] [InlineData("abc.", false)]
    [InlineData("abc/xyz", false)] [InlineData("abc<script>", false)] [InlineData("tên", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    public void LocalPartValidation(string value, bool expected) => Assert.Equal(expected, AddressPolicy.IsValidLocal(value));
    [Theory] [InlineData("ADMIN")] [InlineData("root")] [InlineData("Postmaster")] [InlineData("support")]
    public void ReservedNamesAreCaseInsensitive(string name) => Assert.True(AddressPolicy.IsReserved(name, new TempMailOptions().ReservedNames));
    [Fact] public void CustomReservedName() => Assert.True(AddressPolicy.IsReserved("billing", ["billing"]));
    [Fact] public void NormalizeAddress() => Assert.Equal("namhoang", AddressPolicy.NormalizeLocal(" NamHoang "));
    [Fact] public void RandomAddressesAreValidAndDistinct()
    {
        var generator = new EmailAddressGenerator(); var values = Enumerable.Range(0, 1000).Select(_ => generator.Generate()).ToArray();
        Assert.Equal(1000, values.Distinct().Count()); Assert.All(values, value => Assert.True(AddressPolicy.IsValidLocal(value)));
    }
    [Fact] public void TokensAreRandomAndHashed() { var a = Tokens.Create(); var b = Tokens.Create(); Assert.NotEqual(a, b); Assert.Equal(64, a.Length); Assert.True(Tokens.Matches(a, Tokens.Hash(a))); Assert.False(Tokens.Matches(b, Tokens.Hash(a))); }
    [Theory]
    [InlineData("<script>alert(1)</script><p onclick='x()'>Hello</p>")]
    [InlineData("<iframe src='https://evil.test'></iframe><object data='x'></object><embed src='x'><form><input></form>")]
    [InlineData("<svg onload='x()'><a href='javascript:x()'>test</a></svg>")]
    [InlineData("<img src='https://tracking.example/pixel' onerror='x()'><div style='background:url(https://evil.test)'>x</div>")]
    public void SanitizerBlocksActiveContentAndTracking(string input)
    {
        var output = new EmailHtml().Sanitize(input);
        foreach (var forbidden in new[] { "<script", "<iframe", "<object", "<embed", "<form", "<svg", "<img", "onclick", "onerror", "onload", "javascript:", "style=", "https://" }) Assert.DoesNotContain(forbidden, output, StringComparison.OrdinalIgnoreCase);
    }
    [Fact] public void ExternalImagesRequireHttpsAndExplicitOptIn()
    {
        var h = new EmailHtml(); var value = "<img src='https://example.com/image'><img src='javascript:alert(1)'><img src='//example.com/pixel'><img src='data:image/svg+xml,evil'>";
        Assert.DoesNotContain("<img", h.Sanitize(value)); var allowed = h.Sanitize(value, true);
        Assert.Contains("https://example.com/image", allowed); Assert.DoesNotContain("javascript:", allowed); Assert.DoesNotContain("data:", allowed); Assert.DoesNotContain("src=\"//", allowed);
    }
    [Theory] [InlineData("../secret")] [InlineData("..\\secret")] [InlineData("/etc/passwd")] [InlineData("C:\\secret")] [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/")]
    public void PathTraversalRejected(string value)
    {
        var storage = new AttachmentStorage(Options.Create(new TempMailOptions { StoragePath = Path.Combine(Path.GetTempPath(), "tempmail-unit") }));
        Assert.Throws<MailPolicyException>(() => storage.GetPath(value));
    }
    [Fact] public async Task MimeUnicodeAndAttachmentParsing()
    {
        var parser = new MimeParser(Options.Create(new SmtpOptions()), new EmailHtml());
        var raw = "From: Test <sender@example.com>\r\nTo: abc@mail.example.com\r\nCc: copy@example.com\r\nSubject: =?UTF-8?B?WGluIGNow6Bv?=\r\nMIME-Version: 1.0\r\nContent-Type: multipart/mixed; boundary=foo\r\n\r\n--foo\r\nContent-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\nXin ch=C3=A0o\r\n--foo\r\nContent-Type: application/octet-stream\r\nContent-Disposition: attachment; filename=\"../../hello.txt\"\r\nContent-Transfer-Encoding: base64\r\nContent-ID: <abc>\r\n\r\naGVsbG8=\r\n--foo--\r\n";
        var result = await parser.ParseAsync(Encoding.UTF8.GetBytes(raw), default);
        Assert.Equal("Xin chào", result.Subject); Assert.Contains("Xin chào", result.Text); Assert.Contains("copy@example.com", result.Cc);
        var part = Assert.Single(result.Attachments); Assert.Equal("hello.txt", part.FileName); Assert.Equal("hello", Encoding.UTF8.GetString(part.Bytes)); Assert.Equal("abc", part.ContentId);
    }
    [Fact] public async Task OversizedMimeIsRejected() => await Assert.ThrowsAsync<MailPolicyException>(() => new MimeParser(Options.Create(new SmtpOptions { MaxMessageSizeMB = 1 }), new EmailHtml()).ParseAsync(new byte[1024 * 1024 + 1], default));
    [Theory] [InlineData("FROM:<>", "")] [InlineData("FROM:<Sender@Example.com> SIZE=12", "sender@example.com")] [InlineData("FROM:bad", null)] [InlineData("FROM:<a@b@c>", null)]
    public void SmtpEnvelopeParsing(string input, string? expected) => Assert.Equal(expected, SmtpWorker.ParsePath(input, "FROM:", true));
}
