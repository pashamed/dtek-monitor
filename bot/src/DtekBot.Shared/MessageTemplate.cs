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
        new("date", "Дата сповіщення (Київ)", "15.10.2026"),
        new("weekday", "День тижня", "Четвер"),
        new("daypart", "Частина доби", "Вечір"),
        new("site", "Посилання на сайт ДТЕК", "https://www.dtek-kem.com.ua/ua/shutdowns"),
    ];

    private static readonly HashSet<string> AllowedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "b", "strong", "i", "em", "u", "ins", "s", "strike", "del", "code", "pre", "a", "tg-spoiler", "blockquote",
    };

    // Syntax: {name}, a conditional block {#name}...{/name} shown only when the value is not empty,
    // and {{ / }} for literal braces. Values are HTML-escaped; the template's own tags are kept.
    public static string Render(string? template, IReadOnlyDictionary<string, string> values, string? fallback = null)
    {
        var source = string.IsNullOrWhiteSpace(template) ? fallback ?? Default : template;
        var output = new StringBuilder(source.Length + 256);
        Append(source, values, output);
        return output.ToString();
    }

    private static void Append(string source, IReadOnlyDictionary<string, string> values, StringBuilder output)
    {
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
                if (name is null)
                {
                    output.Append(c);
                }
                else if (name.StartsWith('#'))
                {
                    var block = name[1..];
                    var closing = "{/" + block + "}";
                    var end = source.IndexOf(closing, close + 1, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        output.Append(c);
                        continue;
                    }

                    if (values.TryGetValue(block, out var shown) && !string.IsNullOrWhiteSpace(shown))
                    {
                        Append(source[(close + 1)..end], values, output);
                    }

                    i = end + closing.Length - 1;
                }
                else if (name.StartsWith('/'))
                {
                    i = close;
                }
                else if (values.TryGetValue(name, out var value))
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
    }

    public static IReadOnlyList<string> Validate(string? template, string slot = MessageSlots.Emergency)
    {
        var errors = new List<string>();
        var definition = MessageSlots.Find(slot);
        if (definition is null)
        {
            errors.Add("Невідомий тип повідомлення.");
            return errors;
        }

        if (string.IsNullOrWhiteSpace(template))
        {
            errors.Add("Шаблон порожній.");
            return errors;
        }

        if (template.Length > MaxLength)
        {
            errors.Add($"Шаблон задовгий: максимум {MaxLength} символів.");
        }
        if (slot == MessageSlots.ScheduleLine && (template.Contains('<') || template.Contains('>')))
        {
            errors.Add("Рядок графіка підтримує текст, значки й параметри, без HTML-тегів.");
        }

        var known = definition.Placeholders.Select(p => p.Name).ToHashSet();
        var used = new HashSet<string>();
        string? openBlock = null;
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
                if (name.StartsWith('#'))
                {
                    var block = name[1..];
                    if (!known.Contains(block))
                    {
                        errors.Add($"Невідомий параметр {{{block}}} в умовному блоці.");
                    }
                    else if (openBlock is not null)
                    {
                        errors.Add("Умовні блоки не можуть бути вкладеними.");
                    }

                    openBlock = block;
                }
                else if (name.StartsWith('/'))
                {
                    if (openBlock != name[1..])
                    {
                        errors.Add($"Блок {{/{name[1..]}}} закрито без відкриття {{#{name[1..]}}}.");
                    }

                    openBlock = null;
                }
                else if (known.Contains(name))
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

        if (openBlock is not null)
        {
            errors.Add($"Блок {{#{openBlock}}} не закрито: додайте {{/{openBlock}}}.");
        }

        var required = MessageSlots.RequiredAny(slot);
        if (required.Count > 0 && !required.Any(used.Contains))
        {
            errors.Add(required.Count == 1
                ? $"Додайте {{{required[0]}}}, інакше повідомлення не матиме змісту."
                : $"Додайте {{{string.Join("} або {", required)}}}, інакше повідомлення не матиме змісту.");
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
