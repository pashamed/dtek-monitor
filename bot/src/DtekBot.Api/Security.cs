using System.Security.Cryptography;
using System.Text;

namespace DtekBot.Api;

public static class Security
{
    // Hashing first keeps the comparison constant-time regardless of the input length.
    public static bool SecretMatches(string? provided, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
        {
            return false;
        }

        var a = SHA256.HashData(Encoding.UTF8.GetBytes(provided));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
