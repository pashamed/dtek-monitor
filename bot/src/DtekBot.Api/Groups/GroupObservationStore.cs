using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Groups;

public sealed record GroupDispatchSummary(int Followed, DispatchSummary Dispatch);

public sealed class GroupObservationStore(
    BotDbContext db,
    AlertDispatcher dispatcher,
    IOptionsMonitor<DtekSettings> options,
    ILogger<GroupObservationStore> logger)
{
    public async Task<GroupObservation> SaveAsync(GroupObservation observation, CancellationToken ct)
    {
        var states = await db.GroupStates.Where(s => s.RegionId == observation.RegionId).ToDictionaryAsync(s => s.GroupId, ct);
        var accepted = new Dictionary<string, GroupStatusEntry>(StringComparer.Ordinal);
        foreach (var (groupId, entry) in observation.Groups)
        {
            // Empty values are the legacy ingest contract, not a valid missing status from source collection.
            if (entry is null || entry.Today is not (null or "" or "ScheduleApplies" or "Normal" or "normal" or GroupStatuses.Emergency))
            {
                logger.LogWarning("Ignoring unknown group status for {Region}/{Group}", observation.RegionId, groupId);
                continue;
            }
            if (states.TryGetValue(groupId, out var state) && observation.ObservedAt < state.UpdatedAt)
                continue;
            if (state is null)
            {
                state = new GroupState { RegionId = observation.RegionId, GroupId = groupId };
                db.GroupStates.Add(state);
            }
            state.Today = entry.Today ?? "";
            state.Tomorrow = entry.Tomorrow ?? "";
            state.UpdatedAt = observation.ObservedAt;
            accepted[groupId] = entry;
        }
        await db.SaveChangesAsync(ct);
        return observation with { Groups = accepted };
    }

    public async Task<GroupDispatchSummary> DispatchAsync(GroupObservation observation, CancellationToken ct)
    {
        var region = options.CurrentValue.Regions.First(r => r.Id == observation.RegionId);
        var subscribers = await db.Channels.Where(c => c.IsActive && (c.Addresses.Any(a => a.RegionId == region.Id)
            || (!c.Addresses.Any() && c.RegionId == region.Id))).ToListAsync(ct);
        var followed = subscribers.SelectMany(c => c.ForRegion(region.Id).GroupIds()).Distinct(StringComparer.Ordinal).ToArray();
        var prefix = AlertDispatcher.GroupDeliveryKey(region.Id, "");
        var openGroups = await db.Deliveries.Where(d => !d.IsClosed && d.RegionId.StartsWith(prefix))
            .Select(d => d.RegionId).Distinct().ToListAsync(ct);
        int sent = 0, edited = 0, closed = 0, failed = 0;
        foreach (var groupId in followed.Concat(openGroups.Select(key => key[prefix.Length..])).Distinct(StringComparer.Ordinal))
        {
            var observed = observation.Groups.TryGetValue(groupId, out var entry);
            if (!observed && followed.Contains(groupId, StringComparer.Ordinal)) continue;
            // A group with no remaining subscribers only closes stale threads, even when the source omitted it.
            var summary = await dispatcher.DispatchGroupAsync(region, groupId, !observed || entry!.Today == GroupStatuses.Emergency, ct);
            sent += summary.Sent;
            edited += summary.Edited;
            closed += summary.Closed;
            failed += summary.Failed;
        }
        return new(followed.Length, new(sent, edited, closed, failed));
    }
}
