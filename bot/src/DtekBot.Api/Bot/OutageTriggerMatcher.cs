using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text.Encodings.Web;

namespace DtekBot.Api.Bot;

public sealed record TriggerCheck(bool Valid, int Seen, int Matched, string? Warning);

// The phrase a channel's power-monitor app writes when the power goes off. Case-insensitive regular expression;
// text that is not a valid expression is searched for literally. The bot only sees posts made after it joined,
// so the last few are remembered to let the editor warn about a phrase that never occurs (never to block saving).
public static class OutageTriggerMatcher
{
    public const string DefaultPattern = @"світло\s+зникло";
    public const int MaxLength = 200;
    private const int MaxRecent = 10;
    private const int MaxRecentLength = 200;
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(200);

    private static readonly JsonSerializerOptions Json = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string? Normalize(string? pattern)
    {
        var trimmed = pattern?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed == DefaultPattern ? null : trimmed;
    }

    public static Regex Compile(string? pattern, out bool valid)
    {
        var text = string.IsNullOrWhiteSpace(pattern) ? DefaultPattern : pattern.Trim();
        var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        try
        {
            valid = true;
            return new Regex(text, options, Timeout);
        }
        catch (ArgumentException)
        {
            valid = false;
            return new Regex(Regex.Escape(text), options, Timeout);
        }
    }

    public static bool IsMatch(string? pattern, string text)
    {
        try
        {
            return Compile(pattern, out _).IsMatch(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> ParseRecent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static string AppendRecent(string? json, string text)
    {
        var list = ParseRecent(json).ToList();
        list.Add(text.Length > MaxRecentLength ? text[..MaxRecentLength] : text);
        if (list.Count > MaxRecent)
        {
            list.RemoveRange(0, list.Count - MaxRecent);
        }

        return JsonSerializer.Serialize(list, Json);
    }

    public static TriggerCheck Check(string? pattern, IReadOnlyList<string> recent)
    {
        var regex = Compile(pattern, out var valid);
        var matched = 0;
        foreach (var post in recent)
        {
            try
            {
                if (regex.IsMatch(post))
                {
                    matched++;
                }
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        string? warning = null;
        if (!valid)
        {
            warning = "Це не схоже на коректний регулярний вираз, тому бот шукатиме цей текст дослівно.";
        }
        else if (recent.Count == 0)
        {
            warning = "Бот ще не бачив публікацій цього каналу, тому перевірити текст неможливо. Він порівняє його з новими публікаціями.";
        }
        else if (matched == 0)
        {
            warning = $"Серед {recent.Count} останніх публікацій каналу такого тексту немає. Перевірте, що застосунок пише саме так (регістр не враховується).";
        }

        return new TriggerCheck(valid, recent.Count, matched, warning);
    }
}
