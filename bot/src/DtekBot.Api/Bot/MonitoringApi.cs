namespace DtekBot.Api.Bot;

public static class MonitoringApi
{
    public static void MapMonitoringTick(this WebApplication app)
    {
        app.MapPost("/ingest/monitoring/tick", async (
            HttpRequest request, IConfiguration config, MonitoringCycle cycle, CancellationToken ct) =>
        {
            if (!Security.SecretMatches(request.Headers["X-Ingest-Secret"].ToString(), config["Ingest:Secret"]))
                return Results.Unauthorized();
            await TickGate.Grid.WaitAsync(ct);
            try
            {
                await TickGate.Schedules.WaitAsync(ct);
                try
                {
                    await cycle.RunAsync(ct);
                    return Results.Ok();
                }
                finally { TickGate.Schedules.Release(); }
            }
            finally { TickGate.Grid.Release(); }
        });
    }
}
