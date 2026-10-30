using DtekBot.Api.Checks;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.Enums;

namespace DtekBot.Tests;

public sealed class AddressCheckServiceTests : IDisposable
{
    private const long ChatA = 301;
    private const long ChatB = 302;
    private const string Street = "вул. Драгоманова";
    private const string House = "12";

    private static readonly DateTimeOffset Start = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly DispatcherFixture _f = new();
    private readonly TestClock _clock = new(Start);
    private readonly FakeWorkflowTrigger _trigger = new();
    private readonly AddressCheckService _service;

    public AddressCheckServiceTests()
    {
        _service = new AddressCheckService(
            _f.Db, _f.Bot, _trigger, _f.Options, _clock, NullLogger<AddressCheckService>.Instance, _f.CreateMirror(time: _clock));
    }

    public void Dispose() => _f.Dispose();

    // ---- helpers ----

    private Channel AddChannel(long chatId, Action<Channel>? configure = null, string regionId = "kyiv") =>
        _f.AddChannel(chatId, c =>
        {
            c.AddressStreet = Street;
            c.AddressHouse = House;
            c.AddressLabel = "вул. Драгоманова, 12";
            configure?.Invoke(c);
        }, regionId);

    private AddressCheck AddCheck(
        long chatId,
        AddressCheckStatus status,
        TimeSpan age,
        string street = Street,
        string house = House,
        string regionId = "kyiv",
        string city = "")
    {
        var check = new AddressCheck
        {
            ChatId = chatId,
            RegionId = regionId,
            City = city,
            Street = street,
            House = house,
            Label = $"{street}, {house}",
            Status = status,
            RequestedAt = _clock.Now - age,
        };
        _f.Db.AddressChecks.Add(check);
        _f.Db.SaveChanges();
        return check;
    }

    private AddressResult AddResult(
        TimeSpan age,
        Action<AddressResult>? configure = null,
        string street = Street,
        string house = House,
        string city = "")
    {
        var result = new AddressResult
        {
            RegionId = "kyiv",
            City = city,
            Street = street,
            House = house,
            Found = true,
            SubType = "Аварійне відключення",
            StartDate = "15.07 11:00",
            EndDate = "15.07 15:00",
            Type = "Аварійне",
            CheckedAt = _clock.Now - age,
        };
        configure?.Invoke(result);
        _f.Db.AddressResults.Add(result);
        _f.Db.SaveChanges();
        return result;
    }

    private List<AddressCheck> Checks(long? chatId = null) =>
        _f.Db.AddressChecks.AsNoTracking().Where(c => chatId == null || c.ChatId == chatId).OrderBy(c => c.Id).ToList();

    private Channel Load(long chatId) => _f.LoadChannel(chatId);

    // ---- RequestAsync: validation ----

    [Theory]
    [InlineData(null, "12")]
    [InlineData("", "12")]
    [InlineData(Street, null)]
    [InlineData(Street, "")]
    [InlineData(null, null)]
    public async Task Request_WithoutStreetOrHouse_IsNoAddress(string? street, string? house)
    {
        var channel = AddChannel(ChatA, c => { c.AddressStreet = street; c.AddressHouse = house; });

        var result = await _service.RequestAsync(channel, CancellationToken.None);

        Assert.Equal(RequestOutcome.NoAddress, result.Outcome);
        Assert.Empty(Checks());
        Assert.Empty(_trigger.Events);
    }

    [Fact]
    public async Task Request_ForRegionWithoutGroups_RequiresCity()
    {
        var channel = AddChannel(ChatA, regionId: "odesa");

        var result = await _service.RequestAsync(channel, CancellationToken.None);

        Assert.Equal(RequestOutcome.NoAddress, result.Outcome);
        Assert.Contains("населений пункт", result.Message);
        Assert.Empty(Checks());
        Assert.Empty(_trigger.Events);
    }

