using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Grid;

public sealed record GridRegionResult(string RegionId, string Status, string Kind, bool Changed, DispatchSummary? Dispatch);

// feed: "ok", "unavailable" (fetch failed, nothing changed) or "stale" (older than MaxAgeMinutes, ignored).
public sealed record GridTickSummary(string Feed, DateTimeOffset? GeneratedAt, IReadOnlyList<GridRegionResult> Regions);

// Copies the Ukrenergo status of each configured region into GridState and lets the coordinator react to it.
public sealed class GridTickService(
    BotDbContext db,
    IGridStatusProvider provider,
    RegionAlertCoordinator coordinator,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time,
    ILogger<GridTickService> logger)
{
    public async Task<GridTickSummary> TickAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var feed = await provider.GetAsync(ct);
        if (feed is null)
        {
            return new GridTickSummary("unavailable", null, []);
        }

        var now = time.GetUtcNow();
        if (now - feed.GeneratedAt > TimeSpan.FromMinutes(settings.Grid.MaxAgeMinutes))
        {
            logger.LogWarning("Grid status feed is stale (generated {GeneratedAt:u}), ignored", feed.GeneratedAt);
            return new GridTickSummary("stale", feed.GeneratedAt, []);
        }

        var results = new List<GridRegionResult>();
        foreach (var region in settings.Regions.Where(r => !string.IsNullOrWhiteSpace(r.GridStatusSlug)))
        {
            if (!feed.Regions.TryGetValue(region.GridStatusSlug, out var status))
            {
                continue;
            }

            var row = await db.GridStates.FirstOrDefaultAsync(g => g.RegionId == region.Id, ct);
            var previousKind = row?.Kind ?? Parsing.PopupKind.None;
            if (row is null)
            {
                row = new GridState { RegionId = region.Id };
                db.GridStates.Add(row);
            }

            row.Status = status.Status;
            row.Title = status.Title is { Length: > 256 } t ? t[..256] : status.Title;
            row.Since = status.Since;
            row.FeedGeneratedAt = feed.GeneratedAt;
            row.UpdatedAt = now;
            await db.SaveChangesAsync(ct);

            // While an alert is on, every tick re-checks it (cheap: nothing is sent when the hash is unchanged),
            // so a send that failed earlier is retried.
            var changed = previousKind != row.Kind;
            DispatchSummary? dispatch = null;
            if (changed || row.IsAlert)
            {
                dispatch = await coordinator.RecomputeAsync(region.Id, ct);
            }

            results.Add(new GridRegionResult(region.Id, row.Status, row.Kind.ToString(), changed, dispatch));
        }

        return new GridTickSummary("ok", feed.GeneratedAt, results);
    }
}
