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
    public PopupInterpretation? Interpretation { get; init; }
    public string? Parser { get; init; }

    public PopupClassification Resolve(IReadOnlyList<string> parts) =>
        Interpretation?.Resolve(this, parts) ?? this;

    // Unknown popups are treated as alerts so nothing is silently dropped.
    public bool IsAlert => Kind is PopupKind.Emergency or PopupKind.Accident or PopupKind.Unknown;
}

public sealed partial class PopupClassifier(IOptionsMonitor<DtekSettings> options)
{
    // Compatibility classifier for legacy callers. New observations use PopupInterpreter and Interpret.
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

    public PopupInterpretation Interpret(string regionId, string? popupText)
    {
        var settings = options.CurrentValue;
        var region = settings.Regions.First(r => r.Id.Equals(regionId, StringComparison.OrdinalIgnoreCase));
        if (Normalize(popupText).Length == 0) return new("no_information", []);
        var plain = WebUtility.HtmlDecode(Regex.Replace(popupText!, @"<br\s*/?>|</p>|</h[1-6]>|</li>", "\n", RegexOptions.IgnoreCase));
        var clauses = Regex.Split(plain,
            @"[\r\n.!?;]+|,\s*(?=(?:а|але|проте|натомість|однак)\s)|,\s*(?=(?:на|у|в)\s+\p{L})",
            RegexOptions.IgnoreCase).Select(Normalize).Where(s => s.Length > 0).ToArray();
        var found = new Dictionary<string, PopupKind>(StringComparer.Ordinal);
        IReadOnlyList<string> subject = [];
        PopupKind? pendingKind = null;
        var sawKind = false;
        foreach (var clause in clauses)
        {
            var exception = Regex.Match(clause, @"\bкрім\b|за винятком|не стосується|не поширюється");
            var statement = exception.Success ? clause[..exception.Index] : clause;
            var excluded = exception.Success ? FindParts(regionId, clause[exception.Index..], settings) : [];
            var named = FindParts(regionId, statement, settings);
            var kind = ClassifyKind(statement, settings.Classification);
            if (ContainsAny(statement, ["не скасовано", "не запровадж", "не введено", "не застосовуються"]))
                return new("unable_to_parse", []);
            if (kind == PopupKind.Unknown && (ContainsAny(statement, ["діють графіки", "діють графіки стабілізаційних"])
                && !ContainsAny(statement, ["не діють", "не працюють"])))
                kind = PopupKind.Stabilization;
            if (kind == PopupKind.Unknown
                && ContainsAny(statement, ["відсутнє електропостачання", "немає електропостачання", "зникло світло"]))
                kind = PopupKind.Emergency;
            if (kind == PopupKind.Unknown)
            {
                if (named.Count > 0)
                {
                    subject = named;
                    var remainder = clause;
                    foreach (var label in region.Parts.Select(p => p.Name).Concat(region.RetiredPartLabels.Values))
                        remainder = remainder.Replace(Normalize(label), "", StringComparison.Ordinal);
                    if (pendingKind is { } pending && Regex.IsMatch(remainder, @"^(?:\s|,|та|і)*$"))
                    {
                        found.Clear();
                        foreach (var id in named) found[id] = pending;
                    }
                }
                pendingKind = null;
                continue;
            }
            sawKind = true;
            // Advisory phrases describe a risk, not an outage in this clause.
            if (ContainsAny(clause, ["уникнути екстрених", "запобігти екстреним"])) continue;
            var others = ContainsAny(statement, ["інших районах", "решті районів"]);
            var targets = IsRegionWide(statement) ? region.Parts.Select(p => p.Id).ToArray()
                : named.Count > 0 ? named
                : others ? region.Parts.Select(p => p.Id).Except(subject).ToArray()
                : subject.Count > 0 ? subject
                : region.Parts.Select(p => p.Id).ToArray();
            if (named.Count > 0) subject = named;
            if (named.Count == 0 && subject.Count == 0) pendingKind = kind;
            foreach (var id in targets.Except(excluded))
            {
                // Region-wide schedule context cannot override a local active restriction.
                if (named.Count == 0 && !ContainsAny(statement, ["тут ", "там "]) && kind is PopupKind.Stabilization or PopupKind.Cancelled
                    && found.TryGetValue(id, out var previous) && previous is PopupKind.Accident or PopupKind.Emergency)
                    continue;
                if (named.Count == 0 && kind == PopupKind.Emergency
                    && found.TryGetValue(id, out var active) && active == PopupKind.Accident) continue;
                found[id] = kind;
            }
        }
        // A parent cannot describe all its children when one of them has an explicit different state.
        foreach (var part in region.Parts.Where(p => p.Children.Count > 0))
            if (found.TryGetValue(part.Id, out var parentKind)
                && PopupInterpretation.Expand(region, part.Children).Any(id => found.TryGetValue(id, out var childKind) && childKind != parentKind))
                found.Remove(part.Id);
        if (found.Count == 0) return new(sawKind || Regex.IsMatch(Normalize(popupText), "відключ|електропостач|знеструм|графік")
            ? "unable_to_parse" : "no_information", []);
        var groups = found.GroupBy(p => p.Value).Select(g =>
        {
            var ids = region.Parts.Where(p => g.Any(v => v.Key == p.Id)).Select(p => p.Id).ToArray();
            return new PopupAreaState(g.Key.ToString().ToLowerInvariant(),
                ids.Length == region.Parts.Count ? "all" : "districts", ids);
        }).ToArray();
        var result = new PopupInterpretation("parsed", groups);
        return result.IsValid(region) ? result.Canonicalize(region) : new("unable_to_parse", []);
    }

    internal static bool IsRegionWide(string text) => ContainsAny(text,
        ["по всій", "по всьому", "усьому регіон", "всій област", "всьому міст", "всьому києв", "усьому києв", "усі райони", "всі райони", "весь київ"]);

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
