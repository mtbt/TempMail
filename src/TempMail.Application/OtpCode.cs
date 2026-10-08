using System.Text.RegularExpressions;
namespace TempMail.Application;
public static partial class OtpCode
{
    [GeneratedRegex(@"(?<!\d)\d{6}(?!\d)", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CodeRegex();
    public static string? Find(string text)
    {
        var match = CodeRegex().Match(text);
        return match.Success ? match.Value : null;
    }
}
