using System.Security.Cryptography;
using System.Text;
using DtekBot.Api.MiniApp;

namespace DtekBot.Tests;

public class TelegramInitDataTests
{
    private const string Token = "123456:TEST-bot-token";
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    private static string Hmac(byte[] key, string data) =>
        Convert.ToHexString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data))).ToLowerInvariant();

    // Builds initData exactly as documented by Telegram for Mini Apps.
    private static string Build(
        string botToken = Token,
        DateTimeOffset? authDate = null,
        string? userJson = "{\"id\":42,\"first_name\":\"T\"}",
        bool includeHash = true,
        Action<SortedDictionary<string, string>>? tamperAfterSigning = null,
        bool upperCaseHash = false)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = (authDate ?? Now).ToUnixTimeSeconds().ToString(),
            ["query_id"] = "AAH-test",
        };
        if (userJson is not null)
        {
            fields["user"] = userJson;
        }

        var dataCheckString = string.Join('\n', fields.Select(kv => $"{kv.Key}={kv.Value}"));
        var secretKey = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(botToken));
        var hash = Hmac(secretKey, dataCheckString);

        tamperAfterSigning?.Invoke(fields);

        var parts = fields.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}").ToList();
        if (includeHash)
        {
            parts.Add($"hash={(upperCaseHash ? hash.ToUpperInvariant() : hash)}");
        }

        return string.Join('&', parts);
    }

    private static bool Validate(string? initData, string? token = Token, DateTimeOffset? now = null) =>
        TelegramInitData.TryValidate(initData, token, now ?? Now, MaxAge, out _);

    [Fact]
    public void ValidInitData_ReturnsTrue_WithUserId()
    {
        var ok = TelegramInitData.TryValidate(Build(), Token, Now, MaxAge, out var userId);

        Assert.True(ok);
        Assert.Equal(42, userId);
    }

    [Fact]
    public void ValidInitData_UpperCaseHash_IsAccepted()
    {
        Assert.True(Validate(Build(upperCaseHash: true)));
    }

    [Fact]
    public void ValidInitData_WithLargeUserId_ParsesInt64()
    {
        var initData = Build(userJson: "{\"id\":9007199254740993,\"first_name\":\"T\"}");

        Assert.True(TelegramInitData.TryValidate(initData, Token, Now, MaxAge, out var userId));
        Assert.Equal(9007199254740993L, userId);
    }

    [Fact]
    public void ValidInitData_WithNonAsciiUserName_IsAccepted()
    {
        Assert.True(Validate(Build(userJson: "{\"id\":42,\"first_name\":\"Павло\"}")));
    }

    [Fact]
    public void TamperedField_ReturnsFalse()
    {
        var initData = Build(tamperAfterSigning: f => f["query_id"] = "AAH-other");

        var ok = TelegramInitData.TryValidate(initData, Token, Now, MaxAge, out var userId);

        Assert.False(ok);
        Assert.Equal(0, userId);
    }

    [Fact]
    public void TamperedUserId_ReturnsFalse()
    {
        var initData = Build(tamperAfterSigning: f => f["user"] = "{\"id\":43,\"first_name\":\"T\"}");

        Assert.False(Validate(initData));
    }

    [Fact]
    public void AddedField_ReturnsFalse()
    {
        Assert.False(Validate(Build() + "&extra=1"));
    }

    [Fact]
    public void WrongBotToken_ReturnsFalse()
    {
        Assert.False(Validate(Build(), token: "654321:OTHER"));
    }

    [Fact]
    public void SignedWithOtherToken_ReturnsFalse()
    {
        Assert.False(Validate(Build(botToken: "654321:OTHER")));
    }

    [Fact]
    public void MissingHash_ReturnsFalse()
    {
        Assert.False(Validate(Build(includeHash: false)));
    }

    [Fact]
    public void EmptyHash_ReturnsFalse()
    {
        Assert.False(Validate(Build(includeHash: false) + "&hash="));
    }

    [Fact]
    public void GarbageHash_ReturnsFalse()
    {
        Assert.False(Validate(Build(includeHash: false) + "&hash=zzzz"));
    }

    [Fact]
    public void ExpiredAuthDate_ReturnsFalse()
    {
        var initData = Build(authDate: Now - MaxAge - TimeSpan.FromSeconds(1));

        Assert.False(Validate(initData));
    }

    [Fact]
    public void AuthDateJustInsideMaxAge_ReturnsTrue()
    {
        var initData = Build(authDate: Now - MaxAge + TimeSpan.FromSeconds(1));

        Assert.True(Validate(initData));
    }

    [Fact]
    public void AuthDateFarInTheFuture_ReturnsFalse()
    {
        Assert.False(Validate(Build(authDate: Now + TimeSpan.FromHours(1))));
    }

    [Fact]
    public void AuthDateSlightlyInTheFuture_IsTolerated()
    {
        Assert.True(Validate(Build(authDate: Now + TimeSpan.FromMinutes(1))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankBotToken_ReturnsFalse(string? token)
    {
        Assert.False(Validate(Build(), token));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankInitData_ReturnsFalse(string? initData)
    {
        Assert.False(Validate(initData));
    }

    [Fact]
    public void UserWithoutId_ReturnsFalse()
    {
        Assert.False(Validate(Build(userJson: "{\"first_name\":\"T\"}")));
    }

    [Theory]
    [InlineData("{\"id\":\"42\"}")]
    [InlineData("[1,2]")]
    [InlineData("null")]
    [InlineData("not json")]
    public void MalformedUser_ReturnsFalse(string userJson)
    {
        Assert.False(Validate(Build(userJson: userJson)));
    }

    [Fact]
    public void MissingUser_ReturnsFalse()
    {
        Assert.False(Validate(Build(userJson: null)));
    }

    [Fact]
    public void NonNumericAuthDate_ReturnsFalse()
    {
        // Sign a payload whose auth_date is not a number so only that check can fail.
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = "yesterday",
            ["user"] = "{\"id\":42}",
        };
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(Token));
        var hash = Hmac(secret, string.Join('\n', fields.Select(kv => $"{kv.Key}={kv.Value}")));
        var initData = $"auth_date=yesterday&user={Uri.EscapeDataString(fields["user"])}&hash={hash}";

        Assert.False(Validate(initData));
    }
}
