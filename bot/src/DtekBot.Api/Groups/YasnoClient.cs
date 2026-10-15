using System.Text.Json;
using System.Text.Json.Serialization;

namespace DtekBot.Api.Groups;

public sealed record YasnoItem(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("value")] string Value);

public sealed record YasnoGroup(
    [property: JsonPropertyName("group")] int Group,
    [property: JsonPropertyName("subgroup")] int Subgroup)
{
    public string Id => $"{Group}.{Subgroup}";
}

public interface IYasnoClient
{
    Task<IReadOnlyList<YasnoItem>> SearchStreetsAsync(int regionId, int dsoId, string query, CancellationToken ct);

    Task<IReadOnlyList<YasnoItem>> SearchHousesAsync(int regionId, int dsoId, int streetId, string query, CancellationToken ct);

    Task<YasnoGroup?> GetGroupAsync(int regionId, int dsoId, int streetId, int houseId, CancellationToken ct);
}

// Undocumented public API used by yasno.ua; any failure is reported as "no result" so the bot never breaks on it.
public sealed class YasnoClient(HttpClient http, ILogger<YasnoClient> logger) : IYasnoClient
{
    public const string BaseUrl = "https://app.yasno.ua/api/blackout-service/public/shutdowns/addresses/v2/";

    public async Task<IReadOnlyList<YasnoItem>> SearchStreetsAsync(int regionId, int dsoId, string query, CancellationToken ct) =>
        await GetAsync<List<YasnoItem>>(
            $"streets?regionId={regionId}&dsoId={dsoId}&query={Uri.EscapeDataString(query)}", ct) ?? [];

    public async Task<IReadOnlyList<YasnoItem>> SearchHousesAsync(int regionId, int dsoId, int streetId, string query, CancellationToken ct) =>
        await GetAsync<List<YasnoItem>>(
            $"houses?regionId={regionId}&dsoId={dsoId}&streetId={streetId}&query={Uri.EscapeDataString(query)}", ct) ?? [];

    public async Task<YasnoGroup?> GetGroupAsync(int regionId, int dsoId, int streetId, int houseId, CancellationToken ct)
    {
        var group = await GetAsync<YasnoGroup>(
            $"group?regionId={regionId}&dsoId={dsoId}&streetId={streetId}&houseId={houseId}", ct);

        // A 200 without group fields deserializes to 0.0; treat it as "not found".
        return group is { Group: > 0, Subgroup: > 0 } ? group : null;
    }

    private async Task<T?> GetAsync<T>(string relative, CancellationToken ct) where T : class
    {
        try
        {
            using var response = await http.GetAsync(relative, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogInformation("Yasno {Url} returned {Status}", relative, (int)response.StatusCode);
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning("Yasno request failed: {Message}", ex.Message);
            return null;
        }
    }
}
