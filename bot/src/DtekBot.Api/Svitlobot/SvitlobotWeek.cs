using System.Globalization;
using DtekBot.Api.Data;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;

namespace DtekBot.Api.Svitlobot;

// The Monday-to-Sunday matrix sent to Svitlobot. A day with a published daily schedule uses it; other days use the
// weekly preset. Published days are remembered so earlier days of the week keep their real schedule after the feed
// drops them.
public static class SvitlobotWeek
{
    private const string DayKeyPrefix = "svitlobot-day:";

    public static async Task<string?> BuildAsync(BotDbContext db, ScheduleSnapshot snapshot, string regionId,
        string groupId, bool includeMaybe, DateTimeOffset now, CancellationToken ct)
    {
        var monday = WeekStart(now);
        var published = PublishedDays(snapshot, groupId, monday);
        var keys = Enumerable.Range(0, WeeklyPreset.Days).Select(day => DayKey(regionId, groupId, day)).ToArray();
        var stored = await db.KeyValues.Where(item => keys.Contains(item.Key)).ToDictionaryAsync(item => item.Key, ct);

        var rows = new Dictionary<int, string>();
        var remember = false;
        for (var day = 0; day < WeeklyPreset.Days; day++)
        {
            var date = monday.AddDays(day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            stored.TryGetValue(keys[day], out var remembered);
            if (published.TryGetValue(day, out var row))
            {
                rows[day] = row;
                var value = date + ":" + row;
                if (remembered is null)
                {
                    db.KeyValues.Add(new KeyValue { Key = keys[day], Value = value });
                    remember = true;
                }
                else if (remembered.Value != value)
                {
                    remembered.Value = value;
                    remember = true;
                }
            }
            else if (remembered?.Value is { } value && value.StartsWith(date + ":", StringComparison.Ordinal))
            {
                rows[day] = value[(date.Length + 1)..];
            }
        }

        if (remember)
        {
            await db.SaveChangesAsync(ct);
        }

        var preset = WeeklyPreset.Union(snapshot, [groupId], includeMaybe);
        if (preset is null && rows.Count == 0)
        {
            return null;
        }

        var result = (preset ?? string.Join(';', Enumerable.Repeat(new string('0', WeeklyPreset.Hours), WeeklyPreset.Days)))
            .Split(';');
        foreach (var (day, row) in rows)
        {
            result[day] = row;
        }

        return string.Join(';', result);
    }

    // Published days of the week starting at monday, keyed 0 (Monday) to 6 (Sunday).
    public static Dictionary<int, string> PublishedDays(ScheduleSnapshot snapshot, string groupId, DateTime monday)
    {
        var result = new Dictionary<int, string>();
        if (snapshot.Find(groupId) is not { } group)
        {
            return result;
        }

        for (var day = 0; day < WeeklyPreset.Days; day++)
        {
            var start = ScheduleText.KyivDayStart(monday.AddDays(day));
            var end = ScheduleText.KyivDayStart(monday.AddDays(day + 1));
            if (snapshot.CoversDay(groupId, start, end))
            {
                result[day] = Row(group, start);
            }
        }

        return result;
    }

    // 24 Svitlobot digits for the day; hour h covers [start + h, start + h + 1), like the feed's hour keys.
    public static string Row(GroupSchedule group, DateTimeOffset dayStart)
    {
        var row = new char[WeeklyPreset.Hours];
        for (var hour = 0; hour < WeeklyPreset.Hours; hour++)
        {
            var first = Off(group, dayStart.AddMinutes(hour * 60), dayStart.AddMinutes(hour * 60 + 30));
            var second = Off(group, dayStart.AddMinutes(hour * 60 + 30), dayStart.AddMinutes(hour * 60 + 60));
            row[hour] = (first, second) switch
            {
                (true, true) => '1',
                (true, false) => '2',
                (false, true) => '3',
                _ => '0',
            };
        }

        return new string(row);
    }

    public static DateTime WeekStart(DateTimeOffset now)
    {
        var date = KyivTime.FromUtc(now).Date;
        return date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
    }

    private static bool Off(GroupSchedule group, DateTimeOffset start, DateTimeOffset end) =>
        group.Outages.Any(outage => outage.Start < end && outage.End > start);

    private static string DayKey(string regionId, string groupId, int day) => $"{DayKeyPrefix}{regionId}:{groupId}:{day}";
}
