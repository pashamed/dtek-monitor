using DtekBot.Api.Schedules;
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
        string? scheduleLineTemplate = null)
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

            if (slot == MessageSlots.Local)
            {
                values["group"] = string.IsNullOrEmpty(groupId) ? values["group"] : groupId;
                values["address"] = string.IsNullOrEmpty(address) ? values["address"] : address;
            }

            if (values["popup"].Length == 0)
            {
                values["popup"] = "Шановні клієнти! Через аварію в мережі знеструмлено частину правого берега столиці.";
            }
        }
        else if (region.HasSchedules && !string.IsNullOrEmpty(groupId) && await schedules.GetAsync(region.Id, ct) is { } snapshot
            && snapshot.Find(groupId) is not null)
        {
            if (slot == MessageSlots.Schedule)
            {
                foreach (var pair in ScheduleText.PostValues(region.Name, groupId, snapshot, now, updated: true, lineTemplate: scheduleLineTemplate))
                {
                    values[pair.Key] = pair.Value;
                }

                imageUrl = ScheduleText.ImageUrl(options.CurrentValue.Schedules, snapshot.DataFile, groupId, snapshot.Version, imageKind);
            }
            else
            {
                values["region"] = region.Name;
                values["group"] = groupId;
            }
        }
        else if (slot == MessageSlots.Schedule)
        {
            // No group yet: the picture of a sample group still shows what the post looks like.
            var sample = region.HasSchedules && await schedules.GetAsync(region.Id, ct) is { } any
                ? (any.GroupIds.FirstOrDefault() is { } first ? (any, first) : default)
                : default;
            if (sample.any is not null)
            {
                imageUrl = ScheduleText.ImageUrl(options.CurrentValue.Schedules, sample.any.DataFile, sample.first, sample.any.Version, imageKind);
            }
        }

        var html = MessageTemplate.Render(template, values, definition.DefaultTemplate);
        return new RenderedTemplate(html, imageUrl);
    }
}
