using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Parsing;

public enum PopupKind
{
    None,
    Stabilization,
    Cancelled,
    Accident,
    Emergency,
    Unknown,
}

public sealed record StructuredPopup(
    [property: System.Text.Json.Serialization.JsonPropertyName("scope")] string? Scope,
    [property: System.Text.Json.Serialization.JsonPropertyName("districts")] IReadOnlyList<string>? Districts);

public sealed record PopupClassification(
    PopupKind Kind,
    IReadOnlyList<string> PartIds,
    string NormalizedText,
    string Hash)
{
    // Unknown popups are treated as alerts so nothing is silently dropped.
    public bool IsAlert => Kind is PopupKind.Emergency or PopupKind.Accident or PopupKind.Unknown;
}

public sealed partial class PopupClassifier(IOptionsMonitor<DtekSettings> options)
{
    // structured is what the optional LLM step extracted from the same text; it only decides the districts,
    // never the kind, and is ignored unless it names known districts of the region (or says "all").
    public PopupClassification Classify(string regionId, string? popupText, StructuredPopup? structured = null)
    {
        var settings = options.CurrentValue;
        var text = Normalize(popupText);

        if (text.Length == 0)
        {
            return new PopupClassification(PopupKind.None, [], text, Hash(text));
        }

        var kind = ClassifyKind(text, settings.Classification);
        var parts = FromStructured(regionId, structured, settings) ?? FindParts(regionId, text, settings);
        return new PopupClassification(kind, parts, text, Hash(text));
    }

    // Null means "no usable structured answer, fall back to keywords".
    private static List<string>? FromStructured(string regionId, StructuredPopup? structured, DtekSettings settings)
    {
        if (structured is null)
        {
            return null;
        }

        var region = settings.Regions.FirstOrDefault(r => string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            return null;
        }

        if (string.Equals(structured.Scope, "all", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (!string.Equals(structured.Scope, "districts", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var known = region.Parts.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var valid = (structured.Districts ?? []).Where(known.Contains).Distinct(StringComparer.Ordinal).ToList();
        return valid.Count == 0 ? null : ExpandChildren(region, valid);
    }

    // A part also stands for the parts it implies (a bank for its city districts).
    private static List<string> ExpandChildren(RegionSettings region, IEnumerable<string> ids)
    {
        var result = new List<string>();
        var queue = new Queue<string>(ids);
        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (result.Contains(id, StringComparer.Ordinal))
            {
                continue;
            }

            result.Add(id);
            var part = region.Parts.FirstOrDefault(p => p.Id == id);
            foreach (var child in part?.Children ?? [])
            {
                queue.Enqueue(child);
            }
        }

        return result;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var withBreaks = BreakTag().Replace(value, " ");
        var withoutTags = AnyTag().Replace(withBreaks, "");
        var decoded = WebUtility.HtmlDecode(withoutTags).Replace('\u00A0', ' ');
        return FoldApostrophes(Whitespace().Replace(decoded, " ").Trim().ToLowerInvariant());
    }

    // The site writes ʼ, ’ or ' for the same apostrophe; matching treats them all alike.
    private static string FoldApostrophes(string value) =>
        value.Replace('\u02BC', '\'').Replace('\u2019', '\'').Replace('\u2018', '\'').Replace('`', '\'');

    private static PopupKind ClassifyKind(string text, ClassificationSettings rules)
    {
        if (ContainsAny(text, rules.Cancelled))
        {
            return PopupKind.Cancelled;
        }

        // A stabilization notice can also report an accident in some districts; the accident is the alert.
        if (ContainsAny(text, rules.Accident))
        {
            return PopupKind.Accident;
        }

        // "діють стабілізаційні…" is stabilization, but "не діють стабілізаційні…" is not.
        if (rules.Stabilization.Any(phrase => ContainsNotNegated(text, phrase)))
        {
            return PopupKind.Stabilization;
        }

        if (ContainsAny(text, rules.Emergency))
        {
            return PopupKind.Emergency;
        }

        return PopupKind.Unknown;
    }

    private static List<string> FindParts(string regionId, string text, DtekSettings settings)
    {
        var region = settings.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));

        return region is null
            ? []
            : ExpandChildren(region, region.Parts.Where(part => ContainsAny(text, part.Aliases)).Select(part => part.Id));
    }

    private static bool ContainsAny(string text, IEnumerable<string> phrases) =>
        phrases.Any(phrase => text.Contains(FoldApostrophes(phrase), StringComparison.OrdinalIgnoreCase));

    private static bool ContainsNotNegated(string text, string phrase)
    {
        var index = text.IndexOf(phrase, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var negated = index >= 3 && text.AsSpan(index - 3, 3).SequenceEqual("не ");
            if (!negated)
            {
                return true;
            }

            index = text.IndexOf(phrase, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    [GeneratedRegex(@"<br\s*/?>|</p>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakTag();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex AnyTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
