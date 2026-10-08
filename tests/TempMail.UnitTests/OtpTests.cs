using TempMail.Application;
using TempMail.Infrastructure;
namespace TempMail.UnitTests;
public sealed class OtpTests
{
    [Theory]
    [InlineData("Verification code: 123456", "123456")]
    [InlineData("OTP: 012345", "012345")]
    [InlineData("1234567", null)]
    [InlineData("99123456", null)]
    [InlineData("123456789", null)]
    [InlineData("111111 then 222222", "111111")]
    [InlineData("", null)]
    public void ExactSixDigits(string text, string? expected) => Assert.Equal(expected, OtpCode.Find(text));

    [Theory]
    [InlineData("<p>Your code is <strong>112233</strong></p>", "112233")]
    [InlineData("<p title='123456' data-code='654321'>No code</p>", null)]
    [InlineData("<script>123456</script><style>.x {color: #654321}</style><p>None</p>", null)]
    [InlineData("<!--123456--><img src='https://example.com/123456'>", null)]
    [InlineData("<template>123456</template><p hidden>654321</p>", null)]
    [InlineData("<p>123</p><p>456</p>", null)]
    [InlineData("<p>&#48;12345</p>", "012345")]
    [InlineData("<p>123<strong>456</strong></p>", "123456")]
    [InlineData("<p>123<strong>4567</strong></p>", null)]
    public void HtmlUsesSafeText(string html, string? expected) => Assert.Equal(expected, OtpCode.Find(new EmailHtml().VisibleText(html)));
}
