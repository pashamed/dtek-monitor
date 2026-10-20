using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace DtekBot.Api.Groups;

public interface IDtekAddressClient
{
    Task<IReadOnlyList<string>> SearchCitiesAsync(RegionSettings region, string query, CancellationToken ct);
    Task<IReadOnlyList<string>> SearchStreetsAsync(RegionSettings region, string city, string query, CancellationToken ct);
    Task<IReadOnlyList<DtekAddressHouseDto>> SearchHousesAsync(
        RegionSettings region, string city, string street, string query, CancellationToken ct);
    Task ImportDirectoryAsync(
        RegionSettings region,
        IReadOnlyDictionary<string, IReadOnlyList<string>> streets,
        CancellationToken ct);
}

public sealed class DtekAddressUnavailableException(string regionName, Exception? inner = null)
    : Exception($"Пошук адрес на сайті ДТЕК для регіону «{regionName}» тимчасово недоступний. Спробуйте пізніше.", inner);

public sealed partial class DtekAddressClient(
    HttpClient http,
    IMemoryCache cache,
    BotDbContext db,
    TimeProvider time,
    ILogger<DtekAddressClient> logger) : IDtekAddressClient
{
    private static readonly TimeSpan DirectoryCacheDuration = TimeSpan.FromMinutes(1);

    public async Task ImportDirectoryAsync(
        RegionSettings region,
        IReadOnlyDictionary<string, IReadOnlyList<string>> streets,
        CancellationToken ct)
    {
        var row = await db.AddressDirectories.SingleOrDefaultAsync(entry => entry.RegionId == region.Id, ct);
        var json = JsonSerializer.Serialize(streets);
        if (row is null)
        {
            db.AddressDirectories.Add(new AddressDirectoryState
            {
                RegionId = region.Id,
                Data = json,
                UpdatedAt = time.GetUtcNow(),
            });
        }
        else
        {
            row.Data = json;
            row.UpdatedAt = time.GetUtcNow();
        }

        await db.SaveChangesAsync(ct);
        var directory = streets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        cache.Set(DirectoryCacheKey(region.Id), directory, DirectoryCacheDuration);
    }

    public async Task<IReadOnlyList<string>> SearchCitiesAsync(RegionSettings region, string query, CancellationToken ct)
    {
        var directory = await GetDirectoryAsync(region, ct);
        var search = query.Trim();
        var matches = directory.Keys
            .Where(city => city.Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderBy(city => city, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
        if (matches.Length > 0 || search.Length < 5)
        {
            return matches;
        }

        var normalized = NormalizeCity(search);
        return directory.Keys
            .Select(city => (City: city, Key: NormalizeCity(city)))
            .Where(item => Math.Abs(item.Key.Length - normalized.Length) <= 1
                && EditDistanceAtMostOne(item.Key, normalized))
            .Select(item => item.City)
            .OrderBy(city => city, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> SearchStreetsAsync(
        RegionSettings region, string city, string query, CancellationToken ct)
    {
        var directory = await GetDirectoryAsync(region, ct);
        var streets = directory.FirstOrDefault(entry =>
            string.Equals(entry.Key, city.Trim(), StringComparison.OrdinalIgnoreCase)).Value ?? [];

        return streets
            .Where(street => street.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(street => street, StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToArray();
    }

    public async Task<IReadOnlyList<DtekAddressHouseDto>> SearchHousesAsync(
        RegionSettings region, string city, string street, string query, CancellationToken ct)
    {
        var response = await PostAjaxAsync(region,
        [
            new("method", "getHomeNum"),
            new("data[0][name]", "city"),
            new("data[0][value]", city.Trim()),
            new("data[1][name]", "street"),
            new("data[1][value]", street.Trim()),
        ], ct, includeUpdateFact: true);

        if (!response.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw Unavailable(region, "unexpected house response");
        }

        var houses = new List<DtekAddressHouseDto>();
        foreach (var property in data.EnumerateObject())
        {
            if (!property.Name.Contains(query.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var groups = GroupIds(property.Value).Distinct(StringComparer.Ordinal).ToArray();
            if (groups.Length == 0)
            {
                houses.Add(new DtekAddressHouseDto(property.Name, null));
            }
            else
            {
                houses.AddRange(groups.Select(group => new DtekAddressHouseDto(property.Name, group, groups.Length > 1 ? groups : null)));
            }
        }

        return houses
            .OrderBy(house => house.House, StringComparer.OrdinalIgnoreCase)
            .ThenBy(house => house.GroupId, StringComparer.Ordinal)
            .Take(50)
            .ToArray();
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetDirectoryAsync(
        RegionSettings region, CancellationToken ct) =>
        await cache.GetOrCreateAsync<IReadOnlyDictionary<string, IReadOnlyList<string>>>(
            DirectoryCacheKey(region.Id),
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = DirectoryCacheDuration;
                var stored = await db.AddressDirectories.AsNoTracking()
                    .SingleOrDefaultAsync(row => row.RegionId == region.Id, ct);
                if (stored is not null)
                {
                    var persisted = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(stored.Data)
                        ?? throw new JsonException($"Stored DTEK address directory for {region.Id} is empty.");
                    return persisted.ToDictionary(
                        pair => pair.Key,
                        pair => (IReadOnlyList<string>)pair.Value,
                        StringComparer.OrdinalIgnoreCase);
                }

                var response = await PostAjaxAsync(region, [new("method", "getStreets")], ct);
                if (!response.TryGetProperty("streets", out var streets) || streets.ValueKind != JsonValueKind.Object)
                {
                    throw Unavailable(region, "unexpected street directory");
                }

                var directory = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var city in streets.EnumerateObject())
                {
                    if (city.Value.ValueKind == JsonValueKind.Array)
                    {
                        directory[city.Name] = city.Value.EnumerateArray()
                            .Where(street => street.ValueKind == JsonValueKind.String)
                            .Select(street => street.GetString()!)
                            .ToArray();
                    }
                }

                await ImportDirectoryAsync(region, directory, ct);
                return directory;
            })
        ?? throw new DtekAddressUnavailableException(region.Name);

    private static string DirectoryCacheKey(string regionId) => $"dtek-addresses:{regionId}";

    private async Task<JsonElement> PostAjaxAsync(
        RegionSettings region,
        IEnumerable<KeyValuePair<string, string>> fields,
        CancellationToken ct,
        bool includeUpdateFact = false)
    {
        try
        {
            if (!Uri.TryCreate(region.SiteUrl, UriKind.Absolute, out var pageUri)
                || pageUri.Scheme != Uri.UriSchemeHttps
                || !pageUri.Host.StartsWith("www.dtek-", StringComparison.OrdinalIgnoreCase)
                || !pageUri.Host.EndsWith(".com.ua", StringComparison.OrdinalIgnoreCase))
            {
                throw Unavailable(region, "invalid configured site URL");
            }

            using var pageResponse = await http.GetAsync(pageUri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!pageResponse.IsSuccessStatusCode)
            {
                throw Unavailable(region, $"site returned HTTP {(int)pageResponse.StatusCode}");
            }

            var html = await pageResponse.Content.ReadAsStringAsync(ct);
            var csrf = CsrfToken().Match(html);
            if (!csrf.Success)
            {
                throw Unavailable(region, "CSRF token missing from site response");
            }

            var requestFields = fields;
            if (includeUpdateFact)
            {
                var updateFact = UpdateFact().Match(html);
                if (!updateFact.Success)
                {
                    throw Unavailable(region, "updateFact missing from site response");
                }

                requestFields = fields.Concat(
                [
                    new("data[2][name]", "updateFact"),
                    new("data[2][value]", WebUtility.HtmlDecode(updateFact.Groups[1].Value)),
                ]);
            }

            var cookies = pageResponse.Headers.TryGetValues("Set-Cookie", out var values)
                ? string.Join("; ", values.Select(value => value.Split(';', 2)[0]))
                : null;
            var ajaxUri = new Uri(pageUri, "/ua/ajax");
            using var request = new HttpRequestMessage(HttpMethod.Post, ajaxUri)
            {
                Content = new FormUrlEncodedContent(requestFields),
            };
            request.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
            request.Headers.TryAddWithoutValidation("X-CSRF-Token", WebUtility.HtmlDecode(csrf.Groups[1].Value));
            request.Headers.Referrer = pageUri;
            if (cookies is not null)
            {
                request.Headers.TryAddWithoutValidation("Cookie", cookies);
            }

            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                throw Unavailable(region, $"AJAX endpoint returned HTTP {(int)response.StatusCode}");
            }

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = document.RootElement;
            if (!root.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.True)
            {
                throw Unavailable(region, "AJAX request was rejected");
            }

            return root.Clone();
        }
        catch (DtekAddressUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning("DTEK address lookup failed for region {RegionId}: {ErrorType}", region.Id, ex.GetType().Name);
            throw new DtekAddressUnavailableException(region.Name, ex);
        }
    }

    private DtekAddressUnavailableException Unavailable(RegionSettings region, string reason)
    {
        logger.LogWarning("DTEK address lookup unavailable for region {RegionId}: {Reason}", region.Id, reason);
        return new DtekAddressUnavailableException(region.Name);
    }

    private static IEnumerable<string> GroupIds(JsonElement house)
    {
        if (!house.TryGetProperty("sub_type_reason", out var reasons) || reasons.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var reason in reasons.EnumerateArray())
        {
            if (reason.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var match = GroupId().Match(reason.GetString()!);
            if (match.Success)
            {
                yield return match.Groups[1].Value;
            }
        }
    }

    private static string NormalizeCity(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        foreach (var prefix in new[] { "місто ", "м. ", "м ", "город ", "г. ", "г " })
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                normalized = normalized[prefix.Length..];
                break;
            }
        }

        return new string(normalized
            .Replace('і', 'и')
            .Replace('ї', 'и')
            .Replace('є', 'е')
            .Replace('ґ', 'г')
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static bool EditDistanceAtMostOne(string left, string right)
    {
        var leftIndex = 0;
        var rightIndex = 0;
        var differences = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (left[leftIndex] == right[rightIndex])
            {
                leftIndex++;
                rightIndex++;
                continue;
            }

            if (++differences > 1)
            {
                return false;
            }

            if (left.Length > right.Length)
            {
                leftIndex++;
            }
            else if (right.Length > left.Length)
            {
                rightIndex++;
            }
            else
            {
                leftIndex++;
                rightIndex++;
            }
        }

        return differences + (leftIndex < left.Length || rightIndex < right.Length ? 1 : 0) <= 1;
    }

    [GeneratedRegex("<meta\\s+name=[\"']csrf-token[\"']\\s+content=[\"']([^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex CsrfToken();

    [GeneratedRegex("<input\\b(?=[^>]*\\bname=[\"']updateFact[\"'])(?=[^>]*\\bvalue=[\"']([^\"']*)[\"'])[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex UpdateFact();

    [GeneratedRegex("^GPV\\s*(\\d{1,2}\\.\\d{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex GroupId();
}
