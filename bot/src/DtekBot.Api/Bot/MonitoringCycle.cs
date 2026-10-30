using DtekBot.Api.Grid;
using DtekBot.Api.Groups;
using DtekBot.Api.Parsing;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using DtekBot.Api.Svitlobot;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Bot;

public sealed class MonitoringCycle(
    IDtekPopupCollector popups,
    IGridStatusProvider grid,
    IScheduleProvider schedules,
    GridTickService gridTick,
    RegionObservationStore observations,
    RegionAlertCoordinator coordinator,
    ScheduleNotifier notifier,
    SvitlobotScheduleSyncService svitlobot,
    IOptionsMonitor<DtekSettings> options,
    ILogger<MonitoringCycle> logger,
    IGroupStatusCollector groups,
    GroupObservationStore groupObservations)
{
    public async Task RunAsync(CancellationToken ct)
    {
        var regions = options.CurrentValue.Regions.ToArray();
        var popupTask = popups.CollectAsync(ct);
        var gridTask = grid.RefreshAsync(ct);
        var groupsTask = groups.CollectAsync(ct);
        var scheduleTasks = regions.Where(r => r.HasSchedules)
            .Select(r => schedules.GetAsync(r.Id, ct)).ToArray();

        // No database writes or notifications until every source has completed (or reported unavailable).
        await Task.WhenAll(scheduleTasks.Cast<Task>().Append(popupTask).Append(gridTask).Append(groupsTask));

        var prepared = new List<(DtekPopupObservation Observation, PopupClassification Classification)>();
        foreach (var observation in await popupTask)
            prepared.Add((observation, await observations.PrepareAsync(observation, ct)));
        foreach (var item in prepared) await observations.SaveAsync(item.Observation, ct, item.Classification);
        var summary = await gridTick.ApplyAsync(await gridTask, false, ct);
        var acceptedGroups = new List<GroupObservation>();
        foreach (var observation in await groupsTask)
            acceptedGroups.Add(await groupObservations.SaveAsync(observation, ct));
        logger.LogInformation("Monitoring collection finished: DTEK={DtekCount}/{Regions}, Ukrenergo={Feed}, schedules={Schedules}",
            popupTask.Result.Count, regions.Count(r => !string.IsNullOrWhiteSpace(r.SiteUrl)),
            summary.Feed, scheduleTasks.Count(t => t.Result is not null));

        foreach (var region in regions) await coordinator.RecomputeAsync(region.Id, ct);
        foreach (var observation in acceptedGroups) await groupObservations.DispatchAsync(observation, ct);
        await notifier.TickAsync(ct);
        await svitlobot.TickAsync(ct);
    }
}
