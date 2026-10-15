using System.Text;
using System.Text.Json;
using DtekBot.Api.Schedules;

namespace DtekBot.Tests;

public sealed class FakeScheduleProvider : IScheduleProvider
{
    public Dictionary<string, ScheduleSnapshot?> Snapshots { get; } = [];

    public List<string> Calls { get; } = [];

    public Task<ScheduleSnapshot?> GetAsync(string regionId, CancellationToken ct)
    {
        Calls.Add(regionId);
        return Task.FromResult(Snapshots.GetValueOrDefault(regionId));
    }
}

public static class ScheduleFixtures
{
    public const string FixturePath = "Fixtures/outage-data-kyiv-sample.json";

    // Day keys of the real sample: 15 and 16 October 2026, 00:00 Kyiv time.
    public const long Day1 = 1792011600;
    public const long Day2 = 1792098000;

    public const string RealVersion = "1991457b44082fffd56b5ba30da9117b7ce609512969f291296a554880cd717a";

    public static readonly TimeSpan Summer = TimeSpan.FromHours(3);

    public static string RealJson() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, FixturePath), Encoding.UTF8);

    public static ScheduleSnapshot RealSnapshot(DateTimeOffset? fetchedAt = null, string dataFile = "kyiv") =>
        ScheduleParser.Parse(RealJson(), dataFile, fetchedAt ?? Kyiv(15, 20, 30))!;

    // A moment on the given day of October 2026, Kyiv time (UTC+3 until 25 October).
    public static DateTimeOffset Kyiv(int day, int hour, int minute = 0, int second = 0) =>
        new(2026, 10, day, hour, minute, second, Summer);

    // One day of data for a single group: every hour is "yes" unless overridden.
    public static string Json(
        string? version,
        string? updated,
        params (long Epoch, string Group, (int Hour, string Value)[] Hours)[] days)
    {
        var data = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>();
        foreach (var (epoch, group, hours) in days)
        {
            var key = epoch.ToString();
            if (!data.TryGetValue(key, out var groups))
            {
                data[key] = groups = [];
            }

            var values = Enumerable.Range(1, 24).ToDictionary(h => h.ToString(), _ => "yes");
            foreach (var (hour, value) in hours)
            {
                values[hour.ToString()] = value;
            }

            groups["GPV" + group] = values;
        }

        var fact = new Dictionary<string, object?> { ["data"] = data };
        if (updated is not null)
        {
            fact["update"] = updated;
        }

        var root = new Dictionary<string, object?> { ["fact"] = fact };
        if (version is not null)
        {
            root["meta"] = new Dictionary<string, string> { ["contentHash"] = version };
        }

        return JsonSerializer.Serialize(root);
    }

    public static (int Hour, string Value)[] Hours(string value, params int[] hours) =>
        hours.Select(h => (h, value)).ToArray();

    public static ScheduleSnapshot? Parse(string json, string dataFile = "kyiv", DateTimeOffset? fetchedAt = null)
    {
        try
        {
            return ScheduleParser.Parse(json, dataFile, fetchedAt ?? Kyiv(15, 12));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
