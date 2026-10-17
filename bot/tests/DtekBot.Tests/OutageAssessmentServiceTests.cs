using System.Text.RegularExpressions;
using DtekBot.Api.Checks;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class OutageAssessmentServiceTests : IDisposable
{
    private const long Chat = 701;
    private const string RegionWide =
        "ℹ️ На сайті ДТЕК зараз діє загальне повідомлення про екстрені відключення чи аварію по регіону. Це ймовірна причина, тому перевірку за адресою не запускаю.";

    private static readonly Regex PowerOutPattern =
        new(@"світло\s+зникло", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly DispatcherFixture _f = new();
    private readonly TestClock _clock = new(Kyiv(15, 12));
    private readonly FakeWorkflowTrigger _trigger = new();
    private readonly FakeScheduleProvider _schedules = new();
    private readonly OutageAssessmentService _service;

    public OutageAssessmentServiceTests()
    {
        var checks = new AddressCheckService(
            _f.Db, _f.Bot, _trigger, _f.Options, _clock, NullLogger<AddressCheckService>.Instance);
        _service = new OutageAssessmentService(_f.Db, checks, _schedules, _f.Options, _clock);
        _schedules.Snapshots["kyiv"] = RealSnapshot();
    }

    public void Dispose() => _f.Dispose();

    // ---- helpers ----

    private Channel AddChannel(Action<Channel>? configure = null, string regionId = "kyiv") =>
        _f.AddChannel(Chat, c =>
        {
            c.GroupId = "45.1";
            c.AddressStreet = "вул. Драгоманова";
            c.AddressHouse = "12";
            c.AddressLabel = "вул. Драгоманова, 12";
            configure?.Invoke(c);
        }, regionId);

    private Task<string> AssessAsync(Channel channel) => _service.AssessAsync(channel, CancellationToken.None);

    private void StoreRegionState(PopupKind kind, string regionId = "kyiv")
    {
        _f.Db.RegionStates.Add(new RegionState
        {
            RegionId = regionId,
            Kind = kind,
            Hash = "h",
            ObservedAt = _clock.Now,
            UpdatedAt = _clock.Now,
        });
        _f.Db.SaveChanges();
    }

    private void StoreGroupState(string today, string groupId = "45.1")
    {
        _f.Db.GroupStates.Add(new GroupState
        {
            RegionId = "kyiv",
            GroupId = groupId,
            Today = today,
            Tomorrow = "ScheduleApplies",
            UpdatedAt = _clock.Now,
        });
        _f.Db.SaveChanges();
    }

    private int CheckRows() => _f.Db.AddressChecks.AsNoTracking().Count();

    private void StoreDistrictAlert(params string[] districts)
    {
        _f.Db.RegionStates.Add(new RegionState
        {
            RegionId = "kyiv",
            Kind = PopupKind.Emergency,
            Hash = "h",
            PartIdList = districts,
            ObservedAt = _clock.Now,
            UpdatedAt = _clock.Now,
        });
        _f.Db.SaveChanges();
    }

    // ---- popup naming districts ("в інших районах діють графіки") ----

    [Fact]
    public async Task DistrictAlert_ChatInsideTheNamedDistricts_GetsTheEmergencyAnswer_NotTheScheduleNotice()
    {
        StoreDistrictAlert("holosiivskyi", "obolonskyi");
        var channel = AddChannel(c => c.PartIdList = ["obolonskyi"]);

        var text = await AssessAsync(channel);

        Assert.Contains("У вашому районі (Оболонський район)", text);
        Assert.Contains("Графіки там не діють", text);
        Assert.Contains("Перевірку за адресою не запускаю", text);
        Assert.DoesNotContain("стабілізаційне", text);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
    }

    [Fact]
    public async Task DistrictAlert_ChatOutsideTheNamedDistricts_FollowsItsSchedule_WithoutAnyEmergencyText()
    {
        StoreDistrictAlert("holosiivskyi", "obolonskyi");
        var channel = AddChannel(c => c.PartIdList = ["darnytskyi"]);

        var text = await AssessAsync(channel);

        Assert.Equal(StabilizationAt12, text);
        Assert.Equal(0, CheckRows());
    }

    [Fact]
    public async Task DistrictAlert_ChatOutside_WithScheduleOn_StillRunsTheAddressCheck()
    {
        StoreDistrictAlert("holosiivskyi");
        _clock.Now = Kyiv(15, 15);
        var channel = AddChannel(c => c.PartIdList = ["darnytskyi"]);

        await AssessAsync(channel);

        Assert.Equal(1, CheckRows());
    }

    [Fact]
    public async Task DistrictAlert_ChatWithoutDistricts_GetsTheDistrictsAsContext_AndTheNormalAnswer()
    {
        StoreDistrictAlert("holosiivskyi", "obolonskyi");
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.StartsWith("ℹ️ На сайті ДТЕК діють екстрені відключення в районах: Голосіївський район, Оболонський район.", text);
        Assert.EndsWith(StabilizationAt12, text);
    }

    [Fact]
    public async Task DistrictAlert_WholeBankSubscription_MatchesViaItsDistricts()
    {
        StoreDistrictAlert("holosiivskyi", "obolonskyi", "pecherskyi", "podilskyi", "sviatoshynskyi", "solomianskyi", "shevchenkivskyi");
        var channel = AddChannel(c => c.PartIdList = ["right-bank"]);

        var text = await AssessAsync(channel);

        Assert.Contains("У вашому районі (Правий берег)", text);
        Assert.Equal(0, CheckRows());
    }

    private const string StabilizationAt12 =
        "🗓 За графіком (черга 45.1) зараз планове стабілізаційне відключення з 11:00.\n"
        + "Світло очікується о 14:00.\n"
        + "Перевірку за адресою на сайті ДТЕК не запускаю.\n"
        + "\n"
        + "Графік оновлено: 15.10.2026 20:24 (за даними ДТЕК)";

    private const string RegionWideWarning =
        "⚠️ Увага: на сайті ДТЕК зараз діє загальне повідомлення про екстрені відключення чи аварію по регіону, тому графік може не діяти.";

    // ---- region-wide popup ----

    [Theory]
    [InlineData(PopupKind.Emergency)]
    [InlineData(PopupKind.Accident)]
    [InlineData(PopupKind.Unknown)]
    public async Task RegionWideAlert_WithScheduleOn_RepliesWithContext_AndSkipsTheAddressCheck(PopupKind kind)
    {
        StoreRegionState(kind);
        _clock.Now = Kyiv(15, 15);
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.Equal(RegionWide, text);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
    }

    [Theory]
    [InlineData(PopupKind.Emergency)]
    [InlineData(PopupKind.Accident)]
    [InlineData(PopupKind.Unknown)]
    public async Task RegionWideAlert_WithScheduledOutage_AppendsAWarningToTheNotice(PopupKind kind)
    {
        StoreRegionState(kind);
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.Equal(StabilizationAt12 + "\n\n" + RegionWideWarning, text);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
    }

    [Fact]
    public async Task RegionWideAlert_WithScheduledOutageAndGroupEmergency_ShowsYasnoLine_Notice_ThenWarning()
    {
        StoreRegionState(PopupKind.Emergency);
        StoreGroupState(GroupStatuses.Emergency);
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.Equal(
            "🚨 Для вашої черги 45.1 зараз діють екстрені відключення (за даними Yasno).\n\n"
            + StabilizationAt12
            + "\n\n"
            + RegionWideWarning,
            text);
    }

    [Fact]
    public async Task RegionWideAlert_WithScheduleOn_IgnoresTheGroupEmergencyLine()
    {
        StoreRegionState(PopupKind.Emergency);
        StoreGroupState(GroupStatuses.Emergency);
        _clock.Now = Kyiv(15, 15);
        var channel = AddChannel();

        Assert.Equal(RegionWide, await AssessAsync(channel));
    }

    [Theory]
    [InlineData("nogroup")]
    [InlineData("noprovider")]
    public async Task RegionWideAlert_WithoutScheduleInformation_RepliesWithContext(string reason)
    {
        StoreRegionState(PopupKind.Accident);
        var channel = AddChannel(c =>
        {
            if (reason == "nogroup")
            {
                c.GroupId = null;
            }
        });
        if (reason == "noprovider")
        {
            _schedules.Snapshots["kyiv"] = null;
        }

        Assert.Equal(RegionWide, await AssessAsync(channel));
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
    }

    [Fact]
    public async Task RegionWideAlert_OfAnotherRegion_IsIgnored()
    {
        StoreRegionState(PopupKind.Emergency, "odesa");
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.Equal(StabilizationAt12, text);
    }

    [Theory]
    [InlineData(PopupKind.None)]
    [InlineData(PopupKind.Stabilization)]
    [InlineData(PopupKind.Cancelled)]
    public async Task QuietRegionStates_FallThrough(PopupKind kind)
    {
        StoreRegionState(kind);
        _clock.Now = Kyiv(15, 15);
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.StartsWith("⏳", text);
        Assert.Equal(1, CheckRows());
    }

    // ---- step 2: Yasno group emergency ----

    [Fact]
    public async Task GroupEmergency_WithScheduleOn_PrependsYasnoLine_ThenQueuedCheck()
    {
        _clock.Now = Kyiv(15, 15);
        StoreGroupState(GroupStatuses.Emergency);
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        var parts = text.Split("\n\n");
        Assert.Equal(2, parts.Length);
        Assert.Equal("🚨 Для вашої черги 45.1 зараз діють екстрені відключення (за даними Yasno).", parts[0]);
        Assert.StartsWith("⏳", parts[1]);
        Assert.Equal(1, CheckRows());
        Assert.Single(_trigger.Events);
    }

    [Fact]
    public async Task GroupEmergency_WithScheduledOutage_PrependsYasnoLine_BeforeTheNotice()
    {
        StoreGroupState(GroupStatuses.Emergency);
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.Equal(
            "🚨 Для вашої черги 45.1 зараз діють екстрені відключення (за даними Yasno).\n\n" + StabilizationAt12,
            text);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
    }

    [Theory]
    [InlineData("ScheduleApplies", "45.1")]
    [InlineData("WaitingForSchedule", "45.1")]
    [InlineData(GroupStatuses.Emergency, "46.1")]
    public async Task GroupState_NotEmergencyForThisGroup_AddsNoYasnoLine(string today, string groupId)
    {
        StoreGroupState(today, groupId);
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.Equal(StabilizationAt12, text);
    }

    // ---- step 3: schedule says the power is off ----

    [Fact]
    public async Task ScheduledOutage_RepliesWithStabilizationNotice_WithoutTouchingTheDtekSite()
    {
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.Equal(StabilizationAt12, text);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
        Assert.Empty(_f.Bot.Requests);
        Assert.Equal(["kyiv"], _schedules.Calls);
    }

    [Fact]
    public async Task ScheduledOutage_EveningOutage_ShowsNextDayReturn()
    {
        _clock.Now = Kyiv(15, 21);
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.Equal(
            "🗓 За графіком (черга 45.1) зараз планове стабілізаційне відключення з 20:30.\n"
            + "Світло очікується о 00:00 (16.10).\n"
            + "Перевірку за адресою на сайті ДТЕК не запускаю.\n"
            + "\n"
            + "Графік оновлено: 15.10.2026 20:24 (за даними ДТЕК)",
            text);
    }

    [Theory]
    [InlineData(11, 0)]
    [InlineData(13, 59)]
    public async Task ScheduledOutage_AtTheEdgesOfTheOutage_StillOff(int hour, int minute)
    {
        _clock.Now = Kyiv(15, hour, minute);
        var channel = AddChannel();

        Assert.StartsWith("🗓 За графіком", await AssessAsync(channel));
        Assert.Equal(0, CheckRows());
    }

    [Fact]
    public async Task ScheduledOutage_WithoutSourceUpdateText_HasNoUpdatedLine()
    {
        _schedules.Snapshots["kyiv"] = RealSnapshot() with { SourceUpdatedText = null };
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.DoesNotContain("Графік оновлено", text);
        Assert.StartsWith("🗓 За графіком", text);
    }

    [Fact]
    public async Task ScheduledOutage_ForGroupOtherThanTheDefault_UsesTheChannelsGroup()
    {
        // Group 1.1 and 45.1 differ; build a synthetic snapshot where only 2.1 is off at noon.
        var json = Json("v", "01.01.2026 10:00",
            (Day1, "45.1", []),
            (Day1, "2.1", Hours("no", 12, 13)));
        _schedules.Snapshots["kyiv"] = ScheduleParserParse(json);

        var onGroup = AddChannel();
        Assert.StartsWith("⏳", await AssessAsync(onGroup));

        onGroup.GroupId = "2.1";
        var offGroup = await AssessAsync(onGroup);

        Assert.Contains("(черга 2.1)", offGroup);
        Assert.Contains("з 11:00.", offGroup);
    }

    private static DtekBot.Api.Schedules.ScheduleSnapshot ScheduleParserParse(string json) =>
        DtekBot.Api.Schedules.ScheduleParser.Parse(json, "kyiv", Kyiv(15, 12))!;

    // ---- step 4: fall through to the address check ----

    [Fact]
    public async Task ScheduleOn_FallsThroughToAddressCheck()
    {
        _clock.Now = Kyiv(15, 15);
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.StartsWith("⏳", text);
        var check = Assert.Single(_f.Db.AddressChecks.AsNoTracking());
        Assert.Equal(AddressCheckStatus.Pending, check.Status);
        Assert.Equal(Chat, check.ChatId);
        Assert.Equal(["check-addresses"], _trigger.Events);
    }

    [Theory]
    [InlineData(10, 59, 59)]
    [InlineData(14, 0, 0)]
    public async Task ScheduleJustBeforeOrAfterTheOutage_FallsThrough(int hour, int minute, int second)
    {
        _clock.Now = Kyiv(15, hour, minute, second);
        var channel = AddChannel();

        Assert.StartsWith("⏳", await AssessAsync(channel));
        Assert.Equal(1, CheckRows());
    }

    [Fact]
    public async Task ProviderReturnsNull_FallsThroughToAddressCheck()
    {
        _schedules.Snapshots["kyiv"] = null;
        var channel = AddChannel();

        var text = await AssessAsync(channel);

        Assert.StartsWith("⏳", text);
        Assert.Equal(1, CheckRows());
        Assert.Equal(["kyiv"], _schedules.Calls);
    }

    [Fact]
    public async Task ChannelWithoutGroupId_FallsThrough_WithoutAskingTheProvider()
    {
        var channel = AddChannel(c => c.GroupId = null);

        var text = await AssessAsync(channel);

        Assert.StartsWith("⏳", text);
        Assert.Empty(_schedules.Calls);
        Assert.Equal(1, CheckRows());
    }

    [Fact]
    public async Task RegionWithoutSchedules_FallsThrough_WithoutAskingTheProvider()
    {
        var channel = AddChannel(c => c.RegionId = "donetsk", regionId: "donetsk");

        var text = await AssessAsync(channel);

        Assert.Contains("поки недоступна", text);
        Assert.Empty(_schedules.Calls);
        Assert.Equal(0, CheckRows());
    }

    [Fact]
    public async Task RegionWithSchedulesButNoAddressApi_StillAsksTheScheduleProvider()
    {
        _schedules.Snapshots["odesa"] = null;
        var channel = AddChannel(regionId: "odesa");

        var text = await AssessAsync(channel);

        Assert.Equal(["odesa"], _schedules.Calls);
        Assert.Contains("поки недоступна", text);
    }

    [Fact]
    public async Task OutsideTheCoverageOfTheData_FallsThrough()
    {
        _clock.Now = Kyiv(20, 12);
        var channel = AddChannel();

        Assert.StartsWith("⏳", await AssessAsync(channel));
    }

    [Fact]
    public async Task GroupMissingFromTheSnapshot_FallsThrough()
    {
        _clock.Now = Kyiv(15, 12);
        var channel = AddChannel(c => c.GroupId = "99.9");

        Assert.StartsWith("⏳", await AssessAsync(channel));
    }

    [Fact]
    public async Task FallThrough_WithoutAddress_ReturnsTheNoAddressText()
    {
        _clock.Now = Kyiv(15, 15);
        var channel = AddChannel(c => { c.AddressStreet = null; c.AddressHouse = null; });

        var text = await AssessAsync(channel);

        Assert.Contains("Спершу оберіть адресу", text);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
    }

    [Fact]
    public async Task ScheduledOutage_DoesNotRequireAnAddress()
    {
        var channel = AddChannel(c => { c.AddressStreet = null; c.AddressHouse = null; });

        var text = await AssessAsync(channel);

        Assert.StartsWith("🗓 За графіком", text);
    }

    [Fact]
    public async Task FallThrough_SecondRequest_ReportsPendingCheck()
    {
        _clock.Now = Kyiv(15, 15);
        var channel = AddChannel();
        await AssessAsync(channel);

        var text = await AssessAsync(channel);

        Assert.Contains("ще виконують", text);
        Assert.Equal(1, CheckRows());
        Assert.Single(_trigger.Events);
    }

    // ---- replies never re-trigger the channel reaction ----

    [Fact]
    public async Task Replies_NeverMatchThePowerOutPattern()
    {
        var texts = new List<string>();

        StoreRegionState(PopupKind.Emergency);
        texts.Add(await AssessAsync(AddChannel()));
        _f.Db.RegionStates.RemoveRange(_f.Db.RegionStates);
        _f.Db.SaveChanges();

        texts.Add(await AssessAsync(_f.Db.Channels.Single()));
        StoreGroupState(GroupStatuses.Emergency);
        texts.Add(await AssessAsync(_f.Db.Channels.Single()));
        _clock.Now = Kyiv(15, 15);
        texts.Add(await AssessAsync(_f.Db.Channels.Single()));

        Assert.All(texts, t => Assert.DoesNotMatch(PowerOutPattern, t));
    }
}
