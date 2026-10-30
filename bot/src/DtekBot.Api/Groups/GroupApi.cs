using System.Text.Json.Serialization;
using DtekBot.Api.Bot;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Groups;

public sealed record GroupStatusEntry(
    [property: JsonPropertyName("today")] string? Today,
    [property: JsonPropertyName("tomorrow")] string? Tomorrow);

public sealed record GroupsIngestRequest(
    [property: JsonPropertyName("regionId")] string RegionId,
    [property: JsonPropertyName("groups")] Dictionary<string, GroupStatusEntry>? Groups,
    [property: JsonPropertyName("observedAt")] DateTimeOffset? ObservedAt = null);

public static class GroupApi
{
    public static void MapGroupIngest(this WebApplication app)
    {
        app.MapPost("/ingest/groups", async (
            GroupsIngestRequest body,
            HttpRequest request,
            GroupObservationStore observations,
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

            await TickGate.Grid.WaitAsync(ct);
            try
            {
                var accepted = await observations.SaveAsync(new(region.Id, body.Groups, body.ObservedAt ?? time.GetUtcNow()), ct);
                var result = await observations.DispatchAsync(accepted, ct);
                var summary = result.Dispatch;
                logger.LogInformation("Group ingest {Region}: groups={Groups} dispatch={@Dispatch}", region.Id, body.Groups.Count, summary);
                return Results.Ok(new
                {
                    regionId = region.Id, groups = body.Groups.Count,
                    followed = result.Followed,
                    sent = summary.Sent, edited = summary.Edited, closed = summary.Closed, failed = summary.Failed,
                });
            }
            finally
            {
                TickGate.Grid.Release();
            }
        });
    }
}
