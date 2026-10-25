using System.Collections.Concurrent;
using System.Net;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Schedules;

public interface IScheduleProvider
{
    // Null when the region has no schedule data or nothing usable could be fetched.
    Task<ScheduleSnapshot?> GetAsync(string regionId, CancellationToken ct);
    Task<ScheduleSnapshot?> RefreshAsync(string regionId, CancellationToken ct) => GetAsync(regionId, ct);
}

// One shared copy per region: requests inside the cache window cost nothing, and a refresh is a conditional GET
// (ETag), so many chats never turn into many requests to GitHub or, through it, to the DTEK site.
public sealed class ScheduleProvider(
    IHttpClientFactory httpFactory,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time,
    ILogger<ScheduleProvider> logger) : IScheduleProvider
{
    public const string ClientName = "outage-data";

    private sealed class Entry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public ScheduleSnapshot? Snapshot;
        public string? ETag;
        public DateTimeOffset CheckedAt;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    public Task<ScheduleSnapshot?> GetAsync(string regionId, CancellationToken ct) => GetCoreAsync(regionId, false, ct);

    public Task<ScheduleSnapshot?> RefreshAsync(string regionId, CancellationToken ct) => GetCoreAsync(regionId, true, ct);

    private async Task<ScheduleSnapshot?> GetCoreAsync(string regionId, bool force, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var region = settings.Regions.FirstOrDefault(r => r.Id == regionId);
        if (region is null || !region.HasSchedules)
        {
            return null;
        }

        var entry = _entries.GetOrAdd(region.OutageDataFile, _ => new Entry());
        var now = time.GetUtcNow();
        var ttl = TimeSpan.FromMinutes(settings.Schedules.CacheMinutes);
        var maxStale = TimeSpan.FromMinutes(settings.Schedules.MaxStaleMinutes);

        // A recent attempt counts even when it failed, so a broken source is not hammered by every caller.
        if (!force && WithinWindow(entry, now, ttl))
        {
            return Usable(entry, now, maxStale);
        }

        await entry.Gate.WaitAsync(ct);
        try
        {
            now = time.GetUtcNow();
            if (!force && WithinWindow(entry, now, ttl))
            {
                return Usable(entry, now, maxStale);
            }

            await RefreshAsync(region, settings.Schedules, entry, now, ct);
            return Usable(entry, now, maxStale);
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static bool WithinWindow(Entry entry, DateTimeOffset now, TimeSpan ttl) =>
        entry.CheckedAt != default && now - entry.CheckedAt < ttl - TickJitter;

    // Monitoring cycles start a few milliseconds early or late; without this slack a cycle that lands just
    // before the window ends would skip the check and push it to the following cycle.
    public static readonly TimeSpan TickJitter = TimeSpan.FromSeconds(5);

    // The last good copy, unless it is older than the limit.
    private static ScheduleSnapshot? Usable(Entry entry, DateTimeOffset now, TimeSpan maxStale) =>
        entry.Snapshot is not null && now - entry.Snapshot.FetchedAt <= maxStale ? entry.Snapshot : null;

    private async Task RefreshAsync(RegionSettings region, ScheduleSettings settings, Entry entry, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            var url = $"{settings.BaseUrl.TrimEnd('/')}/data/{region.OutageDataFile}.json";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (entry.ETag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", entry.ETag);
            }

            using var response = await httpFactory.CreateClient(ClientName).SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotModified && entry.Snapshot is not null)
            {
                entry.CheckedAt = now;
                entry.Snapshot = entry.Snapshot with { FetchedAt = now };
                return;
            }

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Schedule data for {File} returned HTTP {Status}", region.OutageDataFile, (int)response.StatusCode);
                entry.CheckedAt = now;
                return;
            }

            var parsed = ScheduleParser.Parse(await response.Content.ReadAsStringAsync(ct), region.OutageDataFile, now);
            entry.CheckedAt = now;
            if (parsed is null)
            {
                logger.LogWarning("Schedule data for {File} had an unexpected shape", region.OutageDataFile);
                return;
            }

            entry.Snapshot = parsed;
            entry.ETag = response.Headers.ETag?.ToString();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            // Back off for one cache window instead of retrying on every request.
            entry.CheckedAt = now;
            logger.LogWarning("Schedule data refresh for {File} failed: {Message}", region.OutageDataFile, ex.Message);
        }
    }
}
