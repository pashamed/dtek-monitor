using System.Text;
using System.Text.Json;

namespace DtekBot.Api.Schedules;

// The weekly "preset" of the outage-data feed: preset.data["GPV45.1"]["1".."7"]["1".."24"], day 1 = Monday,
// hour key h covers [h-1:00, h:00) (same as the daily data).
public static class WeeklyPreset
{
    public const int Days = 7;
    public const int Hours = 24;

    // Svitlobot digits: 0 = no planned outage, 1 = the whole hour, 2 = the first 30 minutes, 3 = the last 30 minutes.
    // Firm outages ("no", "first", "second") always count; with includeMaybe the "maybe", "mfirst" and "msecond"
    // variants count the same way. "yes" is always 0.
    public static char Digit(string? value, bool includeMaybe = false) => value switch
    {
        "no" => '1',
        "first" => '2',
        "second" => '3',
        "maybe" when includeMaybe => '1',
        "mfirst" when includeMaybe => '2',
        "msecond" when includeMaybe => '3',
        _ => '0',
    };

    // Seven rows (Monday first) of 24 digits joined by ';', or null when the feed has no preset for the group.
    public static string? Build(JsonElement presetData, string groupId, bool includeMaybe = false)
    {
        if (presetData.ValueKind != JsonValueKind.Object
            || !presetData.TryGetProperty("GPV" + groupId, out var group)
            || group.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rows = new StringBuilder(Days * (Hours + 1));
        var anyDay = false;
        for (var day = 1; day <= Days; day++)
        {
            if (day > 1)
            {
                rows.Append(';');
            }

            var present = group.TryGetProperty(day.ToString(), out var dayData) && dayData.ValueKind == JsonValueKind.Object;
            anyDay |= present;
            for (var hour = 1; hour <= Hours; hour++)
            {
                var value = present && dayData.TryGetProperty(hour.ToString(), out var cell) && cell.ValueKind == JsonValueKind.String
                    ? cell.GetString()
                    : null;
                rows.Append(Digit(value, includeMaybe));
            }
        }

        return anyDay ? rows.ToString() : null;
    }

    // Every group of the preset section, keyed like ScheduleSnapshot.Groups ("45.1").
    public static IReadOnlyDictionary<string, string> BuildAll(JsonElement root, bool includeMaybe = false)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("preset", out var preset) && preset.ValueKind == JsonValueKind.Object
            && preset.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
        {
            foreach (var group in data.EnumerateObject())
            {
                if (group.Name.StartsWith("GPV", StringComparison.Ordinal) && group.Name.Length > 3
                    && Build(data, group.Name[3..], includeMaybe) is { } rows)
                {
                    result[group.Name[3..]] = rows;
                }
            }
        }

        return result;
    }

    public static string? Union(ScheduleSnapshot snapshot, IEnumerable<string> groupIds, bool includeMaybe = false)
    {
        var weeks = groupIds.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(id => snapshot.WeeklyTimetable(id, includeMaybe)).ToArray();
        if (weeks.Length == 0 || weeks.Any(w => w is null)) return null;
        var result = weeks[0]!.ToCharArray();
        for (var i = 0; i < result.Length; i++)
        {
            if (result[i] == ';') continue;
            var mask = 0;
            foreach (var week in weeks)
            {
                if (week!.Length != result.Length) return null;
                mask |= week[i] switch { '1' => 3, '2' => 1, '3' => 2, _ => 0 };
            }
            result[i] = mask switch { 3 => '1', 1 => '2', 2 => '3', _ => '0' };
        }
        return new string(result);
    }

    public static bool HasOutages(string timetable) => timetable.Any(c => c is '1' or '2' or '3');

    // Svitlobot stores a matrix only when it has at least one full outage hour.
    public static bool IsStorable(string timetable) => timetable.Contains('1');
}
