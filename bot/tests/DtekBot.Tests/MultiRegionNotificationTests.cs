using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Groups;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Requests;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class MultiRegionNotificationTests : IDisposable
{
    private const long ChatId = 912;
    private const string Group = "45.1";
    private const string RegionB = "kyiv-oblast";
    private readonly DispatcherFixture f = new();
    private readonly FakeScheduleProvider provider = new();

    public void Dispose() => f.Dispose();

    private Channel AddChannel(Action<Channel>? configure = null) => f.AddChannel(ChatId, c =>
    {
        c.Addresses =
        [
            new ChatAddress { RegionId = "kyiv", Name = "Дім", Groups = [new ChatAddressGroup { GroupId = Group }] },
            new ChatAddress { RegionId = RegionB, Name = "Офіс", Groups = [new ChatAddressGroup { GroupId = Group }] },
        ];
        c.MirrorPrimary();
        configure?.Invoke(c);
    });

    private ScheduleNotifier Notifier() => new(f.Db, f.Bot, provider, f.Options, f.Clock,
        NullLogger<ScheduleNotifier>.Instance, f.CreateMirror());

    private void SetSchedules()
    {
        provider.Snapshots["kyiv"] = RealSnapshot();
        provider.Snapshots[RegionB] = RealSnapshot() with { DataFile = "kyiv-region" };
    }

    [Fact]
    public async Task RegionalAlerts_RouteBothRegions_LabelCustomText_AndClearIndependently()
    {
        AddChannel(c =>
        {
            c.TemplateMap = new Dictionary<string, string> { [MessageSlots.Emergency] = "🚨 {message}" };
            c.NotifyOnEnd = true;
        });
        await f.DispatchAsync(DispatcherFixture.EmergencyText);
        await f.DispatchAsync(DispatcherFixture.EmergencyText, f.Settings.Regions.Single(r => r.Id == RegionB));
        var deliveries = f.Deliveries(ChatId);
        Assert.Equal(2, deliveries.Count);
        Assert.Equal(2, f.Bot.Sent.Count());
        Assert.StartsWith("📍 м. Київ\n", f.Bot.Sent.First().Text);
        Assert.StartsWith("📍 Київська область\n", f.Bot.Sent.Last().Text);
        await f.DispatchAsync(DispatcherFixture.StabilizationText);
        deliveries = f.Deliveries(ChatId);
        Assert.True(deliveries.Single(d => d.RegionId == "kyiv").IsClosed);
        Assert.False(deliveries.Single(d => d.RegionId == RegionB).IsClosed);
        Assert.Equal(deliveries.Single(d => d.RegionId == "kyiv").TelegramMessageId,
            f.Bot.Sent.Last().ReplyParameters?.MessageId);
        Assert.Equal("kyiv", f.LoadChannel(ChatId).RegionId);
    }

    [Fact]
    public async Task GroupAlerts_SameGroupAcrossRegions_KeepOwnThreads_AndRemovedRegionClosesSilently()
    {
        var channel = AddChannel(c => c.NotifyOnEnd = true);
        var store = new GroupObservationStore(f.Db, f.Dispatcher, f.Options, NullLogger<GroupObservationStore>.Instance);
        foreach (var regionId in channel.RegionIds())
        {
            var observation = new GroupObservation(regionId,
                new Dictionary<string, GroupStatusEntry> { [Group] = new(GroupStatuses.Emergency, "") }, f.Clock.Now);
            var result = await store.DispatchAsync(observation, CancellationToken.None);
            Assert.Equal(1, result.Dispatch.Sent);
        }
        var keys = f.Deliveries(ChatId).Select(d => d.RegionId).ToArray();
        Assert.Contains(AlertDispatcher.GroupDeliveryKey("kyiv", Group), keys);
        Assert.Contains(AlertDispatcher.GroupDeliveryKey(RegionB, Group), keys);
        await f.Dispatcher.DispatchGroupAsync(f.Kyiv, Group, false, CancellationToken.None);
        Assert.False(f.Deliveries(ChatId).Single(d => d.RegionId == keys[1]).IsClosed);
        var sends = f.Bot.Sent.Count();
        channel.Addresses.RemoveAll(a => a.RegionId == RegionB);
        f.Db.SaveChanges();
        await store.DispatchAsync(new GroupObservation(RegionB,
            new Dictionary<string, GroupStatusEntry>(), f.Clock.Now), CancellationToken.None);
        Assert.Equal(sends, f.Bot.Sent.Count());
        Assert.All(f.Deliveries(ChatId), d => Assert.True(d.IsClosed));
    }

    [Fact]
    public async Task SchedulePosts_SameGroupAcrossRegions_KeepIndependentMessagesBaselinesAndEdits()
    {
        var channel = AddChannel(c =>
        {
            c.SchedulePost = true;
            c.SchedulePostImage = ScheduleImage.None;
            c.SchedulePostMode = PostMode.Edit;
            c.TemplateMap = new Dictionary<string, string> { [MessageSlots.ScheduleHeading] = "{header}\n{changes}" };
        });
        SetSchedules();
        var notifier = Notifier();
        var first = await notifier.TickAsync(CancellationToken.None);
        Assert.Equal(2, first.Sent);
        var posts = f.Db.SchedulePostThreads.AsNoTracking().ToDictionary(p => p.RegionId);
        Assert.Equal(2, posts.Count);
        Assert.NotEqual(posts["kyiv"].MessageId, posts[RegionB].MessageId);
        Assert.StartsWith("📍 м. Київ\n", f.Bot.Sent.First().Text);
        Assert.StartsWith("📍 Київська область\n", f.Bot.Sent.Last().Text);
        Assert.Contains(f.Db.KeyValues, row => row.Key.StartsWith($"schedule-plan:{ChatId}:kyiv:"));
        Assert.Contains(f.Db.KeyValues, row => row.Key.StartsWith($"schedule-plan:{ChatId}:{RegionB}:"));
        provider.Snapshots[RegionB] = RealSnapshot() with
        {
            DataFile = "kyiv-region",
            Groups = new Dictionary<string, GroupSchedule>
            {
                [Group] = new(Group, [new(Kyiv(15, 11), Kyiv(15, 15))]),
            },
        };
        var update = await notifier.TickAsync(CancellationToken.None);
        Assert.Equal(1, update.Edited);
        var edit = Assert.Single(f.Bot.Requests.OfType<EditMessageTextRequest>());
        Assert.Equal(posts[RegionB].MessageId, edit.MessageId);
        Assert.Contains("додано планове відключення 14:00–15:00", edit.Text);
        channel.Addresses.RemoveAll(a => a.RegionId == RegionB);
        f.Db.SaveChanges();
        await notifier.TickAsync(CancellationToken.None);
        Assert.Equal("kyiv", Assert.Single(f.Db.SchedulePostThreads.AsNoTracking()).RegionId);
        Assert.DoesNotContain(f.Db.KeyValues, row => row.Key.StartsWith($"schedule-plan:{ChatId}:{RegionB}:"));
        Assert.Equal("kyiv", f.LoadChannel(ChatId).RegionId);
    }

    [Fact]
    public async Task ScheduleEvents_SameGroupAcrossRegions_ClearOnlyTheirOwnEpisode()
    {
        AddChannel(c => { c.ScheduleNotify = true; c.ScheduleImage = ScheduleImage.None; });
        SetSchedules();
        f.Clock.Now = Kyiv(15, 12);
        var notifier = Notifier();
        Assert.Equal(2, (await notifier.TickAsync(CancellationToken.None)).Sent);
        var threads = f.Db.ScheduleThreads.AsNoTracking().ToDictionary(t => t.RegionId);
        provider.Snapshots[RegionB] = RealSnapshot() with
        {
            DataFile = "kyiv-region",
            Groups = new Dictionary<string, GroupSchedule> { [Group] = new(Group, [new(Kyiv(15, 11), Kyiv(15, 12))]) },
        };
        Assert.Equal(1, (await notifier.TickAsync(CancellationToken.None)).Edited);
        var edit = Assert.Single(f.Bot.Requests.OfType<EditMessageTextRequest>());
        Assert.Equal(threads[RegionB].MessageId, edit.MessageId);
        var states = f.Db.ScheduleThreads.AsNoTracking().ToDictionary(t => t.RegionId);
        Assert.Equal(SchedulePhase.Off, states["kyiv"].Phase);
        Assert.Equal(SchedulePhase.On, states[RegionB].Phase);
    }

    [Fact]
    public async Task Reminders_SameGroupAndStartAcrossRegions_DedupeWithinEachRegionOnly()
    {
        AddChannel(c => { c.ScheduleReminder = true; c.ScheduleReminderMinutes = 30; });
        SetSchedules();
        f.Clock.Now = Kyiv(15, 10, 45);
        var notifier = Notifier();
        Assert.Equal(2, (await notifier.TickAsync(CancellationToken.None)).Sent);
        Assert.Equal(0, (await notifier.TickAsync(CancellationToken.None)).Sent);
        var reminders = f.Db.ScheduleReminderDeliveries.AsNoTracking().ToArray();
        Assert.Equal(2, reminders.Length);
        Assert.Equal(2, reminders.Select(r => r.RegionId).Distinct().Count());
        Assert.All(reminders, r => Assert.Equal(Group, r.GroupId));
    }

    [Fact]
    public async Task AddressOnlySecondaryRegionWithNoLegacyGroup_StillReceivesSchedules()
    {
        AddChannel(c =>
        {
            c.Addresses.RemoveAll(a => a.RegionId == "kyiv");
            c.MirrorPrimary();
            c.SchedulePost = true;
            c.SchedulePostImage = ScheduleImage.None;
        });
        SetSchedules();
        Assert.Equal(1, (await Notifier().TickAsync(CancellationToken.None)).Sent);
        Assert.Equal(RegionB, Assert.Single(f.Db.SchedulePostThreads.AsNoTracking()).RegionId);
        Assert.Null(f.LoadChannel(ChatId).GroupId);
    }

    [Fact]
    public async Task ReadOnlyPreviewAndManualPost_IncludeEachRegionWithoutCrossRegionGroups()
    {
        var channel = AddChannel(c => c.SchedulePostImage = ScheduleImage.None);
        SetSchedules();
        var notifier = Notifier();
        var preview = await notifier.PreviewCombinedAsync(channel, CancellationToken.None);
        Assert.Contains("📍 м. Київ\n", preview);
        Assert.Contains("📍 Київська область\n", preview);
        Assert.Empty(f.Bot.Requests);
        Assert.Empty(f.Db.SchedulePostThreads);
        var result = await notifier.SendScheduleNowAsync(channel, CancellationToken.None);
        Assert.True(result.Sent);
        Assert.Equal(2, f.Db.SchedulePostThreads.Count());
    }

    [Fact]
    public async Task ScheduleEvents_MultipleGroupsInOneRegion_KeepSeparateMessagesAndDropRemovedGroup()
    {
        var channel = AddChannel(c =>
        {
            c.Addresses.RemoveAll(a => a.RegionId == RegionB);
            c.Addresses[0].Groups.Add(new ChatAddressGroup { GroupId = "2.1" });
            c.MirrorPrimary();
            c.ScheduleNotify = true;
            c.ScheduleImage = ScheduleImage.None;
        });
        var snapshot = RealSnapshot();
        provider.Snapshots["kyiv"] = snapshot with
        {
            Groups = new Dictionary<string, GroupSchedule>
            {
                [Group] = snapshot.Find(Group)!,
                ["2.1"] = new("2.1", [new(Kyiv(15, 11), Kyiv(15, 13))]),
            },
        };
        var notifier = Notifier();
        Assert.Equal(2, (await notifier.TickAsync(CancellationToken.None)).Sent);
        var threads = f.Db.ScheduleThreads.AsNoTracking().ToArray();
        Assert.Equal(2, threads.Length);
        Assert.Equal(2, threads.Select(t => t.MessageId).Distinct().Count());
        Assert.All(f.Bot.Sent, sent => Assert.Contains("<b>Черга ", sent.Text));
        Assert.Equal(0, (await notifier.TickAsync(CancellationToken.None)).Sent);
        channel.Addresses[0].Groups.RemoveAll(g => g.GroupId == "2.1");
        channel.MirrorPrimary();
        f.Db.SaveChanges();
        await notifier.TickAsync(CancellationToken.None);
        Assert.Equal(Group, Assert.Single(f.Db.ScheduleThreads.AsNoTracking()).GroupId);
    }

    [Fact]
    public async Task LegacyScheduleBaseline_IsMovedToItsOwnRegionWithoutLosingComparison()
    {
        AddChannel(c =>
        {
            c.SchedulePost = true;
            c.SchedulePostImage = ScheduleImage.None;
            c.SchedulePostMode = PostMode.Edit;
            c.TemplateMap = new Dictionary<string, string> { [MessageSlots.ScheduleHeading] = "{header}\n{changes}" };
        });
        SetSchedules();
        var baseline = System.Text.Json.JsonSerializer.Serialize(new
        {
            RegionId = "kyiv",
            Days = ScheduleText.PlannedDays(f.LoadChannel(ChatId).ForRegion("kyiv"), RealSnapshot(), f.Clock.Now),
        });
        for (var index = 0; index * 256 < baseline.Length; index++)
            f.Db.KeyValues.Add(new KeyValue
            {
                Key = $"schedule-plan:{ChatId}:{index:D6}",
                Value = baseline.Substring(index * 256, Math.Min(256, baseline.Length - index * 256)),
            });
        f.Db.SaveChanges();
        provider.Snapshots["kyiv"] = RealSnapshot() with
        {
            Groups = new Dictionary<string, GroupSchedule> { [Group] = new(Group, [new(Kyiv(15, 11), Kyiv(15, 15))]) },
        };
        await Notifier().TickAsync(CancellationToken.None);
        Assert.DoesNotContain(f.Db.KeyValues, row => row.Key.Split(':').Length == 3);
        Assert.Contains(f.Db.KeyValues, row => row.Key.StartsWith($"schedule-plan:{ChatId}:kyiv:"));
        Assert.Contains(f.Db.KeyValues, row => row.Key.StartsWith($"schedule-plan:{ChatId}:{RegionB}:"));
        Assert.Contains("додано планове відключення 14:00–15:00", f.Bot.Sent.First().Text);
        Assert.DoesNotContain(f.Bot.Sent.Last().Text, "Зміни планового графіка");
    }

    [Fact]
    public async Task TelegramFailure_DeactivatesSavedChannel_AndStopsRemainingRegionalSchedules()
    {
        AddChannel(c => { c.SchedulePost = true; c.SchedulePostImage = ScheduleImage.None; });
        SetSchedules();
        f.Bot.Failure = request => request is SendMessageRequest
            ? FakeTelegramBotClient.Forbidden()
            : null;
        var result = await Notifier().TickAsync(CancellationToken.None);
        Assert.Equal(1, result.Failed);
        Assert.False(f.LoadChannel(ChatId).IsActive);
        Assert.Empty(f.Db.SchedulePostThreads);
    }

    [Fact]
    public async Task CatchUp_ReplaysEachAddressRegionGroupWithoutDuplicates()
    {
        var channel = AddChannel();
        foreach (var regionId in channel.RegionIds())
            f.Db.GroupStates.Add(new GroupState
            {
                RegionId = regionId,
                GroupId = Group,
                Today = GroupStatuses.Emergency,
                UpdatedAt = f.Clock.Now,
            });
        f.Db.SaveChanges();
        var coordinator = new RegionAlertCoordinator(f.Db, f.Dispatcher, f.Classifier, f.Options,
            NullLogger<RegionAlertCoordinator>.Instance);
        var catchUp = new AlertCatchUp(f.Db, coordinator, f.Dispatcher, f.Options, NullLogger<AlertCatchUp>.Instance);
        await catchUp.RunAsync(channel, CancellationToken.None);
        Assert.Equal(2, f.Bot.Sent.Count());
        Assert.Equal(2, f.Deliveries(ChatId).Count);
        await catchUp.RunAsync(channel, CancellationToken.None);
        Assert.Equal(2, f.Bot.Sent.Count());
    }

    [Fact]
    public async Task MissingRegionalSchedule_IsUnknownWhileTheOtherRegionCanUpdate()
    {
        var channel = AddChannel(c => { c.ScheduleNotify = true; c.ScheduleImage = ScheduleImage.None; });
        SetSchedules();
        var notifier = Notifier();
        Assert.Equal(2, (await notifier.TickAsync(CancellationToken.None)).Sent);
        provider.Snapshots.Remove(RegionB);
        provider.Snapshots["kyiv"] = RealSnapshot() with
        {
            Groups = new Dictionary<string, GroupSchedule> { [Group] = new(Group, [new(Kyiv(15, 11), Kyiv(15, 12))]) },
        };
        Assert.Equal(1, (await notifier.TickAsync(CancellationToken.None)).Edited);
        var threads = f.Db.ScheduleThreads.AsNoTracking().ToDictionary(t => t.RegionId);
        Assert.Equal(SchedulePhase.Off, threads[RegionB].Phase);
        Assert.Equal(SchedulePhase.On, threads["kyiv"].Phase);
        var requests = f.Bot.Requests.Count;
        var preview = await notifier.PreviewCombinedAsync(channel, CancellationToken.None);
        Assert.Contains("📍 Київська область\nГрафіки тимчасово недоступні.", preview);
        Assert.Equal(requests, f.Bot.Requests.Count);
    }
}
