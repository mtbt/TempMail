using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
namespace TempMail.Application;
public interface IEmailAddressGenerator { string Generate(); }
public sealed class EmailAddressGenerator : IEmailAddressGenerator
{
    public string Generate() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
}
public static partial class AddressPolicy
{
    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{1,38}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex LocalRegex();
    public static string NormalizeAddress(string? value)
    {
        var address = value?.Trim().ToLowerInvariant() ?? "";
        var at = address.IndexOf('@');
        if (at < 1 || at != address.LastIndexOf('@') || !IsValidLocal(address[..at]) || !IsValidDomain(address[(at + 1)..]))
            throw new MailPolicyException("Invalid email address.");
        return address;
    }
    public static string NormalizeLocal(string value) => value.Trim().ToLowerInvariant();
    public static bool IsValidLocal(string value) => LocalRegex().IsMatch(value) && !value.Contains("..", StringComparison.Ordinal);
    public static bool IsValidDomain(string value) => value.Length <= 253 && value.Contains('.') && Uri.CheckHostName(value) == UriHostNameType.Dns && value.All(c => c < 128) && !value.EndsWith('.');
    public static bool IsReserved(string value, IEnumerable<string> reserved) => reserved.Contains(value, StringComparer.OrdinalIgnoreCase);
}
public static class Tokens
{
    public static string Create() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static bool Matches(string token, string hash) => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(token)), Encoding.ASCII.GetBytes(hash));
}
public sealed class MailPolicyException(string message, int statusCode = 400) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
