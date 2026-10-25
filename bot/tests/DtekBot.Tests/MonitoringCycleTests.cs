using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Grid;
using DtekBot.Api.Parsing;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using DtekBot.Api.Svitlobot;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DtekBot.Tests;

public sealed class MonitoringCycleTests
{
    private sealed class Popups : IDtekPopupCollector
    {
        public bool Started { get; private set; }
        public TaskCompletionSource<IReadOnlyList<DtekPopupObservation>> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<DtekPopupObservation>> CollectAsync(CancellationToken ct)
        {
            Started = true;
            return Result.Task;
        }
    }

    private sealed class Grid : IGridStatusProvider
    {
        public bool Started { get; private set; }
        public TaskCompletionSource<GridFeed?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<GridFeed?> GetAsync(CancellationToken ct)
        {
            Started = true;
            return Result.Task;
        }
    }

    private sealed class Schedules : IScheduleProvider
    {
        public List<string> Started { get; } = [];
        public TaskCompletionSource<ScheduleSnapshot?> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ScheduleSnapshot?> GetAsync(string regionId, CancellationToken ct)
        {
            Started.Add(regionId);
            return Result.Task;
        }

        public Task<ScheduleSnapshot?> RefreshAsync(string regionId, CancellationToken ct) =>
            throw new InvalidOperationException("Monitoring ticks must respect the shared schedule refresh window.");
    }

    [Fact]
    public async Task SourcesStartInParallel_AndNothingIsAppliedOrSentBeforeAllComplete()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        factory.WithDb(db => db.Channels.Add(new Channel { ChatId = 123, RegionId = "kyiv", IsActive = true }));
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var popups = new Popups();
        var grid = new Grid();
        var schedules = new Schedules();
        var options = sp.GetRequiredService<IOptionsMonitor<DtekSettings>>();
        var cycle = new MonitoringCycle(popups, grid, schedules, sp.GetRequiredService<GridTickService>(),
            sp.GetRequiredService<RegionObservationStore>(), sp.GetRequiredService<RegionAlertCoordinator>(),
            sp.GetRequiredService<ScheduleNotifier>(), sp.GetRequiredService<SvitlobotScheduleSyncService>(),
            options, NullLogger<MonitoringCycle>.Instance);

        var run = cycle.RunAsync(CancellationToken.None);
        Assert.True(popups.Started);
        Assert.True(grid.Started);
        Assert.Equal(options.CurrentValue.Regions.Count(r => r.HasSchedules), schedules.Started.Count);
        Assert.False(run.IsCompleted);

        popups.Result.SetResult([new("kyiv", "За наказом Укренерго введені екстрені відключення.", factory.Clock.Now)]);
        grid.Result.SetResult(new GridFeed(factory.Clock.Now, new Dictionary<string, GridRegionStatus>
        {
            ["kyiv"] = new("kyiv", GridStatuses.EmergencyOutages, "Екстрені відключення", factory.Clock.Now),
        }));
        Assert.Empty(factory.WithDb(db => db.RegionStates.ToArray()));
        Assert.Empty(factory.WithDb(db => db.GridStates.ToArray()));
        Assert.Empty(factory.Bot.SentTo(123));

        schedules.Result.SetResult(null);
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(factory.WithDb(db => db.RegionStates.ToArray()));
        Assert.Single(factory.WithDb(db => db.GridStates.ToArray()));
        var message = Assert.Single(factory.Bot.SentTo(123));
        Assert.Contains("ДТЕК", message.Text); // No intermediate Ukrenergo-only post.
    }

    [Fact]
    public async Task UnavailableSources_PreservePreviousEmergencyState()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        factory.WithDb(db => db.RegionStates.Add(new RegionState
        {
            RegionId = "kyiv", Kind = PopupKind.Emergency, Hash = "old",
            PopupText = "Екстрені відключення", NormalizedText = "екстрені відключення",
            ObservedAt = factory.Clock.Now, UpdatedAt = factory.Clock.Now,
        }));
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var popups = new Popups();
        var grid = new Grid();
        var schedules = new Schedules();
        popups.Result.SetResult([]);
        grid.Result.SetResult(null);
        schedules.Result.SetResult(null);
        var cycle = new MonitoringCycle(popups, grid, schedules, sp.GetRequiredService<GridTickService>(),
            sp.GetRequiredService<RegionObservationStore>(), sp.GetRequiredService<RegionAlertCoordinator>(),
            sp.GetRequiredService<ScheduleNotifier>(), sp.GetRequiredService<SvitlobotScheduleSyncService>(),
            sp.GetRequiredService<IOptionsMonitor<DtekSettings>>(), NullLogger<MonitoringCycle>.Instance);
        await cycle.RunAsync(CancellationToken.None);
        Assert.Equal(PopupKind.Emergency, factory.WithDb(db => db.RegionStates.Single().Kind));
    }

    [Fact]
    public async Task SameText_StoredWithAnOlderClassification_IsReclassifiedAndAlertsTheNamedDistrict()
    {
        const string popup = "<h6>Шановні клієнти!</h6><p>За наказом НЕК Укренерго <strong>25.10</strong> діють<strong> стабілізаційні </strong>відключення електроенергії.</p>"
            + "<p>У <strong>Бучанському, Обухівському та Фастівському</strong> районів зникло світло через аварію в мережі.</p>";
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var current = sp.GetRequiredService<PopupClassifier>().Classify("kyiv-oblast", popup);
        // What a build that ranked stabilization above accidents stored for the very same text.
        factory.WithDb(db =>
        {
            db.RegionStates.Add(new RegionState
            {
                RegionId = "kyiv-oblast", Kind = PopupKind.Stabilization, Hash = current.Hash, PartIdList = current.PartIds,
                PopupText = popup, NormalizedText = current.NormalizedText,
                ObservedAt = factory.Clock.Now.AddMinutes(-5), UpdatedAt = factory.Clock.Now.AddMinutes(-5),
            });
            db.Channels.Add(new Channel { ChatId = 321, RegionId = "kyiv-oblast", PartIdList = ["bucha"], IsActive = true });
            db.Channels.Add(new Channel { ChatId = 322, RegionId = "kyiv-oblast", PartIdList = ["brovary"], IsActive = true });
        });
        var popups = new Popups();
        var grid = new Grid();
        var schedules = new Schedules();
        popups.Result.SetResult([new("kyiv-oblast", popup, factory.Clock.Now)]);
        grid.Result.SetResult(null);
        schedules.Result.SetResult(null);
        var cycle = new MonitoringCycle(popups, grid, schedules, sp.GetRequiredService<GridTickService>(),
            sp.GetRequiredService<RegionObservationStore>(), sp.GetRequiredService<RegionAlertCoordinator>(),
            sp.GetRequiredService<ScheduleNotifier>(), sp.GetRequiredService<SvitlobotScheduleSyncService>(),
            sp.GetRequiredService<IOptionsMonitor<DtekSettings>>(), NullLogger<MonitoringCycle>.Instance);

        await cycle.RunAsync(CancellationToken.None);

        Assert.Equal(PopupKind.Accident, factory.WithDb(db => db.RegionStates.Single(s => s.RegionId == "kyiv-oblast").Kind));
        Assert.Single(factory.Bot.SentTo(321));
        Assert.Empty(factory.Bot.SentTo(322));
    }
}
