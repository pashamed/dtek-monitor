using System.Text;
using System.Text.RegularExpressions;

namespace DtekBot.Shared;

// Templates are Telegram HTML plus {placeholders}. Values are HTML-escaped on render and
// the tag whitelist is enforced on save, so a rendered message is always valid for Telegram.
public static partial class MessageTemplate
{
    public const int MaxLength = 1000;
    public const string Default = "{emoji} {message}\n\nДеталі: {site}";

    public static readonly IReadOnlyList<PlaceholderDto> Placeholders =
    [
        new("emoji", "Значок типу події", "🚨"),
        new("title", "Короткий заголовок", "Екстрені відключення"),
        new("message", "Готовий текст сповіщення", "м. Київ (Правий берег): введені екстрені відключення…"),
        new("region", "Назва регіону", "м. Київ"),
        new("part", "Частина регіону (якщо визначена)", "Правий берег"),
        new("where", "Регіон разом із частиною", "м. Київ (Правий берег)"),
        new("popup", "Оригінальний текст повідомлення з сайту ДТЕК", "Шановні клієнти! Через аварію в мережі…"),
        new("time", "Час сповіщення (київський)", "14.10 19:48"),
        new("site", "Посилання на сайт ДТЕК", "https://www.dtek-kem.com.ua/ua/shutdowns"),
    ];

    private static readonly HashSet<string> KnownNames = Placeholders.Select(p => p.Name).ToHashSet();

    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "b", "strong", "i", "em", "u", "ins", "s", "strike", "del", "code", "pre", "a", "tg-spoiler", "blockquote",
    };

    public static string Render(string? template, IReadOnlyDictionary<string, string> values)
    {
        var source = string.IsNullOrWhiteSpace(template) ? Default : template;
        var output = new StringBuilder(source.Length + 256);

        for (var i = 0; i < source.Length; i++)
        {
            var c = source[i];
            if (c == '{' && i + 1 < source.Length && source[i + 1] == '{')
            {
                output.Append('{');
                i++;
            }
            else if (c == '}' && i + 1 < source.Length && source[i + 1] == '}')
            {
                output.Append('}');
                i++;
            }
            else if (c == '{')
            {
                var close = source.IndexOf('}', i + 1);
                var name = close < 0 ? null : source[(i + 1)..close];
                if (name is not null && values.TryGetValue(name, out var value))
                {
                    output.Append(Escape(value));
                    i = close;
                }
                else
                {
                    output.Append(c);
                }
            }
            else
            {
                output.Append(c);
            }
        }

        return output.ToString();
    }

    public static IReadOnlyList<string> Validate(string? template)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(template))
        {
            errors.Add("Шаблон порожній.");
            return errors;
        }

        if (template.Length > MaxLength)
        {
            errors.Add($"Шаблон задовгий: максимум {MaxLength} символів.");
        }

        var used = new HashSet<string>();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                i++;
            }
            else if (c == '}')
            {
                errors.Add("Зайва дужка }. Щоб показати її буквально, напишіть }}.");
            }
            else if (c == '{')
            {
                var close = template.IndexOf('}', i + 1);
                if (close < 0)
                {
                    errors.Add("Незакрита дужка {. Щоб показати її буквально, напишіть {{.");
                    break;
                }

                var name = template[(i + 1)..close];
                if (KnownNames.Contains(name))
                {
                    used.Add(name);
                }
                else
                {
                    errors.Add($"Невідомий параметр {{{name}}}.");
                }

                i = close;
            }
        }

        if (!used.Contains("message") && !used.Contains("popup"))
        {
            errors.Add("Додайте {message} або {popup}, інакше сповіщення не матиме змісту.");
        }

        ValidateHtml(template, errors);
        return errors.Distinct().ToList();
    }

    // Safe to inject into a page: only whitelisted attribute-free tags (plus vetted links) survive validation.
    public static string RenderPreviewHtml(
        string? template,
        AlertKind kind,
        string regionName,
        IReadOnlyList<string> partNames,
        string siteUrl,
        string? popup = null)
    {
        var values = AlertText.Values(
            kind,
            regionName,
            partNames,
            siteUrl,
            DateTimeOffset.UtcNow,
            popup ?? "Шановні клієнти! Через аварію в мережі знеструмлено частину правого берега столиці.");
        return Render(template, values);
    }

    private static void ValidateHtml(string template, List<string> errors)
    {
        var stack = new Stack<string>();
        foreach (Match match in TagPattern().Matches(template))
        {
            var closing = match.Groups[1].Value == "/";
            var name = match.Groups[2].Value.ToLowerInvariant();
            var attributes = match.Groups[3].Value;

            if (!AllowedTags.Contains(name))
            {
                errors.Add($"Тег <{name}> не підтримується Telegram.");
                continue;
            }

            if (closing)
            {
                if (stack.Count == 0 || stack.Pop() != name)
                {
                    errors.Add($"Тег </{name}> закрито не в тому місці.");
                }

                continue;
            }

            var hasAttributes = !string.IsNullOrWhiteSpace(attributes);
            if (name == "a" ? !HrefPattern().IsMatch(attributes) : hasAttributes)
            {
                errors.Add(name == "a"
                    ? "Посилання має вигляд <a href=\"https://…\">текст</a>."
                    : $"Тег <{name}> не може мати атрибутів.");
            }

            stack.Push(name);
        }

        if (stack.Count > 0)
        {
            errors.Add($"Тег <{stack.Peek()}> не закрито.");
        }

        var withoutTags = TagPattern().Replace(template, "");
        if (withoutTags.Contains('<') || withoutTags.Contains('>'))
        {
            errors.Add("Символи < та > пишіть як &lt; та &gt;.");
        }

        if (BareAmpersandPattern().IsMatch(withoutTags))
        {
            errors.Add("Символ & пишіть як &amp;.");
        }
    }

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);

    [GeneratedRegex(@"<(/?)([A-Za-z][A-Za-z0-9-]*)((?:\s+[^<>]*)?)>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^\s+href\s*=\s*""(https?://[^""\s]+|tg://[^""\s]+|\{site\})""\s*$")]
    private static partial Regex HrefPattern();

    [GeneratedRegex(@"&(?!(amp|lt|gt|quot|#\d+|#x[0-9a-fA-F]+);)")]
    private static partial Regex BareAmpersandPattern();
}
