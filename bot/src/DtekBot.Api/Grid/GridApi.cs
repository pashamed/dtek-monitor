namespace DtekBot.Api.Grid;

public static class GridApi
{
    public static void MapGridTick(this WebApplication app)
    {
        // Called on a timer from outside (the workflow, or any pinger). Safe to call as often as needed:
        // the feed is cached and nothing is sent unless a region's alert really changed.
        app.MapPost("/ingest/grid/tick", async (
            HttpRequest request,
            GridTickService grid,
            IConfiguration config,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!Security.SecretMatches(request.Headers["X-Ingest-Secret"].ToString(), config["Ingest:Secret"]))
            {
                logger.LogWarning("Grid tick rejected: bad or missing secret");
                return Results.Unauthorized();
            }

            await DtekBot.Api.Bot.TickGate.Grid.WaitAsync(ct);
            try
            {
                var summary = await grid.TickAsync(ct);
                logger.LogInformation(
                    "Grid tick: feed={Feed} regions={Regions}",
                    summary.Feed,
                    string.Join(",", summary.Regions.Select(r => $"{r.RegionId}={r.Status}")));
                return Results.Ok(summary);
            }
            finally
            {
                DtekBot.Api.Bot.TickGate.Grid.Release();
            }
        });
    }
}
