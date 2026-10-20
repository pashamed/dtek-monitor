using System.Text.Json.Serialization;
using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Groups;

public sealed record GroupStatusEntry(
    [property: JsonPropertyName("today")] string? Today,
    [property: JsonPropertyName("tomorrow")] string? Tomorrow);

public sealed record GroupsIngestRequest(
    [property: JsonPropertyName("regionId")] string RegionId,
    [property: JsonPropertyName("groups")] Dictionary<string, GroupStatusEntry>? Groups);

public static class GroupApi
{
    public static void MapGroupIngest(this WebApplication app)
    {
        app.MapPost("/ingest/groups", async (
            GroupsIngestRequest body,
            HttpRequest request,
            BotDbContext db,
            AlertDispatcher dispatcher,
            IOptionsMonitor<DtekSettings> settings,
            TimeProvider time,
            IConfiguration config,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!Security.SecretMatches(request.Headers["X-Ingest-Secret"].ToString(), config["Ingest:Secret"]))
            {
                logger.LogWarning("Group ingest rejected: bad or missing secret");
                return Results.Unauthorized();
            }

            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, body.RegionId, StringComparison.OrdinalIgnoreCase));
            if (region is null || !region.HasGroups)
            {
                return Results.BadRequest(new { error = "region has no groups" });
            }

            // An empty feed must never read as "all emergencies ended".
            if (body.Groups is null || body.Groups.Count == 0)
            {
                return Results.BadRequest(new { error = "empty groups" });
            }

            var now = time.GetUtcNow();
            var states = await db.GroupStates.Where(s => s.RegionId == region.Id).ToDictionaryAsync(s => s.GroupId, ct);
            foreach (var (groupId, entry) in body.Groups)
            {
                var today = entry.Today ?? "";
                var tomorrow = entry.Tomorrow ?? "";
                if (!states.TryGetValue(groupId, out var state))
                {
                    state = new GroupState { RegionId = region.Id, GroupId = groupId };
                    db.GroupStates.Add(state);
                    states[groupId] = state;
                }
                else if (state.Today == today && state.Tomorrow == tomorrow)
                {
                    continue;
                }

                state.Today = today;
                state.Tomorrow = tomorrow;
                state.UpdatedAt = now;
            }

            await db.SaveChangesAsync(ct);

            // Only groups somebody follows are dispatched; delivery hashes make repeats no-ops.
            var subscribers = await db.Channels.Where(c => c.RegionId == region.Id && c.IsActive).ToListAsync(ct);
            var followed = subscribers.SelectMany(c => c.GroupIds()).Distinct(StringComparer.Ordinal).ToList();

            int sent = 0, edited = 0, closed = 0, failed = 0;
            foreach (var groupId in followed)
            {
                if (!body.Groups.TryGetValue(groupId, out var entry))
                {
                    continue;
                }

                var summary = await dispatcher.DispatchGroupAsync(region, groupId, entry.Today == GroupStatuses.Emergency, ct);
                sent += summary.Sent;
                edited += summary.Edited;
                closed += summary.Closed;
                failed += summary.Failed;
            }

            logger.LogInformation(
                "Group ingest {Region}: groups={Groups} followed={Followed} sent={Sent} edited={Edited} closed={Closed} failed={Failed}",
                region.Id, body.Groups.Count, followed.Count, sent, edited, closed, failed);
            return Results.Ok(new { regionId = region.Id, groups = body.Groups.Count, followed = followed.Count, sent, edited, closed, failed });
        });
    }
}
