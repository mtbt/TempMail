using System.Text.RegularExpressions;
namespace TempMail.Application;
public static partial class OtpCode
{
    // Fixed-width matching; Unicode decimal digits also block numeric substrings.
    // NonBacktracking does not support the boundary lookarounds.
    [GeneratedRegex(@"(?<!\p{Nd})[0-9]{6}(?!\p{Nd})", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CodeRegex();
    public static string? Find(string text)
    {
        var match = CodeRegex().Match(text);
        return match.Success ? match.Value : null;
    }
}
