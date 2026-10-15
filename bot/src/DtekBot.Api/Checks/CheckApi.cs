namespace DtekBot.Api.Checks;

public static class CheckApi
{
    public static void MapAddressCheckIngest(this WebApplication app)
    {
        // The workflow asks what to check...
        app.MapGet("/ingest/addresses/pending", async (
            HttpRequest request,
            AddressCheckService checks,
            IConfiguration config,
            CancellationToken ct) =>
        {
            if (!Security.SecretMatches(request.Headers["X-Ingest-Secret"].ToString(), config["Ingest:Secret"]))
            {
                return Results.Unauthorized();
            }

            return Results.Ok(new { streets = await checks.GetPendingAsync(ct) });
        });

        // ...then reports what the DTEK site said.
        app.MapPost("/ingest/addresses", async (
            AddressResultsRequest body,
            HttpRequest request,
            AddressCheckService checks,
            IConfiguration config,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            if (!Security.SecretMatches(request.Headers["X-Ingest-Secret"].ToString(), config["Ingest:Secret"]))
            {
                logger.LogWarning("Address results rejected: bad or missing secret");
                return Results.Unauthorized();
            }

            if (body.Results is null || body.Results.Count == 0)
            {
                return Results.BadRequest(new { error = "empty results" });
            }

            var answered = await checks.ProcessResultsAsync(body, ct);
            logger.LogInformation("Address results: streets={Streets} answered={Answered}", body.Results.Count, answered);
            return Results.Ok(new { streets = body.Results.Count, answered });
        });
    }
}
