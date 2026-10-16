using System.Globalization;
using System.Text.Json;

namespace DtekBot.Api.Schedules;

public sealed record OutageInterval(DateTimeOffset Start, DateTimeOffset End);

public sealed record GroupSchedule(string GroupId, IReadOnlyList<OutageInterval> Outages);

// Where a group stands at a given moment, according to the published schedule.
// Since/Until are null when the moment lies outside what the data covers (before today / after tomorrow).
public sealed record ScheduleStatus(bool IsOff, DateTimeOffset? Since, DateTimeOffset? Until, DateTimeOffset? PreviousEnd, DateTimeOffset? NextStart);

public sealed record ScheduleSnapshot(
    string DataFile,
    string Version,
    DateTimeOffset FetchedAt,
    string? SourceUpdatedText,
    DateTimeOffset CoverageStart,
    DateTimeOffset CoverageEnd,
    IReadOnlyDictionary<string, GroupSchedule> Groups,
    IReadOnlyDictionary<string, string>? WeeklyPresets = null,
    IReadOnlyDictionary<string, string>? WeeklyPresetsWithMaybe = null)
{
    // The group's typical week as Svitlobot rows (see WeeklyPreset); null when the feed has no preset for it.
    public string? WeeklyTimetable(string groupId, bool includeMaybe = false)
    {
        var source = includeMaybe ? WeeklyPresetsWithMaybe : WeeklyPresets;
        return source is not null && source.TryGetValue(groupId, out var rows) ? rows : null;
    }

    // Same numbering as Yasno and DTEK ("45.1"); the data file prefixes it with "GPV".
    public GroupSchedule? Find(string groupId) =>
        Groups.TryGetValue(groupId, out var group) ? group : null;

    public IEnumerable<string> GroupIds => Groups.Keys.OrderBy(GroupSort);

    public ScheduleStatus? StatusAt(string groupId, DateTimeOffset now)
    {
        var group = Find(groupId);
        if (group is null || now < CoverageStart || now >= CoverageEnd)
        {
            return null;
        }

        var current = group.Outages.FirstOrDefault(o => o.Start <= now && now < o.End);
        var previous = group.Outages.LastOrDefault(o => o.End <= now);
        var next = group.Outages.FirstOrDefault(o => o.Start > now);

        // An outage that touches the edge of the data may continue beyond it.
        DateTimeOffset? Edge(DateTimeOffset value, DateTimeOffset edge) => value == edge ? null : value;

        if (current is not null)
        {
            return new ScheduleStatus(
                true,
                Edge(current.Start, CoverageStart),
                Edge(current.End, CoverageEnd),
                previous?.End,
                next?.Start);
        }

        return new ScheduleStatus(
            false,
            previous?.End,
            next?.Start,
            previous?.End,
            next?.Start);
    }

    private static (int, int) GroupSort(string id)
    {
        var parts = id.Split('.');
        return (int.TryParse(parts[0], out var a) ? a : int.MaxValue, parts.Length > 1 && int.TryParse(parts[1], out var b) ? b : 0);
    }
}

public static class ScheduleParser
{
    private const string GroupPrefix = "GPV";

    // Well-formed JSON of an unexpected shape yields null; malformed JSON still throws JsonException.
    public static ScheduleSnapshot? Parse(string json, string dataFile, DateTimeOffset fetchedAt)
    {
        try
        {
            return ParseCore(json, dataFile, fetchedAt);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    // Hour key "h" covers [h-1:00, h:00). "no" = off for the hour, "first"/"second" = off for the first/second half.
    // "yes" and the "maybe" variants count as power on: only a firm outage is reported.
    private static ScheduleSnapshot? ParseCore(string json, string dataFile, DateTimeOffset fetchedAt)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("fact", out var fact) || !fact.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var raw = new Dictionary<string, List<OutageInterval>>(StringComparer.Ordinal);
        DateTimeOffset? first = null, last = null;

        foreach (var day in data.EnumerateObject())
        {
            if (!long.TryParse(day.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            {
                continue;
            }

            var dayStart = DateTimeOffset.FromUnixTimeSeconds(epoch);
            first = first is null || dayStart < first ? dayStart : first;
            last = last is null || dayStart.AddDays(1) > last ? dayStart.AddDays(1) : last;

            foreach (var group in day.Value.EnumerateObject())
            {
                if (!group.Name.StartsWith(GroupPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var id = group.Name[GroupPrefix.Length..];
                if (!raw.TryGetValue(id, out var list))
                {
                    raw[id] = list = [];
                }

                foreach (var hour in group.Value.EnumerateObject())
                {
                    if (!int.TryParse(hour.Name, out var h) || h is < 1 or > 24)
                    {
                        continue;
                    }

                    var start = dayStart.AddMinutes((h - 1) * 60);
                    switch (hour.Value.GetString())
                    {
                        case "no": list.Add(new(start, start.AddMinutes(60))); break;
                        case "first": list.Add(new(start, start.AddMinutes(30))); break;
                        case "second": list.Add(new(start.AddMinutes(30), start.AddMinutes(60))); break;
                    }
                }
            }
        }

        if (first is null || last is null || raw.Count == 0)
        {
            return null;
        }

        var groups = raw.ToDictionary(p => p.Key, p => new GroupSchedule(p.Key, Merge(p.Value)));
        var version = root.TryGetProperty("meta", out var meta) && meta.TryGetProperty("contentHash", out var hash)
            ? hash.GetString() ?? ""
            : "";
        var updated = fact.TryGetProperty("update", out var update) ? update.GetString() : null;

        return new ScheduleSnapshot(dataFile, version, fetchedAt, updated, first.Value, last.Value, groups, WeeklyPresets: WeeklyPreset.BuildAll(root), WeeklyPresetsWithMaybe: WeeklyPreset.BuildAll(root, includeMaybe: true));
    }

    // Adjacent pieces (hour by hour, across midnight) become one outage.
    private static List<OutageInterval> Merge(List<OutageInterval> pieces)
    {
        var merged = new List<OutageInterval>();
        foreach (var piece in pieces.OrderBy(p => p.Start))
        {
            if (merged.Count > 0 && merged[^1].End >= piece.Start)
            {
                merged[^1] = merged[^1] with { End = piece.End > merged[^1].End ? piece.End : merged[^1].End };
            }
            else
            {
                merged.Add(piece);
            }
        }

        return merged;
    }
}
