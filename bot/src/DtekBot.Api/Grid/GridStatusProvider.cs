using System.Net;
using System.Text.Json;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Grid;

public sealed record GridRegionStatus(string Slug, string Status, string? Title, DateTimeOffset? Since);

public sealed record GridFeed(DateTimeOffset GeneratedAt, IReadOnlyDictionary<string, GridRegionStatus> Regions);

public interface IGridStatusProvider
{
    // Null when nothing usable could be fetched: the caller then changes nothing.
    Task<GridFeed?> GetAsync(CancellationToken ct);
    Task<GridFeed?> RefreshAsync(CancellationToken ct) => GetAsync(ct);
}

public static class GridFeedParser
{
    public static GridFeed? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("regions", out var regions)
                || regions.ValueKind != JsonValueKind.Array
                || Time(root, "generated_at") is not { } generatedAt)
            {
                return null;
            }

            var titles = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("statuses", out var statuses) && statuses.ValueKind == JsonValueKind.Object)
            {
                foreach (var s in statuses.EnumerateObject())
                {
                    if (s.Value.ValueKind == JsonValueKind.Object && Text(s.Value, "title_uk") is { } t)
                    {
                        titles[s.Name] = t;
                    }
                }
            }

            var map = new Dictionary<string, GridRegionStatus>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in regions.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object || Text(r, "slug") is not { } slug || Text(r, "status") is not { } status)
                {
                    continue;
                }

                map[slug] = new GridRegionStatus(
                    slug, status, Text(r, "title_uk") ?? titles.GetValueOrDefault(status), Time(r, "since"));
            }

            return new GridFeed(generatedAt, map);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
            ? v.GetString()!.Trim()
            : null;

    private static DateTimeOffset? Time(JsonElement e, string name) =>
        Text(e, name) is { } s
        && DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUniversalTime()
            : null;
}

// One shared copy of the feed: calls inside the cache window cost nothing and a refresh is a conditional GET (ETag).
public sealed class GridStatusProvider(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time,
    ILogger<GridStatusProvider> logger) : IGridStatusProvider
{
    public const string ClientName = "grid-status";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private GridFeed? _feed;
    private string? _etag;
    private DateTimeOffset _checkedAt;

    public Task<GridFeed?> GetAsync(CancellationToken ct) => GetCoreAsync(false, ct);

    public Task<GridFeed?> RefreshAsync(CancellationToken ct) => GetCoreAsync(true, ct);

    private async Task<GridFeed?> GetCoreAsync(bool force, CancellationToken ct)
    {
        var settings = options.CurrentValue.Grid;
        var ttl = TimeSpan.FromMinutes(settings.CacheMinutes);
        if (!force && WithinWindow(ttl))
        {
            return _feed;
        }

        await _gate.WaitAsync(ct);
        try
        {
            if (!force && WithinWindow(ttl))
            {
                return _feed;
            }

            await RefreshAsync(settings, ct);
            return _feed;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool WithinWindow(TimeSpan ttl) => _checkedAt != default && time.GetUtcNow() - _checkedAt < ttl;

    private async Task RefreshAsync(GridSettings settings, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.FeedUrl);
            if (_etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", _etag);
            }

            using var response = await httpFactory.CreateClient(ClientName).SendAsync(request, ct);
            _checkedAt = now;
            if (response.StatusCode == HttpStatusCode.NotModified && _feed is not null)
            {
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Grid status feed returned HTTP {Status}", (int)response.StatusCode);
                _feed = null;
                _etag = null;
                return;
            }

            var parsed = GridFeedParser.Parse(await response.Content.ReadAsStringAsync(ct));
            if (parsed is null)
            {
                logger.LogWarning("Grid status feed had an unexpected shape");
            }

            _feed = parsed;
            _etag = parsed is null ? null : response.Headers.ETag?.ToString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            // Back off for one cache window; the caller sees "nothing usable" and leaves the state alone.
            _checkedAt = now;
            _feed = null;
            _etag = null;
            logger.LogWarning("Grid status feed refresh failed: {Message}", ex.Message);
        }
    }
}
