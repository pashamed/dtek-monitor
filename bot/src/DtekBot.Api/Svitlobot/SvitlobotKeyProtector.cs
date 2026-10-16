using System.Security.Cryptography;
using System.Text;

namespace DtekBot.Api.Svitlobot;

// Encrypts the Svitlobot channel key (it lets anyone change the channel's schedule) before it reaches the database.
// AES-GCM with a 32-byte key from "Svitlobot:KeyEncryptionKey" (base64); stored as base64(nonce | tag | ciphertext).
public sealed class SvitlobotKeyProtector
{
    public const string ConfigKey = "Svitlobot:KeyEncryptionKey";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly Func<string?> _keySource;

    public SvitlobotKeyProtector(IConfiguration config) => _keySource = () => config[ConfigKey];

    // For tests: lets the setting change while the application runs.
    public SvitlobotKeyProtector(Func<string?> keySource) => _keySource = keySource;

    // False when the server setting is missing or is not 32 bytes of base64: the whole feature is then off.
    public bool IsConfigured => LoadKey() is not null;

    private byte[]? LoadKey()
    {
        var text = _keySource();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var key = Convert.FromBase64String(text.Trim());
            return key.Length == 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // Null when the feature is not configured.
    public string? Protect(string plain)
    {
        var key = LoadKey();
        if (key is null)
        {
            return null;
        }

        var data = Encoding.UTF8.GetBytes(plain);
        var blob = new byte[NonceSize + TagSize + data.Length];
        var nonce = blob.AsSpan(0, NonceSize);
        var tag = blob.AsSpan(NonceSize, TagSize);
        RandomNumberGenerator.Fill(nonce);

        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, data, blob.AsSpan(NonceSize + TagSize), tag);
        return Convert.ToBase64String(blob);
    }

    // Null when the feature is not configured, the value is damaged or it was encrypted with another key.
    public string? Unprotect(string? stored)
    {
        var key = LoadKey();
        if (key is null || string.IsNullOrEmpty(stored))
        {
            return null;
        }

        try
        {
            var blob = Convert.FromBase64String(stored);
            if (blob.Length < NonceSize + TagSize)
            {
                return null;
            }

            var plain = new byte[blob.Length - NonceSize - TagSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(blob.AsSpan(0, NonceSize), blob.AsSpan(NonceSize + TagSize), blob.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
