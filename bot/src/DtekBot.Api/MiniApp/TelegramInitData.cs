using System.Collections.Specialized;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;

namespace DtekBot.Api.MiniApp;

// Validates the signed initData string a Telegram Mini App sends (see the "WebApp.initData" docs).
public static class TelegramInitData
{
    public static bool TryValidate(
        string? initData,
        string? botToken,
        DateTimeOffset now,
        TimeSpan maxAge,
        out long userId)
    {
        userId = 0;
        if (string.IsNullOrWhiteSpace(initData) || string.IsNullOrWhiteSpace(botToken))
        {
            return false;
        }

        NameValueCollection query;
        try
        {
            query = HttpUtility.ParseQueryString(initData);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var hash = query["hash"];
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        var dataCheckString = string.Join(
            '\n',
            query.AllKeys
                .Where(k => k is not null && k != "hash")
                .OrderBy(k => k, StringComparer.Ordinal)
                .Select(k => $"{k}={query[k]}"));

        var secretKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var expected = Convert.ToHexString(HMACSHA256.HashData(secretKey, Encoding.UTF8.GetBytes(dataCheckString)));

        var providedBytes = Encoding.ASCII.GetBytes(hash.ToUpperInvariant());
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        if (!CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes))
        {
            return false;
        }

        if (!long.TryParse(query["auth_date"], NumberStyles.None, CultureInfo.InvariantCulture, out var authSeconds))
        {
            return false;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(authSeconds);
        if (age > maxAge || age < TimeSpan.FromMinutes(-5))
        {
            return false;
        }

        try
        {
            using var user = JsonDocument.Parse(query["user"] ?? "");
            userId = user.RootElement.GetProperty("id").GetInt64();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }
}