    [Fact]
    public async Task Request_ForRegionWithoutGroups_QueuesCityAndAddress()
    {
        var channel = AddChannel(ChatA, c => c.AddressCity = "м. Одеса", "odesa");

        var result = await _service.RequestAsync(channel, CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
        Assert.Equal("м. Одеса", Assert.Single(Checks()).City);
        Assert.Single(_trigger.Events);
    }

    [Fact]
    public async Task Request_CacheDoesNotCrossCities()
    {
        AddResult(TimeSpan.FromMinutes(1), city: "м. Буча");
        var channel = AddChannel(ChatA, c => c.AddressCity = "м. Ірпінь");

        var result = await _service.RequestAsync(channel, CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
        Assert.Equal("м. Ірпінь", Assert.Single(Checks()).City);
    }

    [Fact]
    public async Task Request_ForUnknownRegion_IsNoAddress()
    {
        var channel = AddChannel(ChatA, regionId: "atlantis");

        var result = await _service.RequestAsync(channel, CancellationToken.None);

        Assert.Equal(RequestOutcome.NoAddress, result.Outcome);
        Assert.Empty(Checks());
    }

    // ---- RequestAsync: queueing and dispatch ----

    [Fact]
    public async Task Request_Fresh_CreatesPendingCheck_AndTriggersWorkflowOnce()
    {
        var channel = AddChannel(ChatA);

        var result = await _service.RequestAsync(channel, CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
        Assert.Contains("⏳", result.Message);

        var check = Assert.Single(Checks());
        Assert.Equal(ChatA, check.ChatId);
        Assert.Equal("kyiv", check.RegionId);
        Assert.Equal(Street, check.Street);
        Assert.Equal(House, check.House);
        Assert.Equal("вул. Драгоманова, 12", check.Label);
        Assert.Equal(AddressCheckStatus.Pending, check.Status);
        Assert.Equal(Start, check.RequestedAt);
        Assert.Null(check.CompletedAt);

        Assert.Equal(["check-addresses"], _trigger.Events);
        Assert.Equal(AddressCheckService.WorkflowEvent, "check-addresses");
        var stored = Assert.Single(_f.Db.KeyValues.AsNoTracking());
        Assert.Equal("last-dispatch", stored.Key);
        Assert.Equal(Start, DateTimeOffset.Parse(stored.Value));
    }

    [Fact]
    public async Task Request_WithoutLabel_FallsBackToStreetAndHouse()
    {
        var channel = AddChannel(ChatA, c => c.AddressLabel = null);

        await _service.RequestAsync(channel, CancellationToken.None);

        Assert.Equal($"{Street}, {House}", Assert.Single(Checks()).Label);
    }

    [Fact]
    public async Task Request_SecondChatWithinDispatchInterval_IsQueuedButDoesNotTriggerAgain()
    {
        await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(1));

        var result = await _service.RequestAsync(AddChannel(ChatB), CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
        Assert.Equal(2, Checks().Count);
        Assert.Single(_trigger.Events);
        Assert.Equal(Start, DateTimeOffset.Parse(Assert.Single(_f.Db.KeyValues.AsNoTracking()).Value));
    }

    [Theory]
    [InlineData(179, 1)]
    [InlineData(180, 2)]
    [InlineData(600, 2)]
    public async Task Request_TriggersAgain_OnlyOnceDispatchIntervalHasElapsed(int seconds, int expectedTriggers)
    {
        await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);
        _clock.Advance(TimeSpan.FromSeconds(seconds));

        await _service.RequestAsync(AddChannel(ChatB), CancellationToken.None);

        Assert.Equal(expectedTriggers, _trigger.Events.Count);
        var stored = DateTimeOffset.Parse(Assert.Single(_f.Db.KeyValues.AsNoTracking()).Value);
        Assert.Equal(expectedTriggers == 2 ? Start.AddSeconds(seconds) : Start, stored);
    }

    [Fact]
    public async Task Request_WhenTriggerFails_IsStillQueued_AndStoresNothing()
    {
        _trigger.Result = false;

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
        Assert.Single(_trigger.Events);
        Assert.Equal(AddressCheckStatus.Pending, Assert.Single(Checks()).Status);
        Assert.Empty(_f.Db.KeyValues.AsNoTracking());
    }

    [Fact]
    public async Task Request_AfterFailedTrigger_NextRequestTriggersImmediately()
    {
        _trigger.Result = false;
        await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);
        _trigger.Result = true;

        await _service.RequestAsync(AddChannel(ChatB), CancellationToken.None);

        Assert.Equal(2, _trigger.Events.Count);
        Assert.Single(_f.Db.KeyValues.AsNoTracking());
    }

    // ---- RequestAsync: limits ----

    [Fact]
    public async Task Request_WithPendingCheck_IsAlreadyPending()
    {
        AddCheck(ChatA, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.AlreadyPending, result.Outcome);
        Assert.Single(Checks());
        Assert.Empty(_trigger.Events);
    }

    [Fact]
    public async Task Request_PendingCheckJustInsideTimeout_IsStillPending()
    {
        AddCheck(ChatA, AddressCheckStatus.Pending, TimeSpan.FromMinutes(15));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.AlreadyPending, result.Outcome);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task Request_WithinChatCooldown_ReportsRemainingMinutes()
    {
        AddCheck(ChatA, AddressCheckStatus.Done, TimeSpan.FromMinutes(3));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Cooldown, result.Outcome);
        Assert.Contains("7 хв", result.Message);
        Assert.Single(Checks());
        Assert.Empty(_trigger.Events);
    }

