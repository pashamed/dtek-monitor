using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Checks;

// Answers "the power is off, why?" with as little load on the DTEK site as possible. Cheap, shared sources go first;
// the address lookup on the DTEK site is the last resort and only runs when nothing else explains the outage.
public sealed class OutageAssessmentService(
    BotDbContext db,
    AddressCheckService checks,
    IScheduleProvider schedules,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time)
{
    public async Task<string> AssessAsync(Channel channel, CancellationToken ct)
    {
        var state = await db.RegionStates.FirstOrDefaultAsync(s => s.RegionId == channel.RegionId, ct);
        var regionalEmergency = state?.Kind is PopupKind.Emergency or PopupKind.Accident or PopupKind.Unknown;

        // Ukrenergo reports emergencies earlier than the DTEK site, and explains an outage just as well.
        var grid = await db.GridStates.AsNoTracking().FirstOrDefaultAsync(g => g.RegionId == channel.RegionId, ct);
        var gridEmergency = grid is { IsAlert: true };

        // 1. A planned outage in the group's schedule explains it: stop here, the DTEK site is not asked.
        var notice = await ScheduledOutageNoticeAsync(channel, ct);
        if (notice is not null)
        {
            var lines = new List<string>();
            AddGroupEmergencyLine(lines, channel, await GroupInEmergencyAsync(channel, ct));
            lines.Add(notice);
            if (regionalEmergency)
            {
                // A regional emergency overrides the schedule, so the notice alone could mislead.
                lines.Add("⚠️ Увага: на сайті ДТЕК зараз діє загальне повідомлення про екстрені відключення чи аварію по регіону, тому графік може не діяти.");
            }
            else if (gridEmergency)
            {
                lines.Add("⚠️ Увага: Укренерго повідомляє про екстрені відключення чи аварію по регіону, тому графік може не діяти.");
            }

            return string.Join("\n\n", lines);
        }

        // 2. A region-wide emergency or accident popup on the DTEK site is the likely cause for everyone.
        if (regionalEmergency)
        {
            return "ℹ️ На сайті ДТЕК зараз діє загальне повідомлення про екстрені відключення чи аварію по регіону. Це ймовірна причина, тому перевірку за адресою не запускаю.";
        }

        if (gridEmergency)
        {
            return "ℹ️ Укренерго повідомляє про екстрені відключення чи аварію по регіону. Це ймовірна причина, тому перевірку за адресою не запускаю.";
        }

        // 3. Nothing explains it: look at the address on the DTEK site.
        var result = new List<string>();
        AddGroupEmergencyLine(result, channel, await GroupInEmergencyAsync(channel, ct));
        result.Add((await checks.RequestAsync(channel, ct)).Message);
        return string.Join("\n\n", result);
    }

    // The group's emergency status from Yasno (already collected for everyone, free to read).
    private async Task<bool> GroupInEmergencyAsync(Channel channel, CancellationToken ct)
    {
        if (channel.GroupId is null)
        {
            return false;
        }

        var group = await db.GroupStates.FirstOrDefaultAsync(
            g => g.RegionId == channel.RegionId && g.GroupId == channel.GroupId, ct);
        return group?.IsEmergency == true;
    }

    private static void AddGroupEmergencyLine(List<string> lines, Channel channel, bool inEmergency)
    {
        if (inEmergency)
        {
            lines.Add($"🚨 Для вашої черги {channel.GroupId} зараз діють екстрені відключення (за даними Yasno).");
        }
    }

    private async Task<string?> ScheduledOutageNoticeAsync(Channel channel, CancellationToken ct)
    {
        var region = options.CurrentValue.Regions.FirstOrDefault(r => r.Id == channel.RegionId);
        if (channel.GroupId is null || region is not { HasSchedules: true })
        {
            return null;
        }

        var snapshot = await schedules.GetAsync(region.Id, ct);
        var now = time.GetUtcNow();
        var status = snapshot?.StatusAt(channel.GroupId, now);
        return status is { IsOff: true }
            ? ScheduleText.StabilizationNotice(channel.GroupId, status, snapshot!.SourceUpdatedText, now)
            : null;
    }
}
