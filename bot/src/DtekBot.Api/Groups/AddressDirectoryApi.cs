using System.Text.Json.Serialization;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Groups;

public sealed record AddressDirectoryIngestRequest(
    [property: JsonPropertyName("regionId")] string RegionId,
    [property: JsonPropertyName("streets")] Dictionary<string, List<string>?>? Streets);

public static class AddressDirectoryApi
{
    private const int MaxCities = 5_000;
    private const int MaxStreets = 100_000;
    private const int MaxNameLength = 256;

    public static void MapAddressDirectoryIngest(this WebApplication app)
    {
        app.MapPost("/ingest/address-directory", (
            AddressDirectoryIngestRequest body,
            HttpRequest request,
            IDtekAddressClient addresses,
            IOptionsMonitor<DtekSettings> settings,
            IConfiguration config,
            ILogger<Program> logger) =>
        {
            if (!Security.SecretMatches(request.Headers["X-Ingest-Secret"].ToString(), config["Ingest:Secret"]))
            {
                logger.LogWarning("Address directory ingest rejected: bad or missing secret");
                return Results.Unauthorized();
            }

            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, body.RegionId, StringComparison.OrdinalIgnoreCase));
            if (region is null || string.IsNullOrWhiteSpace(region.SiteUrl) || region.HasGroups)
            {
                return Results.BadRequest(new { error = "region does not use DTEK address lookup" });
            }

            if (body.Streets is not { Count: > 0 and <= MaxCities })
            {
                return Results.BadRequest(new { error = "empty or oversized address directory" });
            }

            var streetCount = 0;
            var normalized = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var (city, streets) in body.Streets)
            {
                if (string.IsNullOrWhiteSpace(city) || city.Length > MaxNameLength
                    || streets is null || streets.Count == 0)
                {
                    return Results.BadRequest(new { error = "invalid address directory entry" });
                }

                streetCount += streets.Count;
                if (streetCount > MaxStreets || streets.Any(street =>
                        string.IsNullOrWhiteSpace(street) || street.Length > MaxNameLength))
                {
                    return Results.BadRequest(new { error = "invalid or oversized address directory" });
                }

                if (!normalized.TryAdd(city, streets))
                {
                    return Results.BadRequest(new { error = "duplicate city in address directory" });
                }
            }

            addresses.ImportDirectory(region, normalized);
            logger.LogInformation(
                "Address directory refreshed for {RegionId}: {Cities} cities, {Streets} streets",
                region.Id, normalized.Count, streetCount);
            return Results.Ok(new { regionId = region.Id, cities = normalized.Count, streets = streetCount });
        });
    }
}
