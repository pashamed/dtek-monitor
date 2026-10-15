using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.MiniApp;

// What the editor's "current state" card shows: what the DTEK site says for the region and what the schedule
// says for the chat's group. Reads only data the bot already has, so it is free to call.
public sealed class ChatStatusService(
    BotDbContext db,
    IScheduleProvider schedules,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time)
{
    public async Task<IReadOnlyList<string>> BuildAsync(Channel? channel, CancellationToken ct)
    {
        var region = channel is null || string.IsNullOrEmpty(channel.RegionId)
            ? null
            : options.CurrentValue.Regions.FirstOrDefault(r => r.Id == channel.RegionId);
        if (channel is null || region is null)
        {
            return ["Оберіть регіон, щоб побачити поточний стан."];
        }

        var lines = new List<string>();

        var state = await db.RegionStates.FirstOrDefaultAsync(s => s.RegionId == region.Id, ct);
        lines.Add(state?.Kind switch
        {
            PopupKind.Emergency => "🚨 На сайті ДТЕК діють екстрені відключення.",
            PopupKind.Accident => "⚠️ На сайті ДТЕК повідомлено про аварію в енергосистемі.",
            PopupKind.Unknown => "ℹ️ На сайті ДТЕК є невизначене повідомлення.",
            PopupKind.Stabilization => "🗓 Діють стабілізаційні відключення (за графіками).",
            null => "Даних із сайту ДТЕК ще немає.",
            _ => "✅ Екстрених відключень на сайті ДТЕК немає.",
        });
        if (state is not null)
        {
            lines.Add($"Перевірено: {KyivTime.Format(state.ObservedAt)} (Київ)");
        }

        if (channel.GroupId is { } groupId)
        {
            var group = await db.GroupStates.FirstOrDefaultAsync(g => g.RegionId == region.Id && g.GroupId == groupId, ct);
            if (group?.IsEmergency == true)
            {
                lines.Add($"🚨 Для черги {groupId} діють екстрені відключення (за даними Yasno).");
            }

            if (region.HasSchedules)
            {
                var snapshot = await schedules.GetAsync(region.Id, ct);
                var now = time.GetUtcNow();
                var status = snapshot?.StatusAt(groupId, now);
                if (status is { IsOff: true })
                {
                    var since = status.Since is { } s ? $" з {ScheduleText.Clock(s)}" : "";
                    var until = status.Until is { } u ? $" Світло очікується о {ScheduleText.ClockWithDay(u, now)}." : "";
                    lines.Add($"🔴 За графіком (черга {groupId}) світла немає{since}.{until}");
                }
                else if (status is not null)
                {
                    var next = status.NextStart is { } n
                        ? $" Наступне відключення о {ScheduleText.ClockWithDay(n, now)}."
                        : " Наступних відключень за графіком поки немає.";
                    lines.Add($"🟢 За графіком (черга {groupId}) світло є.{next}");
                }
            }
        }

        return lines;
    }
}
