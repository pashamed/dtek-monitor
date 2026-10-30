using System.Text.Json;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Groups;

public sealed record GroupObservation(
    string RegionId, IReadOnlyDictionary<string, GroupStatusEntry> Groups, DateTimeOffset ObservedAt);

public interface IGroupStatusCollector
{
    Task<IReadOnlyList<GroupObservation>> CollectAsync(CancellationToken ct);
}

public sealed class GroupStatusCollector(
    IHttpClientFactory http,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time,
    ILogger<GroupStatusCollector> logger) : IGroupStatusCollector
{
    public const string ClientName = "yasno-group-status";

    public async Task<IReadOnlyList<GroupObservation>> CollectAsync(CancellationToken ct)
    {
        var results = await Task.WhenAll(options.CurrentValue.Regions.Where(r => r.HasGroups)
            .Select(r => FetchAsync(r, ct)));
        return results.OfType<GroupObservation>().ToArray();
    }

    private async Task<GroupObservation?> FetchAsync(RegionSettings region, CancellationToken ct)
    {
        try
        {
            var observedAt = time.GetUtcNow();
            var url = $"https://app.yasno.ua/api/blackout-service/public/shutdowns/regions/{region.YasnoRegionId}/dsos/{region.YasnoDsoId}/planned-outages";
            using var response = await http.CreateClient(ClientName).GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (json.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var groups = new Dictionary<string, GroupStatusEntry>(StringComparer.Ordinal);
            foreach (var group in json.RootElement.EnumerateObject())
            {
                if (group.Value.ValueKind != JsonValueKind.Object
                    || !group.Value.TryGetProperty("today", out var today)
                    || today.ValueKind != JsonValueKind.Object
                    || !today.TryGetProperty("status", out var status)
                    || status.ValueKind != JsonValueKind.String
                    || string.IsNullOrEmpty(status.GetString()))
                    continue;
                string? tomorrow = null;
                if (group.Value.TryGetProperty("tomorrow", out var next) && next.ValueKind == JsonValueKind.Object
                    && next.TryGetProperty("status", out var nextStatus) && nextStatus.ValueKind == JsonValueKind.String)
                    tomorrow = nextStatus.GetString();
                groups[group.Name] = new(status.GetString(), tomorrow);
            }
            return groups.Count == 0 ? null : new(region.Id, groups, observedAt);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogWarning(ex, "Group status collection unavailable for {Region}", region.Id);
            return null;
        }
    }
}