    [Fact]
    public async Task Request_CooldownRoundsRemainingTimeUp()
    {
        AddCheck(ChatA, AddressCheckStatus.Done, TimeSpan.FromMinutes(9) + TimeSpan.FromSeconds(30));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Cooldown, result.Outcome);
        Assert.Contains("1 хв", result.Message);
    }

    [Fact]
    public async Task Request_AfterCooldown_IsQueued()
    {
        AddCheck(ChatA, AddressCheckStatus.Done, TimeSpan.FromMinutes(10));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
    }

    [Fact]
    public async Task Request_CooldownAlsoAppliesToExpiredChecks()
    {
        AddCheck(ChatA, AddressCheckStatus.Expired, TimeSpan.FromMinutes(5));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Cooldown, result.Outcome);
    }

    [Fact]
    public async Task Request_CooldownIsPerChat()
    {
        AddCheck(ChatA, AddressCheckStatus.Done, TimeSpan.FromMinutes(1));

        var result = await _service.RequestAsync(AddChannel(ChatB), CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
    }

    [Fact]
    public async Task Request_AfterTwentyChecksInADay_IsDailyLimit()
    {
        for (var i = 0; i < 20; i++)
        {
            AddCheck(ChatA, AddressCheckStatus.Done, TimeSpan.FromMinutes(15 + 60 * i));
        }

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.DailyLimit, result.Outcome);
        Assert.Equal(20, Checks().Count);
        Assert.Empty(_trigger.Events);
    }

    [Fact]
    public async Task Request_NineteenChecksInADay_IsStillAllowed()
    {
        for (var i = 0; i < 19; i++)
        {
            AddCheck(ChatA, AddressCheckStatus.Done, TimeSpan.FromMinutes(15 + 60 * i));
        }

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
    }

    [Fact]
    public async Task Request_ChecksOlderThanADay_DoNotCountTowardsDailyLimit()
    {
        for (var i = 0; i < 20; i++)
        {
            AddCheck(ChatA, AddressCheckStatus.Done, TimeSpan.FromHours(25) + TimeSpan.FromMinutes(i));
        }

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
    }

    // ---- RequestAsync: cache ----

    [Fact]
    public async Task Request_WithFreshResult_IsCached_WithoutNewCheckOrTrigger()
    {
        AddResult(TimeSpan.FromMinutes(4));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Cached, result.Outcome);
        Assert.Contains("🚨", result.Message);
        Assert.Contains("вул. Драгоманова, 12", result.Message);
        Assert.Contains("Перевірено:", result.Message);
        Assert.Empty(Checks());
        Assert.Empty(_trigger.Events);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task Request_ResultExactlyAtCacheLimit_IsStillCached()
    {
        AddResult(TimeSpan.FromMinutes(5));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Cached, result.Outcome);
    }

    [Fact]
    public async Task Request_WithStaleResult_IgnoresIt()
    {
        AddResult(TimeSpan.FromMinutes(6));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
        Assert.Single(Checks());
        Assert.Single(_trigger.Events);
    }

    [Fact]
    public async Task Request_FreshResultForAnotherHouse_IsNotUsed()
    {
        AddResult(TimeSpan.FromMinutes(1), house: "14");

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
    }

    [Fact]
    public async Task Request_CachedResult_IsServedEvenDuringChatCooldown()
    {
        AddCheck(ChatA, AddressCheckStatus.Done, TimeSpan.FromMinutes(2));
        AddResult(TimeSpan.FromMinutes(1));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Cached, result.Outcome);
    }

    // ---- RequestAsync: expiry ----

    [Fact]
    public async Task Request_ExpiresStalePendingChecks_AndTellsTheirChats()
    {
        var stale = AddCheck(ChatB, AddressCheckStatus.Pending, TimeSpan.FromMinutes(16), house: "99");

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
        var expired = Checks(ChatB).Single(c => c.Id == stale.Id);
        Assert.Equal(AddressCheckStatus.Expired, expired.Status);
        Assert.Equal(Start, expired.CompletedAt);

        var message = Assert.Single(_f.Bot.SentTo(ChatB));
        Assert.Contains("Не вдалося перевірити", message.Text);
        Assert.Single(_f.Bot.Sent);
    }

    [Fact]
    public async Task Request_OwnStalePendingCheck_IsExpired_ThenANewOneIsQueued()
    {
        AddCheck(ChatA, AddressCheckStatus.Pending, TimeSpan.FromMinutes(16));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
        var checks = Checks(ChatA);
        Assert.Equal([AddressCheckStatus.Expired, AddressCheckStatus.Pending], checks.Select(c => c.Status));
        Assert.Contains("Не вдалося перевірити", Assert.Single(_f.Bot.SentTo(ChatA)).Text);
    }

    [Fact]
    public async Task Expiry_ReplyFailure_DoesNotThrow_AndStillExpires()
    {
        AddCheck(ChatB, AddressCheckStatus.Pending, TimeSpan.FromMinutes(20), house: "99");
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Queued, result.Outcome);
        Assert.Equal(AddressCheckStatus.Expired, Assert.Single(Checks(ChatB)).Status);
    }

    // ---- GetPendingAsync ----

    [Fact]
    public async Task GetPending_GroupsByRegionAndStreet_WithDistinctHouses_OldestFirst()
    {
        AddCheck(301, AddressCheckStatus.Pending, TimeSpan.FromMinutes(10), "вул. Б", "2");
        AddCheck(302, AddressCheckStatus.Pending, TimeSpan.FromMinutes(9), "вул. А", "1");
        AddCheck(303, AddressCheckStatus.Pending, TimeSpan.FromMinutes(8), "вул. Б", "2");
        AddCheck(304, AddressCheckStatus.Pending, TimeSpan.FromMinutes(7), "вул. Б", "3");
        AddCheck(305, AddressCheckStatus.Pending, TimeSpan.FromMinutes(6), "вул. А", "1", "odesa");

        var pending = await _service.GetPendingAsync(CancellationToken.None);

        Assert.Equal(3, pending.Count);
        Assert.Equal(("kyiv", "вул. Б"), (pending[0].RegionId, pending[0].Street));
        Assert.Equal(["2", "3"], pending[0].Houses);
        Assert.Equal(("kyiv", "вул. А"), (pending[1].RegionId, pending[1].Street));
        Assert.Equal(["1"], pending[1].Houses);
        Assert.Equal(("odesa", "вул. А"), (pending[2].RegionId, pending[2].Street));
    }

    [Fact]
    public async Task GetPending_KeepsSameStreetInDifferentCitiesSeparate()
    {
        AddCheck(ChatA, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2), city: "м. Буча");
        AddCheck(ChatB, AddressCheckStatus.Pending, TimeSpan.FromMinutes(1), city: "м. Ірпінь");

        var pending = await _service.GetPendingAsync(CancellationToken.None);

        Assert.Equal(["м. Буча", "м. Ірпінь"], pending.Select(p => p.City));
    }

    [Fact]
    public async Task GetPending_IgnoresDoneAndExpiredChecks()
    {
        AddCheck(301, AddressCheckStatus.Done, TimeSpan.FromMinutes(5), "вул. Done");
        AddCheck(302, AddressCheckStatus.Expired, TimeSpan.FromMinutes(5), "вул. Expired");
        AddCheck(303, AddressCheckStatus.Pending, TimeSpan.FromMinutes(5), "вул. Pending");

        var pending = await _service.GetPendingAsync(CancellationToken.None);

        Assert.Equal("вул. Pending", Assert.Single(pending).Street);
    }

    [Fact]
    public async Task GetPending_IsCappedToMaxStreetsPerRun_KeepingTheOldest()
    {
        for (var i = 0; i < 25; i++)
        {
            AddCheck(1000 + i, AddressCheckStatus.Pending, TimeSpan.FromMinutes(14) - TimeSpan.FromSeconds(i), $"вул. {i:00}");
        }

        var pending = await _service.GetPendingAsync(CancellationToken.None);

        Assert.Equal(20, pending.Count);
        Assert.Equal(Enumerable.Range(0, 20).Select(i => $"вул. {i:00}"), pending.Select(p => p.Street));
    }

    [Fact]
    public async Task GetPending_WhenNothingIsPending_IsEmpty()
    {
        Assert.Empty(await _service.GetPendingAsync(CancellationToken.None));
    }

    [Fact]
    public async Task GetPending_ExpiresStaleChecks_AndDoesNotReturnThem()
    {
        AddCheck(ChatA, AddressCheckStatus.Pending, TimeSpan.FromMinutes(30));
        AddCheck(ChatB, AddressCheckStatus.Pending, TimeSpan.FromMinutes(1), "вул. Нова");

        var pending = await _service.GetPendingAsync(CancellationToken.None);

        Assert.Equal("вул. Нова", Assert.Single(pending).Street);
        Assert.Equal(AddressCheckStatus.Expired, Assert.Single(Checks(ChatA)).Status);
        Assert.Single(_f.Bot.SentTo(ChatA));
    }

    // ---- ProcessResultsAsync ----

    private static AddressResultsRequest Results(
        string street = Street,
        string regionId = "kyiv",
        string city = "",
        params (string House, HouseResultEntry Entry)[] houses) =>
        new([new StreetResult(regionId, city, street, houses.ToDictionary(h => h.House, h => h.Entry))]);

    private static HouseResultEntry Entry(bool found = true, string? subType = null, string? start = null, string? end = null, string? type = null) =>
        new(found, subType, start, end, type);

    [Fact]
    public async Task Process_CreatesAndUpdatesAddressResults()
    {
        var first = await _service.ProcessResultsAsync(
            Results(houses: [("12", Entry(subType: "Аварійне", start: "a", end: "b", type: "t"))]), CancellationToken.None);

        Assert.Equal(0, first);
        var row = Assert.Single(_f.Db.AddressResults.AsNoTracking());
        Assert.Equal(("kyiv", "", Street, "12"), (row.RegionId, row.City, row.Street, row.House));
        Assert.True(row.Found);
        Assert.Equal(("Аварійне", "a", "b", "t"), (row.SubType, row.StartDate, row.EndDate, row.Type));
        Assert.Equal(Start, row.CheckedAt);

        _clock.Advance(TimeSpan.FromMinutes(7));
        await _service.ProcessResultsAsync(Results(houses: [("12", Entry(found: false))]), CancellationToken.None);

        row = Assert.Single(_f.Db.AddressResults.AsNoTracking());
        Assert.False(row.Found);
        Assert.Equal("", row.SubType);
        Assert.Equal("", row.StartDate);
        Assert.Equal(Start.AddMinutes(7), row.CheckedAt);
    }

    [Fact]
    public async Task Process_StoresSameHouseInDifferentCitiesSeparately()
    {
        await _service.ProcessResultsAsync(
            new AddressResultsRequest([
                new StreetResult("odesa", "м. Буча", Street, new() { ["12"] = Entry(subType: "аварійне") }),
                new StreetResult("odesa", "м. Ірпінь", Street, new() { ["12"] = Entry(subType: "планове") }),
            ]),
            CancellationToken.None);

        Assert.Equal(2, _f.Db.AddressResults.AsNoTracking().Count());
    }

    [Fact]
    public async Task Process_SendsOneMessagePerPendingCheck_AndMarksItDone()
    {
        AddCheck(ChatA, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2));

        var answered = await _service.ProcessResultsAsync(
            Results(houses: [("12", Entry(subType: "Аварійне відключення", start: "s", end: "e"))]), CancellationToken.None);

        Assert.Equal(1, answered);
        var message = Assert.Single(_f.Bot.Sent);
        Assert.Equal(ChatA, message.ChatId.Identifier);
        Assert.Contains("🚨", message.Text);
        Assert.Contains($"{Street}, {House}", message.Text);
        Assert.Equal(default, message.ParseMode);

        var check = Assert.Single(Checks());
        Assert.Equal(AddressCheckStatus.Done, check.Status);
        Assert.Equal(Start, check.CompletedAt);
    }

    [Fact]
    public async Task Process_PausedAtCompletion_SuppressesQueuedReplyButMarksDone()
    {
        const long channelId = -303;
        AddChannel(channelId, c => c.IsPaused = true);
        AddCheck(channelId, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2));

        var answered = await _service.ProcessResultsAsync(
            Results(houses: [("12", Entry())]), CancellationToken.None);

        Assert.Equal(1, answered);
        Assert.Empty(_f.Bot.SentTo(channelId));
        Assert.Equal(AddressCheckStatus.Done, Assert.Single(Checks(channelId)).Status);
    }

    [Fact]
    public async Task Process_QuietHoursSilenceQueuedReplyAndPrivateCopy()
    {
        const long channelId = -304;
        const long admin = 9002;
        AddChannel(channelId, c =>
        {
            c.QuietHoursEnabled = true;
            c.QuietHoursStartMinute = 14 * 60;
            c.QuietHoursEndMinute = 16 * 60;
        });
        _f.Db.ChannelAdmins.Add(new ChannelAdmin { ChatId = channelId, UserId = admin });
        _f.Db.SaveChanges();
        _f.Bot.SetStatus(admin, ChatMemberStatus.Administrator);
        AddCheck(channelId, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2));

        await _service.ProcessResultsAsync(Results(houses: [("12", Entry())]), CancellationToken.None);

        Assert.True(Assert.Single(_f.Bot.SentTo(channelId)).DisableNotification);
        Assert.True(Assert.Single(_f.Bot.Requests.OfType<CopyMessageRequest>()).DisableNotification);
    }

    [Fact]
    public async Task Process_ChecksForHousesNotInResults_StayPending()
    {
        AddCheck(ChatA, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2), house: "12");
        AddCheck(ChatB, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2), house: "14");

        var answered = await _service.ProcessResultsAsync(Results(houses: [("12", Entry())]), CancellationToken.None);

        Assert.Equal(1, answered);
        Assert.Single(_f.Bot.Sent);
        Assert.Equal(AddressCheckStatus.Done, Assert.Single(Checks(ChatA)).Status);
        var other = Assert.Single(Checks(ChatB));
        Assert.Equal(AddressCheckStatus.Pending, other.Status);
        Assert.Null(other.CompletedAt);
    }

    [Fact]
    public async Task Process_TwoChatsOnTheSameAddress_BothGetReplies()
    {
        AddCheck(ChatA, AddressCheckStatus.Pending, TimeSpan.FromMinutes(3));
        AddCheck(ChatB, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2));

        var answered = await _service.ProcessResultsAsync(Results(houses: [("12", Entry())]), CancellationToken.None);

        Assert.Equal(2, answered);
        Assert.Single(_f.Bot.SentTo(ChatA));
        Assert.Single(_f.Bot.SentTo(ChatB));
        Assert.All(Checks(), c => Assert.Equal(AddressCheckStatus.Done, c.Status));
    }

    [Fact]
    public async Task Process_DoesNotAnswerChecksOfOtherStreetsRegionsOrStatuses()
    {
        AddCheck(301, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2), "вул. Інша");
        AddCheck(302, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2), regionId: "odesa");
        AddCheck(303, AddressCheckStatus.Done, TimeSpan.FromMinutes(2));
        AddCheck(304, AddressCheckStatus.Expired, TimeSpan.FromMinutes(2));

        var answered = await _service.ProcessResultsAsync(Results(houses: [("12", Entry())]), CancellationToken.None);

        Assert.Equal(0, answered);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task Process_TelegramForbidden_DoesNotThrow_AndStillMarksDone()
    {
        AddCheck(ChatA, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2));
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var answered = await _service.ProcessResultsAsync(Results(houses: [("12", Entry())]), CancellationToken.None);

        Assert.Equal(1, answered);
        Assert.Equal(AddressCheckStatus.Done, Assert.Single(Checks()).Status);
    }

    [Fact]
    public async Task Process_FailureForOneChat_DoesNotStopTheOthers()
    {
        AddCheck(ChatA, AddressCheckStatus.Pending, TimeSpan.FromMinutes(3));
        AddCheck(ChatB, AddressCheckStatus.Pending, TimeSpan.FromMinutes(2));
        _f.Bot.Failure = r => r is SendMessageRequest { ChatId.Identifier: ChatA } ? FakeTelegramBotClient.Forbidden() : null;

        var answered = await _service.ProcessResultsAsync(Results(houses: [("12", Entry())]), CancellationToken.None);

        Assert.Equal(2, answered);
        Assert.Contains(_f.Bot.Sent, r => r.ChatId.Identifier == ChatB);
        Assert.All(Checks(), c => Assert.Equal(AddressCheckStatus.Done, c.Status));
    }

    [Fact]
    public async Task Process_ClipsValuesToColumnLengths_AndTrims()
    {
        await _service.ProcessResultsAsync(
            Results(houses:
            [
                ("12", Entry(
                    subType: "  " + new string('s', 600),
                    start: new string('a', 100),
                    end: new string('b', 100),
                    type: new string('t', 300))),
            ]),
            CancellationToken.None);

        var row = Assert.Single(_f.Db.AddressResults.AsNoTracking());
        Assert.Equal(new string('s', 512), row.SubType);
        Assert.Equal(new string('a', 64), row.StartDate);
        Assert.Equal(new string('b', 64), row.EndDate);
        Assert.Equal(new string('t', 256), row.Type);
    }

    [Fact]
    public async Task Process_NullFields_BecomeEmptyStrings()
    {
        await _service.ProcessResultsAsync(Results(houses: [("12", Entry(found: true))]), CancellationToken.None);

        var row = Assert.Single(_f.Db.AddressResults.AsNoTracking());
        Assert.Equal(("", "", "", ""), (row.SubType, row.StartDate, row.EndDate, row.Type));
    }

    [Fact]
    public async Task Process_NullOrEmptyResults_AreHarmless()
    {
        Assert.Equal(0, await _service.ProcessResultsAsync(new AddressResultsRequest(null), CancellationToken.None));
        Assert.Equal(0, await _service.ProcessResultsAsync(new AddressResultsRequest([]), CancellationToken.None));
        Assert.Equal(
            0,
            await _service.ProcessResultsAsync(
                new AddressResultsRequest([new StreetResult("kyiv", "", Street, null)]), CancellationToken.None));
        Assert.Empty(_f.Db.AddressResults.AsNoTracking());
    }

    [Fact]
    public async Task Process_SavesResultsForHousesNobodyIsWaitingFor()
    {
        await _service.ProcessResultsAsync(
            Results(houses: [("12", Entry()), ("14", Entry(found: false))]), CancellationToken.None);

        Assert.Equal(2, _f.Db.AddressResults.AsNoTracking().Count());
    }

    [Fact]
    public async Task Process_ResultStoredBy_Process_IsServedFromCacheByRequest()
    {
        await _service.ProcessResultsAsync(Results(houses: [("12", Entry(subType: "Аварійне"))]), CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(2));

        var result = await _service.RequestAsync(AddChannel(ChatA), CancellationToken.None);

        Assert.Equal(RequestOutcome.Cached, result.Outcome);
        Assert.Contains("🚨", result.Message);
    }

    // ---- BuildReplyAsync ----

    private static AddressResult Result(
        bool found = true,
        string subType = "",
        string start = "",
        string end = "",
        string type = "") => new()
    {
        RegionId = "kyiv",
        Street = Street,
        House = House,
        Found = found,
        SubType = subType,
        StartDate = start,
        EndDate = end,
        Type = type,
        CheckedAt = Start,
    };

    private Task<string> Reply(AddressResult result, string? label = "Мій дім", string regionId = "kyiv") =>
        _service.BuildReplyAsync(label, result, regionId, CancellationToken.None);

    [Fact]
    public async Task Reply_NotFound()
    {
        var text = await Reply(Result(found: false, subType: "Аварійне"));

        Assert.StartsWith("❓", text);
        Assert.Contains("не знайдено", text);
        Assert.Contains("«Мій дім»", text);
        Assert.DoesNotContain("🚨", text);
    }

    [Theory]
    [InlineData("Аварійне відключення")]
    [InlineData("АВАРІЙНІ")]
    [InlineData("Екстрене відключення")]
    [InlineData("екстрені")]
    public async Task Reply_Emergency(string subType)
    {
        var text = await Reply(Result(subType: subType, start: "15.07 11:00", end: "15.07 15:00"));

        Assert.StartsWith("🚨", text);
        Assert.Contains("позапланове", text);
        Assert.Contains($"Причина: {subType}", text);
        Assert.Contains("15.07 11:00 — 15.07 15:00", text);
        Assert.Contains("«Мій дім»".Trim('«', '»'), text);
    }

    [Fact]
    public async Task Reply_PlannedOutage_IsNotEmergency()
    {
        var text = await Reply(Result(subType: "Планове відключення", start: "15.07 14:00", end: "15.07 18:00"));

        Assert.StartsWith("⏰", text);
        Assert.Contains("планове", text);
        Assert.Contains("15.07 14:00 — 15.07 18:00", text);
        Assert.DoesNotContain("🚨", text);
        Assert.DoesNotContain("Причина", text);
    }

    [Fact]
    public async Task Reply_PlannedOutage_WithOnlyType_IsStillAnOutage()
    {
        var text = await Reply(Result(type: "Планові"));

        Assert.StartsWith("⏰", text);
    }

    [Fact]
    public async Task Reply_PeriodWithOnlyStart_HasNoDanglingDash()
    {
        var text = await Reply(Result(subType: "Планове", start: "15.07 14:00"));

        Assert.Contains("15.07 14:00", text);
        Assert.DoesNotContain("14:00 —", text);
    }

    [Fact]
    public async Task Reply_NoOutage()
    {
        var text = await Reply(Result());

        Assert.StartsWith("✅", text);
        Assert.Contains("відключень на сайті ДТЕК немає", text);
        Assert.Contains("електрощиток", text);
    }

    [Fact]
    public async Task Reply_BlankLabel_FallsBackToStreetAndHouse()
    {
        Assert.Contains($"{Street}, {House}", await Reply(Result(), label: null));
        Assert.Contains($"{Street}, {House}", await Reply(Result(), label: "  "));
    }

    [Fact]
    public async Task Reply_EndsWithCheckedLine_InKyivTime()
    {
        var text = await Reply(Result());

        Assert.EndsWith("Перевірено: 15.07 15:00 (Київ)", text);
    }

    [Theory]
    [InlineData(PopupKind.Emergency, "загальне повідомлення про екстрені відключення")]
    [InlineData(PopupKind.Accident, "загальне повідомлення про аварію в мережі")]
    public async Task Reply_AppendsRegionContext_ForEmergencyAndAccident(PopupKind kind, string expected)
    {
        StoreRegionState("kyiv", kind);

        var text = await Reply(Result());

        Assert.Contains("ℹ️ На сайті ДТЕК діє " + expected, text);
        Assert.EndsWith("(Київ)", text);
    }

    [Theory]
    [InlineData(PopupKind.Stabilization)]
    [InlineData(PopupKind.None)]
    [InlineData(PopupKind.Cancelled)]
    [InlineData(PopupKind.Unknown)]
    public async Task Reply_NoRegionContext_ForOtherKinds(PopupKind kind)
    {
        StoreRegionState("kyiv", kind);

        Assert.DoesNotContain("ℹ️", await Reply(Result()));
    }

    [Fact]
    public async Task Reply_NoRegionContext_WhenStateIsMissingOrBelongsToAnotherRegion()
    {
        Assert.DoesNotContain("ℹ️", await Reply(Result()));

        StoreRegionState("odesa", PopupKind.Emergency);

        Assert.DoesNotContain("ℹ️", await Reply(Result()));
    }

    private void StoreRegionState(string regionId, PopupKind kind)
    {
        _f.Db.RegionStates.Add(new RegionState
        {
            RegionId = regionId,
            Kind = kind,
            Hash = "h",
            ObservedAt = Start,
            UpdatedAt = Start,
        });
        _f.Db.SaveChanges();
    }

    // ---- replies must never re-trigger the «Світло зникло» reaction ----

    private static readonly System.Text.RegularExpressions.Regex PowerOutPattern =
        new(@"світло\s+зникло", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    [Fact]
    public async Task Replies_NeverMatchThePowerOutTriggerPattern()
    {
        var texts = new List<string>();

        // Every outcome of RequestAsync.
        texts.Add((await _service.RequestAsync(AddChannel(401, c => c.AddressStreet = null), CancellationToken.None)).Message);
        texts.Add((await _service.RequestAsync(AddChannel(402, regionId: "odesa"), CancellationToken.None)).Message);
        var queuedChannel = AddChannel(403);
        texts.Add((await _service.RequestAsync(queuedChannel, CancellationToken.None)).Message);
        texts.Add((await _service.RequestAsync(queuedChannel, CancellationToken.None)).Message);
        AddCheck(404, AddressCheckStatus.Done, TimeSpan.FromMinutes(2));
        texts.Add((await _service.RequestAsync(AddChannel(404), CancellationToken.None)).Message);
        for (var i = 0; i < 20; i++)
        {
            AddCheck(405, AddressCheckStatus.Done, TimeSpan.FromMinutes(15 + 60 * i));
        }

        texts.Add((await _service.RequestAsync(AddChannel(405), CancellationToken.None)).Message);
        AddResult(TimeSpan.FromMinutes(1), house: "77");
        texts.Add((await _service.RequestAsync(AddChannel(406, c => c.AddressHouse = "77"), CancellationToken.None)).Message);

        // Expiry notice (sent through Telegram).
        AddCheck(407, AddressCheckStatus.Pending, TimeSpan.FromMinutes(30), house: "88");
        await _service.GetPendingAsync(CancellationToken.None);
        texts.AddRange(_f.Bot.Sent.Select(s => s.Text));

        // Every verdict, with and without region context and with the fallback label.
        foreach (var kind in new[] { PopupKind.None, PopupKind.Emergency, PopupKind.Accident })
        {
            _f.Db.RegionStates.RemoveRange(_f.Db.RegionStates);
            if (kind != PopupKind.None)
            {
                StoreRegionState("kyiv", kind);
            }

            foreach (var result in new[]
            {
                Result(found: false),
                Result(subType: "Аварійне відключення", start: "a", end: "b"),
                Result(subType: "Планове", start: "a", end: "b"),
                Result(),
            })
            {
                texts.Add(await Reply(result));
                texts.Add(await Reply(result, label: null));
            }
        }

        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.DoesNotMatch(PowerOutPattern, t));
        Assert.Matches(PowerOutPattern, "Світло зникло");
    }

    // ---- AddressResult truth table ----

    [Theory]
    //   found, subType,      start, end, type, hasOutage, isEmergency
    [InlineData(false, "", "", "", "", false, false)]
    [InlineData(false, "Аварійне", "a", "b", "t", false, false)]
    [InlineData(true, "", "", "", "", false, false)]
    [InlineData(true, "", "a", "", "", true, false)]
    [InlineData(true, "", "", "b", "", true, false)]
    [InlineData(true, "", "", "", "t", true, false)]
    [InlineData(true, "Планове", "", "", "", true, false)]
    [InlineData(true, "Аварійне", "", "", "", true, true)]
    [InlineData(true, "АВАРІЙНЕ", "", "", "", true, true)]
    [InlineData(true, "Екстрене відключення", "", "", "", true, true)]
    [InlineData(true, "ЕКСТРЕНЕ", "", "", "", true, true)]
    [InlineData(true, "Планове", "a", "b", "Аварійне", true, false)]
    public void AddressResult_OutageFlags(
        bool found, string subType, string start, string end, string type, bool hasOutage, bool isEmergency)
    {
        var result = Result(found, subType, start, end, type);

        Assert.Equal(hasOutage, result.HasOutage);
        Assert.Equal(isEmergency, result.IsEmergency);
    }
}
