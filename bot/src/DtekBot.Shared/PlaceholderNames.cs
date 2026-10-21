using System.Text;

namespace DtekBot.Shared;

// Users see and type Ukrainian placeholder names; storage and rendering use the canonical Latin keys.
// Translation happens at the API boundary and touches only {name}, {#name} and {/name} tokens.
public static class PlaceholderNames
{
    // Canonical Latin key -> Ukrainian name (lowercase Cyrillic and '_' only, so it survives the {name} tokenizer).
    public static readonly IReadOnlyDictionary<string, string> Table = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["emoji"] = "значок",
        ["title"] = "заголовок",
        ["message"] = "текст",
        ["region"] = "регіон",
        ["part"] = "район",
        ["where"] = "місце",
        ["popup"] = "текст_сайту",
        ["time"] = "час",
        ["minutes"] = "хвилин",
        ["site"] = "сайт",
        ["group"] = "черга",
        ["address"] = "адреса",
        ["header"] = "шапка",
        ["weekday"] = "день_тижня",
        ["date"] = "дата",
        ["outages"] = "відключення",
        ["total"] = "разом",
        ["tomorrow"] = "завтра",
        ["updated"] = "оновлено",
        ["onDuration"] = "світло_було",
        ["offDuration"] = "світла_не_було",
        ["until"] = "очікуємо_до",
        ["next"] = "наступне",
        ["planned"] = "тривалість",
        ["start"] = "початок",
        ["end"] = "кінець",
        ["duration"] = "тривалість_інтервалу",
        ["daypart"] = "частина_доби",
        ["morning"] = "ранок",
        ["afternoon"] = "день",
        ["evening"] = "вечір",
        ["night"] = "ніч",
        ["tomorrowDate"] = "дата_завтра",
        ["changes"] = "зміни",
    };

    private static readonly Dictionary<string, string> ToKey =
        Table.ToDictionary(p => p.Value, p => p.Key, StringComparer.Ordinal);

    public static string Display(string key) => Table.TryGetValue(key, out var name) ? name : key;

    public static string Canonical(string name) => ToKey.TryGetValue(name, out var key) ? key : name;

    // Accepts Ukrainian or Latin names; unknown names are left as they are so validation can report them.
    public static string ToCanonical(string? template) => Translate(template, Canonical);

    public static string ToDisplay(string? template) => Translate(template, Display);

    // Placeholder list as shown to users: Ukrainian Name, canonical Key.
    public static IReadOnlyList<PlaceholderDto> ToDisplayList(IReadOnlyList<PlaceholderDto> placeholders) =>
        placeholders.Select(p => p with { Name = Display(p.Name), Key = p.Key ?? p.Name }).ToList();

    public static TemplateSlotDto ToDisplaySlot(TemplateSlotDto slot) =>
        slot with { DefaultTemplate = ToDisplay(slot.DefaultTemplate), Placeholders = ToDisplayList(slot.Placeholders) };

    private static string Translate(string? text, Func<string, string> map)
    {
        if (string.IsNullOrEmpty(text) || text.IndexOf('{') < 0)
        {
            return text ?? "";
        }

        var output = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if ((c == '{' || c == '}') && i + 1 < text.Length && text[i + 1] == c)
            {
                output.Append(c).Append(c);
                i++;
            }
            else if (c == '{')
            {
                var close = text.IndexOf('}', i + 1);
                if (close < 0)
                {
                    output.Append(text, i, text.Length - i);
                    break;
                }

                var name = text[(i + 1)..close];
                var prefix = name.Length > 0 && (name[0] == '#' || name[0] == '/') ? name[..1] : "";
                output.Append('{').Append(prefix).Append(map(name[prefix.Length..])).Append('}');
                i = close;
            }
            else
            {
                output.Append(c);
            }
        }

        return output.ToString();
    }
}
