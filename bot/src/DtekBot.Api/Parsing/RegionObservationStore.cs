using DtekBot.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace DtekBot.Api.Parsing;

public sealed class RegionObservationStore(BotDbContext db, PopupClassifier classifier, TimeProvider time)
{
    public async Task<(bool Changed, PopupClassification Classification)> SaveAsync(
        DtekPopupObservation observation, CancellationToken ct)
    {
        var result = classifier.Classify(observation.RegionId, observation.PopupText, observation.Structured);
        var state = await db.RegionStates.FirstOrDefaultAsync(s => s.RegionId == observation.RegionId, ct);
        if (state is not null && observation.ObservedAt < state.ObservedAt) return (false, result);
        // Same text can classify differently after a rule or settings change; that must reach the stored state too.
        var changed = state is null || state.Hash != result.Hash || state.Kind != result.Kind
            || !(state.PartIdList ?? []).SequenceEqual(result.PartIds, StringComparer.Ordinal);
        if (state is null)
        {
            state = new RegionState { RegionId = observation.RegionId };
            db.RegionStates.Add(state);
        }
        state.ObservedAt = observation.ObservedAt;
        if (changed)
        {
            state.Kind = result.Kind;
            state.NormalizedText = result.NormalizedText;
            state.PopupText = observation.PopupText?.Trim() is { Length: > 4000 } text ? text[..4000] : observation.PopupText?.Trim();
            state.Hash = result.Hash;
            state.PartIdList = result.PartIds;
            state.UpdatedAt = time.GetUtcNow();
        }
        await db.SaveChangesAsync(ct);
        return (changed, result);
    }
}
