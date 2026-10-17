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
        var region = options.CurrentValue.Regions.FirstOrDefault(r => r.Id == channel.RegionId);
        region?.UpgradeRetiredParts(channel);

        var state = await db.RegionStates.FirstOrDefaultAsync(s => s.RegionId == channel.RegionId, ct);
        var dtekAlert = state?.Kind is PopupKind.Emergency or PopupKind.Accident or PopupKind.Unknown;

        // A popup that names districts only concerns them; "in other districts the schedules apply".
        IReadOnlyList<string> named = dtekAlert ? state!.PartIdList ?? [] : [];
        var districtScoped = named.Count > 0;
        var mine = channel.PartIdList.Where(p => named.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
        var regionalEmergency = dtekAlert && !districtScoped;

        // Ukrenergo reports emergencies earlier than the DTEK site, and explains an outage just as well.
        var grid = await db.GridStates.AsNoTracking().FirstOrDefaultAsync(g => g.RegionId == channel.RegionId, ct);
        var gridEmergency = grid is { IsAlert: true };

        // 0. The chat's own district is under emergency outages: the schedule does not apply there, the DTEK site
        // is not asked, and the user is told so.
        if (districtScoped && mine.Count > 0)
        {
            var lines = new List<string>();
            AddGroupEmergencyLine(lines, channel, await GroupInEmergencyAsync(channel, ct));
            var places = region is null ? string.Join(", ", mine) : string.Join(", ", region.DisplayNames(mine));
            lines.Add($"🚨 У вашому районі ({places}) зараз діють екстрені відключення за даними ДТЕК. Графіки там не діють, тому причина, найімовірніше, в цьому. Перевірку за адресою не запускаю.");
            return string.Join("\n\n", lines);
        }

        var answer = await AssessOutsideEmergencyDistrictsAsync(channel, regionalEmergency, gridEmergency, ct);

        // Without chosen districts there is no telling whether the address is one of the named ones.
        if (districtScoped && channel.PartIdList.Count == 0)
        {
            var places = region is null ? string.Join(", ", named) : string.Join(", ", region.DisplayNames(named));
            return $"ℹ️ На сайті ДТЕК діють екстрені відключення в районах: {places}. Якщо ваша адреса в одному з них, графік там не діє.\n\n{answer}";
        }

        return answer;
    }

    private async Task<string> AssessOutsideEmergencyDistrictsAsync(Channel channel, bool regionalEmergency, bool gridEmergency, CancellationToken ct)
    {
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
