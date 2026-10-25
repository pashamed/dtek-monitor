using System.Collections.Concurrent;
using System.Net;
using DtekBot.Api.Checks;
using DtekBot.Api.Settings;
using HtmlAgilityPack;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Parsing;

public sealed record DtekPopupObservation(
    string RegionId, string? PopupText, DateTimeOffset ObservedAt, StructuredPopup? Structured = null);

public interface IDtekPopupCollector
{
    Task<IReadOnlyList<DtekPopupObservation>> CollectAsync(CancellationToken ct);
}

// Pending browser replies are correlated to one runtime cycle, never to the next one.
public sealed class DtekPopupCollector(
    IHttpClientFactory http,
    IServiceScopeFactory scopes,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time,
    ILogger<DtekPopupCollector> logger) : IDtekPopupCollector
{
    public const string ClientName = "dtek-popups";
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DtekPopupObservation>> pending = new();

    public bool Accept(string? collectionId, DtekPopupObservation observation)
    {
        if (collectionId is null || !pending.TryGetValue(collectionId + ":" + observation.RegionId, out var reply))
            return false;
        reply.TrySetResult(observation);
        return true;
    }

    public async Task<IReadOnlyList<DtekPopupObservation>> CollectAsync(CancellationToken ct)
    {
        var regions = options.CurrentValue.Regions.Where(r => !string.IsNullOrWhiteSpace(r.SiteUrl)).ToArray();
        var direct = await Task.WhenAll(regions.Select(r => FetchAsync(r, ct)));
        var results = direct.OfType<DtekPopupObservation>().ToList();
        var missing = regions.Where((_, index) => direct[index] is null).ToArray();
        if (missing.Length == 0) return results;

        var collectionId = Guid.NewGuid().ToString("N");
        var replies = missing.ToDictionary(r => r.Id, _ =>
            new TaskCompletionSource<DtekPopupObservation>(TaskCreationOptions.RunContinuationsAsynchronously));
        foreach (var (regionId, reply) in replies) pending[collectionId + ":" + regionId] = reply;
        try
        {
            using var scope = scopes.CreateScope();
            var triggered = await scope.ServiceProvider.GetRequiredService<IWorkflowTrigger>().TriggerAsync(
                "check-regions", ct, new { collectionId, regionIds = missing.Select(r => r.Id).ToArray() });
            if (!triggered)
            {
                logger.LogWarning("DTEK browser fallback could not start; preserving {Count} region states", missing.Length);
                return results;
            }

            var timeout = options.CurrentValue.Timers.BrowserWait;
            try
            {
                await Task.WhenAll(replies.Values.Select(r => r.Task)).WaitAsync(timeout, time, ct);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("DTEK browser fallback timed out; unavailable regions: {Regions}",
                    string.Join(",", replies.Where(r => !r.Value.Task.IsCompletedSuccessfully).Select(r => r.Key)));
            }

            results.AddRange(replies.Values.Where(r => r.Task.IsCompletedSuccessfully).Select(r => r.Task.Result));
            return results;
        }
        finally
        {
            foreach (var regionId in replies.Keys) pending.TryRemove(collectionId + ":" + regionId, out _);
        }
    }

    private async Task<DtekPopupObservation?> FetchAsync(RegionSettings region, CancellationToken ct)
    {
        try
        {
            using var response = await http.CreateClient(ClientName).GetAsync(region.SiteUrl, ct);
            if (response.IsSuccessStatusCode
                && TryReadPopup(await response.Content.ReadAsStringAsync(ct), out var text))
                return new(region.Id, text, time.GetUtcNow());

            logger.LogWarning("DTEK {Region}: HTTP {Status} or non-site HTML; requesting browser fallback",
                region.Id, (int)response.StatusCode);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested) throw;
            logger.LogWarning(ex, "DTEK popup fetch failed for {Region}", region.Id);
            return null;
        }
    }

    public static bool TryReadPopup(string html, out string? popupText)
    {
        popupText = null;
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        // Imperva can return HTTP 200. Only an actual shutdown page can clear an alert.
        if (doc.DocumentNode.SelectSingleNode("//meta[@name='csrf-token']") is null
            || !html.Contains("DisconSchedule", StringComparison.Ordinal))
            return false;

        var modals = doc.DocumentNode.SelectNodes(
            "//*[contains(concat(' ',normalize-space(@class),' '),' m-attention__container ')"
            + " and contains(concat(' ',normalize-space(@class),' '),' modal__container--firstPopup ')]");
        if (modals is null) return true;
        foreach (var modal in modals)
        {
            var title = modal.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' modal__title ')]");
            var text = modal.SelectSingleNode(".//*[contains(concat(' ',normalize-space(@class),' '),' m-attention__text ')]");
            if (text is null) return false;
            foreach (var br in text.SelectNodes(".//br")?.ToArray() ?? [])
                if (br.ParentNode is { } parent) parent.ReplaceChild(doc.CreateTextNode("\n"), br);
            var value = string.Join("\n\n", new[] { title?.InnerText, text.InnerText }
                .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => WebUtility.HtmlDecode(t!).Trim()));
            if (value.Length > 0) { popupText = value; return true; }
        }
        return true;
    }
}
