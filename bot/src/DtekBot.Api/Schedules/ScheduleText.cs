using System.Globalization;
using DtekBot.Api.Settings;
using DtekBot.Shared;

namespace DtekBot.Api.Schedules;

// Wording and picture links for schedule messages. Kept free of I/O so it is easy to test.
public static class ScheduleText
{
    private static readonly string[] Weekdays =
        ["Неділя", "Понеділок", "Вівторок", "Середа", "Четвер", "Пʼятниця", "Субота"];

    public const string NoOutages = "🟢 Відключень не заплановано";

    // Compact style used in the channel messages: "17год 23хв".
    public static string Duration(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes);
        if (minutes < 1)
        {
            return "<1хв";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        return (hours, rest) switch
        {
            (0, _) => $"{rest}хв",
            (_, 0) => $"{hours}год",
            _ => $"{hours}год {rest}хв",
        };
    }

    // Rounded length for the outage lists: "~4 год", "~3,5 год", "~45 хв".
    public static string Approx(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes);
        var hours = minutes / 60;
        var rest = minutes % 60;
        return (hours, rest) switch
        {
            (0, _) => $"~{rest} хв",
            (_, 0) => $"~{hours} год",
            (_, 30) => $"~{hours},5 год",
            _ => $"~{hours} год {rest} хв",
        };
    }

    public static string Clock(DateTimeOffset moment) => KyivTime.FromUtc(moment).ToString("HH:mm");

    // "00:00 (16.10)" when the moment is not today (Kyiv time), "14:00" otherwise, so it reads well after "о".
    public static string ClockWithDay(DateTimeOffset moment, DateTimeOffset now)
    {
        var local = KyivTime.FromUtc(moment);
        return local.Date == KyivTime.FromUtc(now).Date ? local.ToString("HH:mm") : $"{local:HH:mm} ({local:dd.MM})";
    }

    // Kyiv midnight of the date, as an instant; the offset is tried both ways because of the summer-time change.
    public static DateTimeOffset KyivDayStart(DateTime date)
    {
        foreach (var hours in new[] { 2, 3 })
        {
            var candidate = new DateTimeOffset(date.Date, TimeSpan.FromHours(hours));
            if (KyivTime.FromUtc(candidate).Offset == candidate.Offset)
            {
                return candidate;
            }
        }

        return new DateTimeOffset(date.Date, TimeSpan.FromHours(2));
    }

    public static string DateKey(DateTimeOffset now) => KyivTime.FromUtc(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // One line per outage inside the day: "🔴 14:00 - 18:00 (~4 год)"; a midnight end reads "24:00".
    public static IReadOnlyList<string> OutageLines(GroupSchedule? group, DateTimeOffset dayStart, DateTimeOffset dayEnd,
        string? lineTemplate = null, int? part = null)
    {
        var lines = new List<string>();
        foreach (var outage in group?.Outages ?? [])
        {
            var start = outage.Start > dayStart ? outage.Start : dayStart;
            var end = outage.End < dayEnd ? outage.End : dayEnd;
            if (end <= start)
            {
                continue;
            }
            var local = KyivTime.FromUtc(start);
            var daypart = local.Hour / 6;
            if (part is not null && daypart != part) continue;

            var endLabel = end == dayEnd ? "24:00" : Clock(end);
            if (string.IsNullOrWhiteSpace(lineTemplate))
                lines.Add($"🔴 {Clock(start)} - {endLabel} ({Approx(end - start)})");
            else
                lines.Add(System.Net.WebUtility.HtmlDecode(MessageTemplate.Render(lineTemplate,
                    new Dictionary<string, string>
                    {
                        ["start"] = Clock(start), ["end"] = endLabel, ["duration"] = Approx(end - start),
                        ["date"] = local.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                        ["weekday"] = Weekdays[(int)local.DayOfWeek],
                        ["daypart"] = new[] { "Ніч", "Ранок", "День", "Вечір" }[daypart],
                    })));
        }

        return lines;
    }

    private static TimeSpan OutageTotal(GroupSchedule? group, DateTimeOffset dayStart, DateTimeOffset dayEnd)
    {
        var total = TimeSpan.Zero;
        foreach (var outage in group?.Outages ?? [])
        {
            var start = outage.Start > dayStart ? outage.Start : dayStart;
            var end = outage.End < dayEnd ? outage.End : dayEnd;
            if (end > start)
            {
                total += end - start;
            }
        }

        return total;
    }

    // Values for the schedule post. updated picks the "Оновлений графік" heading.
    public static IReadOnlyDictionary<string, string> PostValues(
        string regionName,
        string groupId,
        ScheduleSnapshot snapshot,
        DateTimeOffset now,
        bool updated,
        string? lineTemplate = null)
    {
        var local = KyivTime.FromUtc(now);
        var dayStart = KyivDayStart(local.Date);
        var dayEnd = KyivDayStart(local.Date.AddDays(1));
        var nextEnd = KyivDayStart(local.Date.AddDays(2));
        var group = snapshot.Find(groupId);

        var today = OutageLines(group, dayStart, dayEnd, lineTemplate);
        var tomorrowLines = snapshot.CoverageEnd >= nextEnd ? OutageLines(group, dayEnd, nextEnd, lineTemplate) : null;
        var total = OutageTotal(group, dayStart, dayEnd);

        return new Dictionary<string, string>
        {
            ["header"] = updated ? "Оновлений графік відключень на сьогодні" : "Графік відключень на сьогодні",
            ["weekday"] = Weekdays[(int)local.DayOfWeek],
            ["date"] = local.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            ["outages"] = today.Count == 0 ? NoOutages : string.Join("\n", today),
            ["total"] = today.Count == 0 ? "" : Approx(total),
            ["tomorrow"] = tomorrowLines is null ? "" : tomorrowLines.Count == 0 ? NoOutages : string.Join("\n", tomorrowLines),
            ["updated"] = snapshot.SourceUpdatedText ?? "",
            ["region"] = regionName,
            ["group"] = groupId,
            ["night"] = string.Join("\n", OutageLines(group, dayStart, dayEnd, lineTemplate, 0)),
            ["morning"] = string.Join("\n", OutageLines(group, dayStart, dayEnd, lineTemplate, 1)),
            ["afternoon"] = string.Join("\n", OutageLines(group, dayStart, dayEnd, lineTemplate, 2)),
            ["evening"] = string.Join("\n", OutageLines(group, dayStart, dayEnd, lineTemplate, 3)),
            ["tomorrowDate"] = local.AddDays(1).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
        };
    }

    public static IReadOnlyDictionary<string, string> BeforeOffValues(
        string regionName,
        string groupId,
        DateTimeOffset outageStart,
        int minutesBefore) =>
        new Dictionary<string, string>
        {
            ["time"] = Clock(outageStart),
            ["minutes"] = minutesBefore.ToString(CultureInfo.InvariantCulture),
            ["date"] = KyivTime.FromUtc(outageStart).ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
            ["region"] = regionName,
            ["group"] = groupId,
        };

    // What the schedule post shows, ignoring the heading: a change here is a reason to post again.
    public static string PostFingerprint(IReadOnlyDictionary<string, string> values) =>
        values["outages"] + "|" + values["tomorrow"];

    // File names follow the repository: group "45.1" -> gpv-45-1-emergency.png (today and tomorrow grid) or
    // gpv-45-1-summary.png (short card). Null when the chat wants no picture.
    public static string? ImageUrl(ScheduleSettings settings, string dataFile, string groupId, string version, ScheduleImage kind = ScheduleImage.Grid)
    {
        if (kind == ScheduleImage.None)
        {
            return null;
        }

        var name = "gpv-" + groupId.Replace('.', '-') + (kind == ScheduleImage.Card ? "-summary.png" : "-emergency.png");
        var url = $"{settings.BaseUrl.TrimEnd('/')}/images/{dataFile}/{name}";

        // Telegram caches pictures by URL, so a changed schedule needs a new URL to be fetched again.
        return string.IsNullOrEmpty(version) ? url : $"{url}?v={Uri.EscapeDataString(version.Length > 12 ? version[..12] : version)}";
    }

    public static IReadOnlyDictionary<string, string> OffValues(string regionName, string groupId, ScheduleStatus status, DateTimeOffset now)
    {
        var onFor = status.Since is { } since && status.PreviousEnd is { } previous && since - previous >= TimeSpan.FromMinutes(1)
            ? Duration(since - previous)
            : "";
        var planned = status.Since is { } from && status.Until is { } to && to > from ? Approx(to - from) : "";

        return new Dictionary<string, string>
        {
            ["time"] = Clock(status.Since ?? now),
            ["onDuration"] = onFor,
            ["until"] = status.Until is { } until ? ClockWithDay(until, now) : "",
            ["planned"] = planned,
            ["region"] = regionName,
            ["group"] = groupId,
        };
    }

    // outageStart is set when this message continues an outage the chat was told about.
    public static IReadOnlyDictionary<string, string> OnValues(string regionName, string groupId, ScheduleStatus status, DateTimeOffset? outageStart, DateTimeOffset now)
    {
        var restored = status.Since ?? now;
        var offFor = outageStart is { } off && restored - off >= TimeSpan.FromMinutes(1) ? Duration(restored - off) : "";

        return new Dictionary<string, string>
        {
            ["time"] = Clock(restored),
            ["offDuration"] = offFor,
            ["next"] = status.NextStart is { } next ? ClockWithDay(next, now) : "",
            ["region"] = regionName,
            ["group"] = groupId,
        };
    }

    public static string OffMessage(string regionName, string groupId, ScheduleStatus status, DateTimeOffset now, string? template = null) =>
        MessageTemplate.Render(template, OffValues(regionName, groupId, status, now), MessageSlots.Find(MessageSlots.Off)!.DefaultTemplate);

    public static string OnMessage(string regionName, string groupId, ScheduleStatus status, DateTimeOffset? outageStart, DateTimeOffset now, string? template = null) =>
        MessageTemplate.Render(template, OnValues(regionName, groupId, status, outageStart, now), MessageSlots.Find(MessageSlots.On)!.DefaultTemplate);

    // What the bot says instead of querying the DTEK site when the schedule already explains the outage.
    public static string StabilizationNotice(string groupId, ScheduleStatus status, string? sourceUpdated, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            $"🗓 За графіком (черга {groupId}) зараз планове стабілізаційне відключення"
                + (status.Since is { } since ? $" з {Clock(since)}." : "."),
            status.Until is { } until
                ? $"Світло очікується о {ClockWithDay(until, now)}."
                : "Час повернення за графіком поки невідомий.",
            "Перевірку за адресою на сайті ДТЕК не запускаю.",
        };

        if (!string.IsNullOrWhiteSpace(sourceUpdated))
        {
            lines.Add("");
            lines.Add($"Графік оновлено: {sourceUpdated} (за даними ДТЕК)");
        }

        return string.Join("\n", lines);
    }
}
