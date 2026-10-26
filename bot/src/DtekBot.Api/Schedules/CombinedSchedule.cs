using System.Net;
using System.Text.RegularExpressions;
using DtekBot.Api.Data;
using DtekBot.Shared;

namespace DtekBot.Api.Schedules;

public static class CombinedSchedule
{
    public static string Render(Channel channel, ScheduleSnapshot snapshot, string regionName, DateTimeOffset now, bool updated,
        IReadOnlyList<string>? partNames = null, IReadOnlyDictionary<string, string>? changes = null)
    {
        var date = KyivTime.FromUtc(now).Date;
        var sections = new List<string>();
        for (var offset = 0; offset <= 1; offset++)
        {
            var start = ScheduleText.KyivDayStart(date.AddDays(offset));
            var end = ScheduleText.KyivDayStart(date.AddDays(offset + 1));
            var values = new Dictionary<string, string>
            {
                ["header"] = (updated ? "Оновлений графік" : "Графік") + " відключень на " + (offset == 0 ? "сьогодні" : "завтра"),
                ["weekday"] = ScheduleText.Weekdays[(int)date.AddDays(offset).DayOfWeek],
                ["date"] = date.AddDays(offset).ToString("dd.MM.yyyy", System.Globalization.CultureInfo.InvariantCulture),
                ["region"] = regionName,
                ["part"] = string.Join(", ", partNames ?? []),
                ["address"] = channel.AddressText(),
                ["changes"] = changes?.GetValueOrDefault(ScheduleText.DateKey(start)) ?? "",
            };
            var template = channel.TemplateFor(MessageSlots.ScheduleHeading);
            var fallback = MessageSlots.Find(MessageSlots.ScheduleHeading)!.DefaultTemplate;
            var heading = MessageTemplate.Render(template, values, fallback);
            if (snapshot.CoverageStart > start || snapshot.CoverageEnd < end)
            {
                sections.Add(heading + "\n\nГрафік ще не оприлюднено.");
                continue;
            }
            var groups = channel.GroupIds().Select(id =>
            {
                var group = snapshot.CoversDay(id, start, end) ? snapshot.Find(id) : null;
                var lines = group is null ? "Графік тимчасово недоступний."
                    : string.Join("\n", ScheduleText.OutageLines(group, start, end, channel.TemplateFor(MessageSlots.ScheduleLine)));
                if (group is not null && lines.Length == 0) lines = ScheduleText.NoOutages;
                return (channel.IsCombined() ? channel.Heading(id) + "\n" : "")
                    + MessageTemplate.Render("{outages}", new Dictionary<string, string> { ["outages"] = lines });
            });
            sections.Add(heading + "\n\n" + string.Join("\n\n", groups));
        }
        return string.Join("\n\n", sections);
    }

    // Long digests use plain text so splitting cannot break HTML tags/entities.
    public static IReadOnlyList<string> Parts(string html)
    {
        if (html.Length <= 4096) return [html];
        var text = WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", ""));
        var parts = new List<string>();
        while (text.Length > 3900)
        {
            var cut = text.LastIndexOf('\n', 3900);
            if (cut < 100) cut = 3900;
            if (char.IsHighSurrogate(text[cut - 1])) cut--;
            parts.Add(WebUtility.HtmlEncode(text[..cut]));
            text = text[cut..].TrimStart('\n');
        }
        if (text.Length > 0) parts.Add(WebUtility.HtmlEncode(text));
        return parts;
    }
}
