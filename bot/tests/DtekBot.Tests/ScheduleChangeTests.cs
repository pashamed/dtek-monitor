using System.Text.Json;
using DtekBot.Api.Data;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class ScheduleChangeTests : IDisposable
{
    private readonly DispatcherFixture f = new();
    private readonly TestClock clock = new(Kyiv(15, 12));
    private readonly FakeScheduleProvider provider = new();

    public void Dispose() => f.Dispose();

    private ScheduleNotifier Notifier() => new(f.Db, f.Bot, provider, f.Options, clock, NullLogger<ScheduleNotifier>.Instance, f.CreateMirror(time: clock));

    // The default heading has no change summary; these tests opt in the way a custom template does.
    private const string HeadingWithChanges = "❗️ {header}\n📅 {weekday} {date}{#changes}\n\n{changes}{/changes}";

    private Channel AddChannel(Action<Channel>? configure = null) => f.AddChannel(801, channel =>
    {
        channel.GroupId = "45.1";
        channel.SchedulePost = true;
        channel.SchedulePostImage = ScheduleImage.None;
        channel.TemplateMap = new Dictionary<string, string> { [MessageSlots.ScheduleHeading] = HeadingWithChanges };
        configure?.Invoke(channel);
    });

    private static ScheduleSnapshot Plan(params (string Group, OutageInterval[] Outages)[] groups) =>
        RealSnapshot() with { Groups = groups.ToDictionary(g => g.Group, g => new GroupSchedule(g.Group, g.Outages)) };

    private static OutageInterval Out(int day, int start, int end) => new(Kyiv(day, start), Kyiv(day, end));

    private Task<ScheduleTickSummary> Tick() => Notifier().TickAsync(CancellationToken.None);

    private static IReadOnlyDictionary<string, string> Changes(Channel channel, ScheduleSnapshot before, ScheduleSnapshot after,
        DateTimeOffset? at = null)
    {
        var now = at ?? Kyiv(15, 12);
        return ScheduleText.PlannedChanges(channel, ScheduleText.PlannedDays(channel, before, now),
            ScheduleText.PlannedDays(channel, after, now), now);
    }

    [Fact]
    public void Changes_ReportsOnlyAddedAndRemovedPortions_NotWholeShiftedIntervals()
    {
        var channel = new Channel { GroupId = "45.1" };
        var changes = Changes(channel, Plan(("45.1", [Out(15, 11, 14)])), Plan(("45.1", [Out(15, 12, 15)])));

        Assert.Equal("🔄 Зміни планового графіка:\nЧерга 45.1: додано планове відключення 14:00–15:00; скасовано планове відключення 11:00–12:00",
            changes["2026-10-15"]);
        Assert.Equal("", changes["2026-10-16"]);
    }

    [Fact]
    public void Changes_UnionNormalizationIgnoresOrderingOverlapAndAdjacentSplits()
    {
        var channel = new Channel { GroupId = "45.1" };
        var before = Plan(("45.1", [Out(15, 11, 14)]));
        var after = Plan(("45.1", [Out(15, 13, 14), Out(15, 12, 13), Out(15, 11, 13)]));

        Assert.All(Changes(channel, before, after).Values, value => Assert.Equal("", value));
    }

    [Fact]
    public void Changes_EmptyKnownPlanCancelsOutages_ButMissingGroupOrDayDoesNot()
    {
        var channel = new Channel { GroupId = "45.1" };
        var before = Plan(("45.1", [Out(15, 11, 14), Out(16, 11, 14)]));
        var empty = Plan(("45.1", []));

        Assert.Contains("скасовано планове відключення 11:00–14:00", Changes(channel, before, empty)["2026-10-15"]);
        Assert.All(Changes(channel, before, Plan(("other", []))).Values, value => Assert.Empty(value));
        var limited = empty with { CoverageEnd = Kyiv(16, 0) };
        Assert.Empty(Changes(channel, before, limited)["2026-10-16"]);
        var partial = empty with { CoverageStart = Kyiv(15, 1) };
        Assert.Empty(Changes(channel, before, partial)["2026-10-15"]);
    }

    [Fact]
    public void Changes_AndRenderingDoNotTreatUnpublishedGroupDayAsEmpty()
    {
        var channel = new Channel { GroupId = "45.1" };
        var before = Plan(("45.1", [Out(15, 11, 14), Out(16, 11, 14)]));
        var after = Plan(("45.1", [Out(15, 11, 14)])) with
        {
            Groups = new Dictionary<string, GroupSchedule>
            {
                ["45.1"] = new("45.1", [Out(15, 11, 14)], [Kyiv(15, 0)]),
            },
        };
        Assert.Empty(Changes(channel, before, after)["2026-10-16"]);
        var text = ScheduleText.RenderPost(channel, after, "Київ", Kyiv(15, 12), true, []);
        Assert.EndsWith("Графік тимчасово недоступний.", text);
        Assert.DoesNotContain(ScheduleText.NoOutages, text);
        Assert.Empty(ScheduleText.PostValues("Київ", "45.1", after, Kyiv(15, 12), true)["tomorrow"]);
    }

    [Fact]
    public void Changes_DeduplicatesSharedGroupsAndAttributesEveryAddress()
    {
        var channel = new Channel { Addresses =
        [
            new() { Name = "Дім <1>", Groups = [new() { GroupId = "45.1" }, new() { GroupId = "2.1", Label = "Гараж" }] },
            new() { Name = "Офіс", Groups = [new() { GroupId = "45.1" }] },
        ] };
        var before = Plan(("45.1", [Out(15, 11, 14)]), ("2.1", []));
        var after = Plan(("45.1", []), ("2.1", [Out(15, 15, 16)]));
        var changes = Changes(channel, before, after);

        Assert.Contains("Черга 2.1 · Дім <1> · Гараж: додано", changes["2026-10-15"]);
        Assert.Contains("Черга 45.1 · Дім <1>, Офіс: скасовано", changes["2026-10-15"]);
        Assert.Equal(1, changes["2026-10-15"].Split("Черга 45.1").Length - 1);
        var text = ScheduleText.RenderPost(channel, after, "Київ", Kyiv(15, 12), true, [], changes);
        Assert.Contains("Дім &lt;1&gt;", text);
        Assert.DoesNotContain("Дім <1>", text);
    }

    [Fact]
    public void Changes_ClipsCrossMidnightOutagesToMatchingDates_AndFormatsMidnight()
    {
        var channel = new Channel { GroupId = "45.1" };
        var before = Plan(("45.1", []));
        var after = Plan(("45.1", [new(Kyiv(15, 23), Kyiv(16, 1))]));
        var changes = Changes(channel, before, after);

        Assert.Contains("23:00–24:00", changes["2026-10-15"]);
        Assert.Contains("00:00–01:00", changes["2026-10-16"]);
    }

    [Fact]
    public void RenderPost_CustomHeadingDoesNotOptIn_IsUnchanged_AndStoredTemplateIsUntouched()
    {
        var channel = new Channel { GroupId = "45.1" };
        channel.TemplateMap = new Dictionary<string, string> { [MessageSlots.ScheduleHeading] = "Мій {date}" };
        var saved = channel.Templates;
        var after = Plan(("45.1", [Out(15, 14, 15)]));
        var changes = Changes(channel, Plan(("45.1", [])), after);

        Assert.Equal(CombinedSchedule.Render(channel, after, "Київ", Kyiv(15, 12), true),
            ScheduleText.RenderPost(channel, after, "Київ", Kyiv(15, 12), true, [], changes));
        Assert.Equal(saved, channel.Templates);
    }

    [Fact]
    public void RenderPost_CustomHeadingCanOptIn_SeparatelyForEachDay()
    {
        var channel = new Channel { GroupId = "45.1" };
        channel.TemplateMap = new Dictionary<string, string>
        {
            [MessageSlots.ScheduleHeading] = "Мій {date}{#changes}\n{changes}{/changes}",
        };
        var before = Plan(("45.1", []));
        var after = Plan(("45.1", [Out(16, 14, 15)]));
        var text = ScheduleText.RenderPost(channel, after, "Київ", Kyiv(15, 12), true, [], Changes(channel, before, after));

        Assert.StartsWith("Мій 15.10.2026\n\n" + ScheduleText.NoOutages, text);
        Assert.Contains("Мій 16.10.2026\n🔄 Зміни планового графіка:", text);
        Assert.Equal(1, text.Split("додано планове відключення").Length - 1);
        Assert.DoesNotContain("{changes}", text);
    }

    [Fact]
    public async Task Tick_DefaultHeading_DoesNotShowTheChangeSummary()
    {
        AddChannel(channel => channel.TemplateMap = new Dictionary<string, string>());
        provider.Snapshots["kyiv"] = Plan(("45.1", [Out(15, 11, 14)]));
        await Tick();
        provider.Snapshots["kyiv"] = Plan(("45.1", [Out(15, 11, 15)]));

        Assert.Equal(1, (await Tick()).Sent);
        Assert.DoesNotContain("Зміни планового графіка", f.Bot.Sent.Last().Text);
        Assert.DoesNotContain("додано планове відключення", f.Bot.Sent.Last().Text);
        Assert.Contains("11:00 - 15:00", f.Bot.Sent.Last().Text);
    }

    [Fact]
    public async Task Tick_PersistsBaselineAcrossNotifierAndDbTrackingReset_AndDoesNotRepeat()
    {
        AddChannel();
        provider.Snapshots["kyiv"] = Plan(("45.1", [Out(15, 11, 14)]));
        await Tick();
        f.Db.ChangeTracker.Clear();
        provider.Snapshots["kyiv"] = Plan(("45.1", [Out(15, 11, 15)]));

        Assert.Equal(1, (await Tick()).Sent);
        Assert.Contains("додано планове відключення 14:00–15:00", f.Bot.Sent.Last().Text);
        Assert.DoesNotContain("Світло з", f.Bot.Sent.Last().Text);
        Assert.Equal(1, (await Tick()).Skipped);
        Assert.Equal(2, f.Bot.Sent.Count());
    }

    [Fact]
    public async Task Tick_FailedSendDoesNotAdvanceBaseline_RetryStillHasChanges()
    {
        AddChannel();
        provider.Snapshots["kyiv"] = Plan(("45.1", [Out(15, 11, 14)]));
        await Tick();
        var saved = string.Concat(f.Db.KeyValues.OrderBy(v => v.Key).Select(v => v.Value));
        provider.Snapshots["kyiv"] = Plan(("45.1", []));
        f.Bot.Failure = _ => new HttpRequestException("offline");

        Assert.Equal(1, (await Tick()).Failed);
        Assert.Equal(saved, string.Concat(f.Db.KeyValues.OrderBy(v => v.Key).Select(v => v.Value)));
        f.Bot.Failure = null;
        Assert.Equal(1, (await Tick()).Sent);
        Assert.Contains("скасовано планове відключення 11:00–14:00", f.Bot.Sent.Last().Text);
    }

    [Fact]
    public async Task Tick_RolloverComparesYesterdayTomorrowWithToday_NotYesterdayToday()
    {
        AddChannel();
        provider.Snapshots["kyiv"] = Plan(("45.1", [Out(15, 11, 14), Out(16, 17, 19)]));
        await Tick();
        clock.Now = Kyiv(16, 12);
        provider.Snapshots["kyiv"] = Plan(("45.1", [Out(16, 17, 20)])) with
        {
            CoverageStart = Kyiv(16, 0), CoverageEnd = Kyiv(17, 0),
        };

        Assert.Equal(1, (await Tick()).Sent);
        var text = f.Bot.Sent.Last().Text;
        Assert.Contains("додано планове відключення 19:00–20:00", text);
        Assert.DoesNotContain("скасовано", text);
        Assert.DoesNotContain("11:00", text);
    }

    [Fact]
    public async Task Tick_UnavailableGroupDoesNotEraseBaseline_AndRecoveredPlanComparesCorrectly()
    {
        AddChannel(channel => channel.Addresses =
        [
            new() { Name = "Дім", RegionId = "kyiv", Groups = [new() { GroupId = "45.1" }, new() { GroupId = "2.1" }] },
        ]);
        provider.Snapshots["kyiv"] = Plan(("45.1", [Out(15, 11, 14)]), ("2.1", []));
        await Tick();
        provider.Snapshots["kyiv"] = Plan(("2.1", []));
        await Tick();
        Assert.DoesNotContain("скасовано", f.Bot.Sent.Last().Text);
        provider.Snapshots["kyiv"] = Plan(("45.1", [Out(15, 11, 15)]), ("2.1", []));

        await Tick();
        Assert.Contains("додано планове відключення 14:00–15:00", f.Bot.Sent.Last().Text);
    }

    [Fact]
    public async Task Tick_NewRegionOrNewGroupDoesNotReuseUnrelatedBaseline()
    {
        AddChannel();
        provider.Snapshots["kyiv"] = Plan(("45.1", [Out(15, 11, 14)]));
        await Tick();
        f.Db.Channels.Single().GroupId = "2.1";
        await f.Db.SaveChangesAsync();
        provider.Snapshots["kyiv"] = Plan(("2.1", [Out(15, 17, 19)]));
        await Tick();
        Assert.DoesNotContain("Зміни планового графіка", f.Bot.Sent.Last().Text);

        var channel = f.Db.Channels.Single();
        channel.RegionId = "kyiv-oblast";
        await f.Db.SaveChangesAsync();
        provider.Snapshots["kyiv-oblast"] = Plan(("2.1", [Out(15, 11, 14)]));
        await Tick();
        Assert.DoesNotContain("Зміни планового графіка", f.Bot.Sent.Last().Text);
    }

    [Fact]
    public async Task Tick_ChunksStateWithinColumnLimits_PrunesOldDatesGroupsAndDeletedChats()
    {
        AddChannel(channel => channel.Addresses =
        [
            new() { Name = "Дім", RegionId = "kyiv",
                Groups = Enumerable.Range(1, 20).Select(id => new ChatAddressGroup { GroupId = $"{id}.1" }).ToList() },
        ]);
        provider.Snapshots["kyiv"] = Plan(Enumerable.Range(1, 20)
            .Select(id => ($"{id}.1", new[] { Out(15, 11, 14), Out(16, 17, 19) })).ToArray());
        await Tick();
        Assert.True(f.Db.KeyValues.Count() > 1);
        Assert.All(f.Db.KeyValues, row => { Assert.True(row.Key.Length <= 64); Assert.True(row.Value.Length <= 256); });
        var channel = f.Db.Channels.Single();
        channel.Addresses.Clear();
        channel.GroupId = "1.1";
        clock.Now = Kyiv(16, 12);
        provider.Snapshots["kyiv"] = Plan(("1.1", [Out(16, 17, 19)])) with
        {
            CoverageStart = Kyiv(16, 0), CoverageEnd = Kyiv(17, 0),
        };
        await f.Db.SaveChangesAsync();
        await Tick();
        var json = string.Concat(f.Db.KeyValues.OrderBy(v => v.Key).Select(v => v.Value));
        using var doc = JsonDocument.Parse(json);
        var days = doc.RootElement.GetProperty("Days");
        Assert.All(days.EnumerateObject(), day => Assert.StartsWith("2026-10-16|1.1", day.Name));
        channel.IsActive = false;
        await f.Db.SaveChangesAsync();
        await Tick();
        Assert.Empty(f.Db.KeyValues);
    }

    [Fact]
    public async Task Tick_MalformedStoredStateStartsCleanWithoutInventingChanges()
    {
        AddChannel();
        f.Db.KeyValues.Add(new KeyValue { Key = "schedule-plan:801:000000", Value = "{invalid-json" });
        await f.Db.SaveChangesAsync();
        provider.Snapshots["kyiv"] = Plan(("45.1", [Out(15, 11, 14)]));

        Assert.Equal(1, (await Tick()).Sent);
        Assert.DoesNotContain("Зміни планового графіка", f.Bot.Sent.Last().Text);
        var json = string.Concat(f.Db.KeyValues.OrderBy(v => v.Key).Select(v => v.Value));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("kyiv", doc.RootElement.GetProperty("RegionId").GetString());
    }
}
