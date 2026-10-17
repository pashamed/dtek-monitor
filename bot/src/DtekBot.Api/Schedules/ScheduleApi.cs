namespace DtekBot.Api.Schedules;

public static class ScheduleApi
{
    public static void MapScheduleTick(this WebApplication app)
    {
        // Called on a timer from outside (the workflow, or any pinger). Safe to call as often as needed:
        // it only compares each chat's stored state with the cached schedule.
        app.MapPost("/ingest/schedules/tick", async (
            HttpRequest request,
            ScheduleNotifier notifier,
            IConfiguration config,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!Security.SecretMatches(request.Headers["X-Ingest-Secret"].ToString(), config["Ingest:Secret"]))
            {
                logger.LogWarning("Schedule tick rejected: bad or missing secret");
                return Results.Unauthorized();
            }

            await DtekBot.Api.Bot.TickGate.Schedules.WaitAsync(ct);
            try
            {
                var summary = await notifier.TickAsync(ct);
                logger.LogInformation(
                    "Schedule tick: chats={Chats} sent={Sent} edited={Edited} skipped={Skipped} failed={Failed}",
                    summary.Chats, summary.Sent, summary.Edited, summary.Skipped, summary.Failed);
                return Results.Ok(summary);
            }
            finally
            {
                DtekBot.Api.Bot.TickGate.Schedules.Release();
            }
        });
    }
}
