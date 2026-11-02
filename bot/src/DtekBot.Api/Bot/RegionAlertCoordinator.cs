using System.Collections.Concurrent;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Bot;

// Explicit DTEK statuses take priority for their territory. Ukrenergo covers unresolved areas;
// an absent or unparsed popup cannot override an active grid alert.
public sealed class RegionAlertCoordinator(
    BotDbContext db,
    AlertDispatcher dispatcher,
    PopupClassifier classifier,
    IOptionsMonitor<DtekSettings> options,
    ILogger<RegionAlertCoordinator> logger)
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new();

    // Null when the region is unknown.
    public async Task<DispatchSummary?> RecomputeAsync(string regionId, CancellationToken ct)
    {
        var region = options.CurrentValue.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            return null;
        }

        // Serializes recomputes of one region so a tick and an ingest cannot both post the same alert.
        var gate = Gates.GetOrAdd(region.Id, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var state = await db.RegionStates.AsNoTracking().FirstOrDefaultAsync(s => s.RegionId == region.Id, ct);
            var grid = await db.GridStates.AsNoTracking().FirstOrDefaultAsync(g => g.RegionId == region.Id, ct);

            var dtek = DtekClassification(region, state);
            var gridActive = grid is { IsAlert: true };
            logger.LogInformation(
                "Region {Region}: dtek={DtekKind} grid={GridStatus}", region.Id, dtek.Kind, grid?.Status ?? "none");

            var fallback = gridActive ? GridFallback(region, dtek, grid!) : null;
            var source = dtek.Kind != PopupKind.None || (grid is null && state is not null) ? AlertSource.Dtek
                : grid is not null ? AlertSource.Ukrenergo : AlertSource.None;
            return await dispatcher.DispatchAsync(
                region, dtek, state?.PopupText, ct, source, regionWideActive: fallback is not null,
                regionWideFallback: fallback,
                regionWideFallbackText: fallback is not null ? GridPopupText(grid!) : null);
        }
        finally
        {
            gate.Release();
        }
    }

    private PopupClassification DtekClassification(RegionSettings region, RegionState? state)
    {
        if (state is null)
        {
            return classifier.Classify(region.Id, null);
        }

        // Rows written before the districts were stored have none: fall back to the keywords in the text.
        var parts = state.PartIdList ?? classifier.Classify(region.Id, state.PopupText).PartIds;
        return state.Classification() with { PartIds = parts };
    }

    // The hash only depends on the status, so a refreshed "since" never edits the message again.
    public static PopupClassification GridClassification(GridState grid) =>
        new(grid.Kind, [], grid.Title ?? "", $"grid:{grid.Status}");

    private static PopupClassification? GridFallback(RegionSettings region, PopupClassification dtek, GridState grid)
    {
        var fallback = GridClassification(grid);
        var cleared = dtek.ExplicitClearanceParts(region);
        if (cleared.Count == 0) return fallback;
        var remaining = region.Parts.Where(p => p.Children.Count == 0 && !cleared.Contains(p.Id))
            .Select(p => p.Id).ToArray();
        return remaining.Length == 0 ? null : fallback with
        {
            PartIds = remaining,
            Hash = $"{fallback.Hash}:districts:{string.Join(",", remaining)}",
        };
    }

    public static string GridPopupText(GridState grid)
    {
        var title = string.IsNullOrWhiteSpace(grid.Title) ? "статус Укренерго" : grid.Title;
        return grid.Since is { } since
            ? $"Укренерго: {title} (з {KyivTime.Format(since)})"
            : $"Укренерго: {title}";
    }
}
