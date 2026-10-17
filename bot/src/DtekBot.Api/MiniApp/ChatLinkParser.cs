using System.Text.RegularExpressions;

namespace DtekBot.Api.MiniApp;

public sealed record ChatLinkTarget(string? Chat, string? Error);

// Turns whatever a user pastes for a channel (https://t.me/name, t.me/name, @name, name, a numeric id) into the value
// Telegram's getChat accepts: "@name" or a numeric id. Private invite links cannot be resolved by a bot.
public static partial class ChatLinkParser
{
    public const string InviteError =
        "Посилання-запрошення (t.me/+…) приватне, за ним бот не знайде канал. Додайте бота адміністратором у канал: він підключиться сам. Або вкажіть публічне посилання чи @імʼя каналу.";

    public static ChatLinkTarget Parse(string? input)
    {
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 200 || text.Any(char.IsControl) || text.Any(char.IsWhiteSpace))
        {
            return new(null, "Вкажіть посилання на канал (t.me/…) або @імʼя.");
        }

        if (long.TryParse(text, out var id))
        {
            return new(id.ToString(), null);
        }

        var deepLink = DeepLink().Match(text);
        if (deepLink.Success)
        {
            return FromName(deepLink.Groups[1].Value);
        }

        var link = TelegramLink().Match(text);
        if (link.Success)
        {
            var first = link.Groups[1].Value;
            if (first.StartsWith('+') || first.Equals("joinchat", StringComparison.OrdinalIgnoreCase))
            {
                return new(null, InviteError);
            }

            // t.me/s/name is the public web preview of a channel.
            if (first.Equals("s", StringComparison.OrdinalIgnoreCase) && link.Groups[2].Success)
            {
                return FromName(link.Groups[2].Value);
            }

            return FromName(first);
        }

        return FromName(text.TrimStart('@'));
    }

    private static ChatLinkTarget FromName(string name) =>
        UserName().IsMatch(name)
            ? new("@" + name, null)
            : new(null, "Це не схоже на публічне імʼя каналу. Приклад: https://t.me/my_channel або @my_channel.");

    [GeneratedRegex(@"^(?:https?://)?(?:www\.)?(?:t|telegram)\.me/([^/?#]+)(?:/([^/?#]+))?(?:[/?#].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TelegramLink();

    [GeneratedRegex(@"^tg://resolve\?(?:.*&)?domain=([^&#]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeepLink();

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]{3,31}$")]
    private static partial Regex UserName();
}
