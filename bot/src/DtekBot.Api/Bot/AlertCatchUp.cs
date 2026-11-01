using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Bot;

// Alerts are posted when the source changes, so a chat that is switched on (or changes its region, districts, group or
// alert switches) in the middle of an ongoing emergency would otherwise hear nothing until the next change. This runs
// the same dispatch for each address region and group: chats that already have the alert are left alone, the new one
// gets it. A failure here never fails the save that triggered it.
public sealed class AlertCatchUp(
    BotDbContext db,
    RegionAlertCoordinator coordinator,
    AlertDispatcher dispatcher,
    IOptionsMonitor<DtekSettings> options,
    ILogger<AlertCatchUp> logger)
{
    public async Task RunAsync(Channel channel, CancellationToken ct)
    {
        if (!channel.IsActive || channel.IsPaused || channel.RegionIds().Count == 0)
        {
            return;
        }

        foreach (var regionId in channel.RegionIds())
        {
            try
            {
                await coordinator.RecomputeAsync(regionId, ct);
                var region = options.CurrentValue.Regions.FirstOrDefault(r => r.Id == regionId);
                if (region is null) continue;
                foreach (var groupId in channel.ForRegion(regionId).GroupIds())
                {
                    var group = await db.GroupStates.AsNoTracking()
                        .FirstOrDefaultAsync(g => g.RegionId == region.Id && g.GroupId == groupId, ct);
                    if (group?.IsEmergency == true)
                    {
                        await dispatcher.DispatchGroupAsync(region, groupId, true, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Catching up the alerts of chat {ChatId} in {RegionId} failed", channel.ChatId, regionId);
            }
        }
    }
}
