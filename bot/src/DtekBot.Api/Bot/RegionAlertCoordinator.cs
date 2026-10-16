using System.Collections.Concurrent;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Bot;

// The one place that decides what a region's alert is. Two sources report emergencies, and the bot believes
// whichever speaks first: the DTEK site popup (RegionState, can name districts) and the Ukrenergo status
// (GridState, region-wide). Both /ingest and the grid tick call RecomputeAsync, so neither can close the
// other's alert: "ended" is only announced once both are clear.
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

            if (dtek.IsAlert)
            {
                // Narrowing to districts must not end a region-wide Ukrenergo alert for chats outside them.
                var source = grid is null ? AlertSource.None : AlertSource.Dtek;
                return await dispatcher.DispatchAsync(region, dtek, state?.PopupText, ct, source, regionWideActive: gridActive);
            }

            if (gridActive)
            {
                var synthetic = GridClassification(grid!);
                return await dispatcher.DispatchAsync(
                    region, synthetic, GridPopupText(grid!), ct, AlertSource.Ukrenergo, regionWideActive: true);
            }

            return await dispatcher.DispatchAsync(region, dtek, state?.PopupText, ct);
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
        return new PopupClassification(state.Kind, parts, state.NormalizedText, state.Hash);
    }

    // The hash only depends on the status, so a refreshed "since" never edits the message again.
    public static PopupClassification GridClassification(GridState grid) =>
        new(grid.Kind, [], grid.Title ?? "", $"grid:{grid.Status}");

    public static string GridPopupText(GridState grid)
    {
        var title = string.IsNullOrWhiteSpace(grid.Title) ? "статус Укренерго" : grid.Title;
        return grid.Since is { } since
            ? $"Укренерго: {title} (з {KyivTime.Format(since)})"
            : $"Укренерго: {title}";
    }
}
