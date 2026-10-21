using DtekBot.Api.Data;
using DtekBot.Api.MiniApp;
using DtekBot.Api.Parsing;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class ChatStatusServiceTests : IDisposable
{
    private const long Chat = 1100;

    private readonly DispatcherFixture _f = new();
    private readonly TestClock _clock = new(Kyiv(15, 12));
    private readonly FakeScheduleProvider _schedules = new();
    private readonly ChatStatusService _service;

    public ChatStatusServiceTests()
    {
        _service = new ChatStatusService(_f.Db, _schedules, _f.Options, _clock);
        _schedules.Snapshots["kyiv"] = RealSnapshot();
    }

    public void Dispose() => _f.Dispose();

    private Channel AddChannel(Action<Channel>? configure = null, string regionId = "kyiv") =>
        _f.AddChannel(Chat, configure, regionId);

    private Task<IReadOnlyList<string>> BuildAsync(Channel? channel, string? regionId = null) =>
        _service.BuildAsync(channel, CancellationToken.None, regionId);

    private void StoreState(
        PopupKind kind,
        string regionId = "kyiv",
        DateTimeOffset? observed = null,
        IReadOnlyList<string>? partIds = null)
    {
        _f.Db.RegionStates.Add(new RegionState
        {
            RegionId = regionId,
            Kind = kind,
            PartIdList = partIds,
            Hash = "h",
            ObservedAt = observed ?? new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _f.Db.SaveChanges();
    }

    private void StoreGroup(string today, string groupId = "45.1", string regionId = "kyiv")
    {
        _f.Db.GroupStates.Add(new GroupState
        {
            RegionId = regionId,
            GroupId = groupId,
            Today = today,
            Tomorrow = "ScheduleApplies",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _f.Db.SaveChanges();
    }

    private const string ChooseRegion = "Оберіть регіон, щоб побачити поточний стан.";

    // ---- no region ----

    [Fact]
    public async Task NullChannel_AsksToChooseARegion()
    {
        Assert.Equal([ChooseRegion], await BuildAsync(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("atlantis")]
    public async Task ChannelWithoutAKnownRegion_AsksToChooseARegion(string regionId)
    {
        var channel = new Channel { ChatId = Chat, RegionId = regionId };

        Assert.Equal([ChooseRegion], await BuildAsync(channel));
    }

    [Fact]
    public async Task NoRegion_DoesNotReadAnyState()
    {
        StoreState(PopupKind.Emergency);

        Assert.Equal([ChooseRegion], await BuildAsync(null));
        Assert.Empty(_schedules.Calls);
    }

    // ---- popup state ----

    [Theory]
    [InlineData(PopupKind.Emergency, "🚨 На сайті ДТЕК діють екстрені відключення.")]
    [InlineData(PopupKind.Accident, "⚠️ На сайті ДТЕК повідомлено про аварію в енергосистемі.")]
    [InlineData(PopupKind.Unknown, "ℹ️ На сайті ДТЕК є невизначене повідомлення.")]
    [InlineData(PopupKind.Stabilization, "🗓 Діють стабілізаційні відключення (за графіками).")]
    [InlineData(PopupKind.None, "ℹ️ Екстрених відключень на сайті ДТЕК не зазначено.")]
    [InlineData(PopupKind.Cancelled, "ℹ️ Екстрених відключень на сайті ДТЕК не зазначено.")]
    public async Task PopupLine_ForEachKind(PopupKind kind, string expected)
    {
        StoreState(kind);
        var channel = AddChannel();

        var lines = await BuildAsync(channel);

        Assert.Equal(expected, lines[0]);
    }

    [Fact]
    public async Task WithAState_TheNextLineIsTheCheckedTime_InKyivTime()
    {
        StoreState(PopupKind.Emergency, "odesa", new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));
        var channel = AddChannel(regionId: "odesa");

        var lines = await BuildAsync(channel);

        Assert.Equal(["🚨 На сайті ДТЕК діють екстрені відключення.", "Перевірено: 15.07 15:00 (час Києва · Одеська область)"], lines);
    }

    [Fact]
    public async Task WinterTime_IsUtcPlusTwo()
    {
        StoreState(PopupKind.None, "odesa", new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero));
        var channel = AddChannel(regionId: "odesa");

        var lines = await BuildAsync(channel);

        Assert.Equal("Перевірено: 15.01 14:00 (час Києва · Одеська область)", lines[1]);
    }

    [Fact]
    public async Task WithoutAState_SaysThereIsNoDataYet_AndHasNoCheckedLine()
    {
        var channel = AddChannel(regionId: "odesa");

        var lines = await BuildAsync(channel);

        Assert.Equal(["Даних із сайту ДТЕК ще немає."], lines);
    }

    [Fact]
    public async Task StateOfAnotherRegion_IsIgnored()
    {
        StoreState(PopupKind.Emergency, "odesa");
        var channel = AddChannel();
        _schedules.Snapshots["kyiv"] = null;

        var lines = await BuildAsync(channel);

        Assert.Equal(["Даних із сайту ДТЕК ще немає."], lines);
    }

    [Fact]
    public async Task RequestedRegion_IsUsedInsteadOfSavedRegion()
    {
        StoreState(PopupKind.Emergency, "odesa");
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel, "odesa");

        Assert.Equal("🚨 На сайті ДТЕК діють екстрені відключення.", lines[0]);
        Assert.DoesNotContain(lines, line => line.Contains("45.1"));
        Assert.Empty(_schedules.Calls);
    }

    [Fact]
    public async Task EmergencyPopupForOtherDistricts_IsNotShownAsApplyingToChat()
    {
        StoreState(PopupKind.Emergency, partIds: ["north"]);
        var channel = AddChannel(c => c.PartIdList = ["south"]);

        var lines = await BuildAsync(channel);

        Assert.Equal("ℹ️ Для обраних районів екстрених відключень на сайті ДТЕК не зазначено.", lines[0]);
        Assert.DoesNotContain(lines, line => line.Contains("🚨"));
    }

    // ---- Yasno group emergency ----

    [Fact]
    public async Task GroupEmergency_AddsTheYasnoLine_AfterTheCheckedLine()
    {
        StoreState(PopupKind.None);
        StoreGroup(GroupStatuses.Emergency);
        _clock.Now = Kyiv(15, 15);
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel);

        Assert.Equal(
            [
                "ℹ️ Екстрених відключень на сайті ДТЕК не зазначено.",
                "Перевірено: 15.07 15:00 (час Києва · м. Київ)",
                "🚨 Для черги 45.1 діють екстрені відключення (за даними Yasno).",
                "🗓 За графіком (черга 45.1) зараз відключення не заплановано. Наступне відключення о 20:30.",
            ],
            lines);
    }

    [Theory]
    [InlineData("ScheduleApplies", "45.1")]
    [InlineData("WaitingForSchedule", "45.1")]
    [InlineData(GroupStatuses.Emergency, "46.1")]
    public async Task GroupState_NotEmergencyForThisGroup_AddsNoYasnoLine(string today, string groupId)
    {
        StoreGroup(today, groupId);
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel);

        Assert.DoesNotContain(lines, l => l.Contains("Yasno"));
    }

    [Fact]
    public async Task GroupEmergency_WithoutGroupId_IsNotShown()
    {
        StoreGroup(GroupStatuses.Emergency);
        var channel = AddChannel();

        Assert.DoesNotContain(await BuildAsync(channel), l => l.Contains("Yasno"));
    }

    [Fact]
    public async Task GroupEmergency_OfAnotherRegion_IsNotShown()
    {
        StoreGroup(GroupStatuses.Emergency, regionId: "odesa");
        var channel = AddChannel(c => c.GroupId = "45.1");

        Assert.DoesNotContain(await BuildAsync(channel), l => l.Contains("Yasno"));
    }

    // ---- schedule ----

    [Fact]
    public async Task InsideAnOutage_ShowsSinceAndExpectedReturn()
    {
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel);

        Assert.Equal("🗓 За графіком (черга 45.1) заплановано відключення з 11:00. Планове завершення о 14:00.", lines.Last());
        Assert.Equal(["kyiv"], _schedules.Calls);
    }

    [Fact]
    public async Task OutsideAnOutage_ShowsTheNextOutage()
    {
        _clock.Now = Kyiv(15, 15);
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel);

        Assert.Equal("🗓 За графіком (черга 45.1) зараз відключення не заплановано. Наступне відключення о 20:30.", lines.Last());
    }

    [Fact]
    public async Task EveningOutage_ShowsTheNextDayReturnWithItsDate()
    {
        _clock.Now = Kyiv(15, 21);
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel);

        Assert.Equal("🗓 За графіком (черга 45.1) заплановано відключення з 20:30. Планове завершення о 00:00 (16.10).", lines.Last());
    }

    [Fact]
    public async Task NextOutageTomorrow_ShowsItsDate()
    {
        _clock.Now = Kyiv(15, 23, 59, 59).AddSeconds(2);
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel);

        Assert.Equal("🗓 За графіком (черга 45.1) зараз відключення не заплановано. Наступне відключення о 06:00.", lines.Last());
    }

    [Fact]
    public async Task AfterTheLastOutage_SaysThereAreNoMoreOutages()
    {
        _clock.Now = Kyiv(16, 22);
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel);

        Assert.Equal("🗓 За графіком (черга 45.1) зараз відключення не заплановано. Наступних відключень за графіком поки немає.", lines.Last());
    }

    [Fact]
    public async Task OutageThatStartedBeforeTheData_HasNoSince()
    {
        _schedules.Snapshots["kyiv"] = ScheduleParser.Parse(
            Json("v", "16.10.2026 00:10", (Day2, "45.1", Hours("no", 1, 2, 3))), "kyiv", Kyiv(16, 0, 10))!;
        _clock.Now = Kyiv(16, 1);
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel);

        Assert.Equal("🗓 За графіком (черга 45.1) заплановано відключення. Планове завершення о 03:00.", lines.Last());
    }

    [Fact]
    public async Task OutageThatEndsAfterTheData_HasNoExpectedReturn()
    {
        _schedules.Snapshots["kyiv"] = ScheduleParser.Parse(
            Json("v", "15.10.2026 10:00", (Day1, "45.1", Hours("no", 23, 24))), "kyiv", Kyiv(15, 10))!;
        _clock.Now = Kyiv(15, 23);
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel);

        Assert.Equal("🗓 За графіком (черга 45.1) заплановано відключення з 22:00.", lines.Last());
    }

    [Fact]
    public async Task ProviderReturnsNull_HasNoScheduleLine()
    {
        _schedules.Snapshots["kyiv"] = null;
        StoreState(PopupKind.None);
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel);

        Assert.Equal(["ℹ️ Екстрених відключень на сайті ДТЕК не зазначено.", "Перевірено: 15.07 15:00 (час Києва · м. Київ)"], lines);
    }

    [Fact]
    public async Task UnknownGroup_HasNoScheduleLine()
    {
        var channel = AddChannel(c => c.GroupId = "99.9");

        var lines = await BuildAsync(channel);

        Assert.Equal(["Даних із сайту ДТЕК ще немає."], lines);
    }

    [Fact]
    public async Task OutsideTheCoverage_HasNoScheduleLine()
    {
        _clock.Now = Kyiv(20, 12);
        var channel = AddChannel(c => c.GroupId = "45.1");

        var lines = await BuildAsync(channel);

        Assert.DoesNotContain(lines, l => l.Contains("За графіком"));
    }

    [Fact]
    public async Task WithoutAGroup_TheProviderIsNotAsked()
    {
        var channel = AddChannel();

        await BuildAsync(channel);

        Assert.Empty(_schedules.Calls);
    }

    [Fact]
    public async Task RegionWithoutSchedules_TheProviderIsNotAsked()
    {
        var channel = AddChannel(c => c.GroupId = "45.1", "donetsk");

        var lines = await BuildAsync(channel);

        Assert.Empty(_schedules.Calls);
        Assert.Equal(["Даних із сайту ДТЕК ще немає."], lines);
    }

    [Fact]
    public async Task PartsAndDistrictsOfTheChannel_DoNotAffectTheStatus()
    {
        StoreState(PopupKind.Accident, "kyiv-oblast");
        var channel = AddChannel(c => c.PartIdList = ["bucha"], "kyiv-oblast");

        var lines = await BuildAsync(channel);

        Assert.Equal("⚠️ На сайті ДТЕК повідомлено про аварію в енергосистемі.", lines[0]);
    }
}
