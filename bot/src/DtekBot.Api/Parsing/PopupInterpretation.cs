using System.Text.Json;
using System.Text.Json.Serialization;
using DtekBot.Api.Settings;

namespace DtekBot.Api.Parsing;

public sealed record PopupAreaState(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("districts")] IReadOnlyList<string> Districts);

public sealed record PopupInterpretation(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("states")] IReadOnlyList<PopupAreaState> States)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        PropertyNameCaseInsensitive = false,
        RespectRequiredConstructorParameters = true,
    };
    public static readonly string[] Kinds = ["cancelled", "stabilization", "accident", "emergency"];

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static PopupInterpretation? Read(string json) =>
        JsonSerializer.Deserialize<PopupInterpretation>(json, JsonOptions);

    public bool IsValid(RegionSettings region)
    {
        if (States is null) return false;
        if (Status is "unable_to_parse" or "no_information") return States.Count == 0;
        if (Status != "parsed" || States.Count == 0) return false;
        var known = region.Parts.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var assigned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in States)
        {
            if (state is null || !Kinds.Contains(state.Kind, StringComparer.Ordinal)
                || state.Scope is not ("all" or "districts")
                || state.Districts is null || state.Districts.Count == 0)
                return false;
            foreach (var id in state.Districts)
                if (!known.Contains(id) || !assigned.Add(id)) return false;
            if (!Expand(region, state.Districts).All(state.Districts.Contains)) return false;
            if (state.Scope == "all" && (States.Count != 1 || !known.SetEquals(state.Districts)))
                return false;
        }

        return true;
    }

    public PopupInterpretation Canonicalize(RegionSettings region) => this with
    {
        States = States.OrderBy(s => Priority(KindOf(s.Kind)))
            .Select(s => s with { Districts = region.Parts.Where(p => s.Districts.Contains(p.Id)).Select(p => p.Id).ToArray() })
            .ToArray(),
    };

    public static HashSet<string> Expand(RegionSettings region, IEnumerable<string> ids)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>(ids);
        while (pending.TryDequeue(out var id))
        {
            if (!result.Add(id)) continue;
            foreach (var child in region.Parts.FirstOrDefault(p => p.Id == id)?.Children ?? []) pending.Enqueue(child);
        }
        return result;
    }

    public PopupClassification Apply(PopupClassification original)
    {
        if (Status != "parsed") return original with { Kind = PopupKind.Unknown, PartIds = [], Interpretation = this };
        var active = States.Where(s => KindOf(s.Kind) is PopupKind.Accident or PopupKind.Emergency).ToArray();
        var candidates = active.Length > 0 ? active : States.ToArray();
        var kind = candidates.Select(s => KindOf(s.Kind)).OrderBy(Priority).First();
        var parts = candidates.Any(s => s.Scope == "all") ? []
            : candidates.SelectMany(s => s.Districts).Distinct(StringComparer.Ordinal).ToArray();
        return original with { Kind = kind, PartIds = parts, Interpretation = this };
    }

    public PopupClassification Resolve(PopupClassification original, IReadOnlyList<string> wanted)
    {
        if (Status != "parsed") return original;
        var matching = States.Where(s => wanted.Count == 0 || s.Scope == "all"
            || s.Districts.Intersect(wanted, StringComparer.OrdinalIgnoreCase).Any()).ToArray();
        if (matching.Length == 0)
            return original with { Kind = PopupKind.None, PartIds = [], Interpretation = null };
        var active = matching.Where(s => KindOf(s.Kind) is PopupKind.Accident or PopupKind.Emergency).ToArray();
        var candidates = active.Length > 0 ? active : matching;
        return original with
        {
            Kind = candidates.Select(s => KindOf(s.Kind)).OrderBy(Priority).First(),
            PartIds = candidates.Any(s => s.Scope == "all") ? []
                : candidates.SelectMany(s => s.Districts).Distinct(StringComparer.Ordinal).ToArray(),
            Interpretation = null,
        };
    }

    public static PopupKind KindOf(string kind) => kind switch
    {
        "emergency" => PopupKind.Emergency,
        "accident" => PopupKind.Accident,
        "stabilization" => PopupKind.Stabilization,
        "cancelled" => PopupKind.Cancelled,
        _ => PopupKind.Unknown,
    };

    private static int Priority(PopupKind kind) => kind switch
    {
        PopupKind.Accident => 0,
        PopupKind.Emergency => 1,
        PopupKind.Stabilization => 2,
        PopupKind.Cancelled => 3,
        _ => 4,
    };
}
