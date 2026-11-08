using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Parsing;

public sealed class RegionObservationStore(BotDbContext db, PopupClassifier classifier, TimeProvider time, PopupInterpreter interpreter,
    IOptionsMonitor<DtekSettings> options)
{
    public async Task<PopupClassification> PrepareAsync(DtekPopupObservation observation, CancellationToken ct)
    {
        var existing = await db.RegionStates.AsNoTracking().FirstOrDefaultAsync(s => s.RegionId == observation.RegionId, ct);
        if (existing is not null && observation.ObservedAt < existing.ObservedAt) return existing.Classification();
        var parsed = await interpreter.InterpretAsync(observation.RegionId, observation.PopupText, ct);
        var original = classifier.Classify(observation.RegionId, observation.PopupText);
        var result = original.Kind == PopupKind.None ? original : parsed.Interpretation.Apply(original);
        return result with
        {
            Parser = parsed.Parser,
            Hash = result.Interpretation is null ? result.Hash : Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
                    result.Hash + "\n" + result.Interpretation.ToJson()))),
        };
    }

    public async Task<(bool Changed, PopupClassification Classification)> SaveAsync(
        DtekPopupObservation observation, CancellationToken ct, PopupClassification? prepared = null)
    {
        var result = prepared ?? await PrepareAsync(observation, ct);
        var state = await db.RegionStates.FirstOrDefaultAsync(s => s.RegionId == observation.RegionId, ct);
        if (state is not null && observation.ObservedAt < state.ObservedAt) return (false, result);
        // Same text can classify differently after a rule or settings change; that must reach the stored state too.
        var changed = state is null || state.Hash != result.Hash || state.Kind != result.Kind
            || state.InterpretationJson != result.Interpretation?.ToJson()
            || !(state.PartIdList ?? []).SequenceEqual(result.PartIds, StringComparer.Ordinal);
        if (state is null)
        {
            state = new RegionState { RegionId = observation.RegionId };
            db.RegionStates.Add(state);
        }
        state.ObservedAt = observation.ObservedAt;

        // A popup can be missing from one page load while the outage goes on; an alert ends only when
        // a second popup-less observation, taken at least ClearConfirm later, agrees.
        var confirm = options.CurrentValue.Timers.ClearConfirm;
        if (changed && result.Kind == PopupKind.None && state.Kind is PopupKind.Emergency or PopupKind.Accident or PopupKind.Unknown
            && confirm > TimeSpan.Zero)
        {
            state.ClearPendingSince ??= observation.ObservedAt;
            if (observation.ObservedAt - state.ClearPendingSince < confirm)
            {
                await db.SaveChangesAsync(ct);
                return (false, state.Classification());
            }
        }
        state.ClearPendingSince = null;
        if (changed)
        {
            state.Kind = result.Kind;
            state.NormalizedText = result.NormalizedText.Length > 4000 ? result.NormalizedText[..4000] : result.NormalizedText;
            state.PopupText = observation.PopupText?.Trim() is { Length: > 4000 } text ? text[..4000] : observation.PopupText?.Trim();
            state.Hash = result.Hash;
            state.PartIdList = result.PartIds;
            state.InterpretationJson = result.Interpretation?.ToJson();
            state.UpdatedAt = time.GetUtcNow();
        }
        state.PopupParser = result.Parser;
        await db.SaveChangesAsync(ct);
        return (changed, result);
    }
}
