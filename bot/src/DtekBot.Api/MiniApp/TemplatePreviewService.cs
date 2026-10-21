using DtekBot.Api.Schedules;
using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.MiniApp;

public sealed record RenderedTemplate(string Html, string? ImageUrl);

// Builds the text (and picture) a template slot would produce, for the preview and for the test post.
// Real schedule data is used when the chat has a group; everything else comes from the placeholder examples.
public sealed class TemplatePreviewService(
    IOptionsMonitor<DtekSettings> options,
    IScheduleProvider schedules,
    TimeProvider time)
{
    public static AlertKind KindFor(string slot) => slot switch
    {
        MessageSlots.Regional => AlertKind.Accident,
        MessageSlots.Ended => AlertKind.Ended,
        _ => AlertKind.Emergency,
    };

    public async Task<RenderedTemplate> RenderAsync(
        string slot,
        string? template,
        RegionSettings region,
        IReadOnlyList<string> partIds,
        string? groupId,
        string? address,
        ScheduleImage imageKind,
        AlertKind? kindOverride,
        CancellationToken ct,
        string? scheduleLineTemplate = null,
        string? scheduleHeadingTemplate = null)
    {
        var definition = MessageSlots.Find(slot)!;
        var now = time.GetUtcNow();
        var values = MessageSlots.Samples(slot).ToDictionary(p => p.Key, p => p.Value);
        string? imageUrl = null;

        if (MessageSlots.IsAlert(slot))
        {
            var partNames = slot == MessageSlots.Local
                ? [$"черга {(string.IsNullOrEmpty(groupId) ? values["group"] : groupId)}"]
                : partIds
                    .Select(id => region.Parts.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Name)
                    .OfType<string>()
                    .ToList();
            foreach (var pair in AlertText.Values(kindOverride ?? KindFor(slot), region.Name, partNames, region.SiteUrl, now, null))
            {
                values[pair.Key] = pair.Value;
            }
            values["address"] = address ?? "";

            if (slot == MessageSlots.Local)
            {
                values["group"] = string.IsNullOrEmpty(groupId) ? values["group"] : groupId;
            }

            if (values["popup"].Length == 0)
            {
                values["popup"] = "Шановні клієнти! Через аварію в мережі знеструмлено частину правого берега столиці.";
            }
        }
        else if (slot == MessageSlots.Schedule)
        {
            var snapshot = region.HasSchedules ? await schedules.GetAsync(region.Id, ct) : null;
            var sample = string.IsNullOrEmpty(groupId) || snapshot?.Find(groupId) is null;
            var imageGroup = sample ? snapshot?.GroupIds.FirstOrDefault() : groupId;
            if (snapshot is not null && imageGroup is not null)
            {
                imageUrl = ScheduleText.ImageUrl(options.CurrentValue.Schedules, snapshot.DataFile, imageGroup, snapshot.Version, imageKind);
            }
            if (snapshot is null || sample)
            {
                var date = KyivTime.FromUtc(now).Date;
                var today = ScheduleText.KyivDayStart(date);
                var tomorrow = ScheduleText.KyivDayStart(date.AddDays(1));
                groupId = "sample";
                snapshot = new ScheduleSnapshot("", "", now, null, today, ScheduleText.KyivDayStart(date.AddDays(2)),
                    new Dictionary<string, GroupSchedule>
                    {
                        [groupId] = new(groupId, [
                            new OutageInterval(today.AddHours(14), today.AddHours(18)),
                            new OutageInterval(tomorrow.AddHours(14), tomorrow.AddHours(18)),
                        ]),
                    });
            }
            var target = new Channel { RegionId = region.Id, GroupId = groupId, AddressLabel = address, PartIdList = partIds.ToList() };
            var templates = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(scheduleLineTemplate)) templates[MessageSlots.ScheduleLine] = scheduleLineTemplate;
            if (!string.IsNullOrWhiteSpace(scheduleHeadingTemplate)) templates[MessageSlots.ScheduleHeading] = scheduleHeadingTemplate;
            target.TemplateMap = templates;
            var scheduleHtml = CombinedSchedule.Render(target, snapshot, region.Name, now, false, region.DisplayNames(partIds));
            return new RenderedTemplate((sample ? "<b>Зразок</b>\n\n" : "") + scheduleHtml, imageUrl);
        }
        else if (region.HasSchedules && !string.IsNullOrEmpty(groupId) && await schedules.GetAsync(region.Id, ct) is { } snapshot
            && snapshot.Find(groupId) is not null)
        {
            values["region"] = region.Name;
            values["group"] = groupId;
        }

        var html = MessageTemplate.Render(template, values, definition.DefaultTemplate);
        return new RenderedTemplate(html, imageUrl);
    }
}
