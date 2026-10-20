using System.Net;
using System.Text.RegularExpressions;
using DtekBot.Api.Data;
using DtekBot.Shared;

namespace DtekBot.Api.Schedules;

public static class CombinedSchedule
{
    public static string Render(Channel channel, ScheduleSnapshot snapshot, string regionName, DateTimeOffset now, bool updated)
    {
        var template = channel.TemplateFor(MessageSlots.Schedule);
        var sections = new List<string>();
        foreach (var groupId in channel.GroupIds())
        {
            if (snapshot.Find(groupId) is null)
            {
                sections.Add(channel.Heading(groupId) + "\nГрафік тимчасово недоступний.");
                continue;
            }
            var values = ScheduleText.PostValues(regionName, groupId, snapshot, now, updated, channel.TemplateFor(MessageSlots.ScheduleLine));
            var text = MessageTemplate.Render(template, values, MessageSlots.Find(MessageSlots.Schedule)!.DefaultTemplate);
            if (channel.IsCombined())
            {
                if (string.IsNullOrWhiteSpace(template))
                    text = values["outages"] + "\nЗавтра: " + (values["tomorrow"] == "" ? "ще не оприлюднено" : values["tomorrow"]);
                text = channel.Heading(groupId) + "\n" + text;
            }
            sections.Add(text);
        }
        var body = string.Join("\n\n", sections);
        if (channel.IsCombined() && string.IsNullOrWhiteSpace(template) && channel.GroupIds().Count > 0)
        {
            var values = ScheduleText.PostValues(regionName, channel.GroupIds()[0], snapshot, now, updated, channel.TemplateFor(MessageSlots.ScheduleLine)).ToDictionary(p => p.Key, p => p.Value);
            values["outages"] = body;
            // Sections already contain escaped heading labels; rendering {outages} would escape them a second time.
            var marker = "COMBINED_SECTIONS";
            values["outages"] = marker;
            return MessageTemplate.Render(null, values, MessageSlots.Find(MessageSlots.Schedule)!.DefaultTemplate).Replace(marker, body);
        }
        return body;
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
