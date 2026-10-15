using DtekBot.Api.Settings;
using DtekBot.Shared;

namespace DtekBot.Api.Schedules;

// Wording and picture links for schedule messages. Kept free of I/O so it is easy to test.
public static class ScheduleText
{
    public static string Duration(TimeSpan span)
    {
        var minutes = (int)Math.Round(span.TotalMinutes);
        if (minutes < 1)
        {
            return "менше хвилини";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        return (hours, rest) switch
        {
            (0, _) => $"{rest} хв",
            (_, 0) => $"{hours} год",
            _ => $"{hours} год {rest} хв",
        };
    }

    public static string Clock(DateTimeOffset moment) => KyivTime.FromUtc(moment).ToString("HH:mm");

    // "00:00 (16.10)" when the moment is not today (Kyiv time), "14:00" otherwise, so it reads well after "о".
    public static string ClockWithDay(DateTimeOffset moment, DateTimeOffset now)
    {
        var local = KyivTime.FromUtc(moment);
        return local.Date == KyivTime.FromUtc(now).Date ? local.ToString("HH:mm") : $"{local:HH:mm} ({local:dd.MM})";
    }

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

    public static string OffMessage(string regionName, string groupId, ScheduleStatus status, DateTimeOffset now)
    {
        var lines = new List<string>
        {
            $"🔴 {(status.Since is { } since ? Clock(since) : "")} Світло вимкнено за графіком".Replace("  ", " ").Trim(),
            $"{regionName} · черга {groupId}",
        };

        if (status.Since is { } start && status.PreviousEnd is { } previous && previous < start)
        {
            lines.Add($"🕓 Світло було {Duration(start - previous)}");
        }

        lines.Add(status.Until is { } until
            ? $"🗓 Очікуємо повернення о {ClockWithDay(until, now)}"
            : "🗓 Час повернення поки невідомий (чекаємо оновлення графіка)");
        return string.Join("\n", lines);
    }

    // Power is on. outageStart is set when this message continues an outage it announced before.
    public static string OnMessage(string regionName, string groupId, ScheduleStatus status, DateTimeOffset? outageStart, DateTimeOffset now)
    {
        var header = outageStart is null ? "Світло є за графіком" : "Світло повернулось за графіком";
        var lines = new List<string>
        {
            $"🟢 {(status.Since is { } since && outageStart is not null ? Clock(since) : "")} {header}".Replace("  ", " ").Trim(),
            $"{regionName} · черга {groupId}",
        };

        if (outageStart is { } off && status.Since is { } on && on > off)
        {
            lines.Add($"🕓 Світла не було {Duration(on - off)}");
        }

        if (status.Since is { } restored && now > restored)
        {
            lines.Add($"🕓 Світло є вже {Duration(now - restored)}");
        }

        lines.Add(status.NextStart is { } next
            ? $"🗓 Наступне відключення за графіком о {ClockWithDay(next, now)}"
            : "🗓 Наступних відключень за графіком поки немає");
        return string.Join("\n", lines);
    }

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
