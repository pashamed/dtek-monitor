using DtekBot.Api.Data;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class ScheduleNotifierTests : IDisposable
{
    private const long ChatA = 801;
    private const long ChatB = 802;
    private const long ChatC = 803;
    private const string Version12 = "1991457b4408";
    private const string ImageBase = "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/images/kyiv/";

    private readonly DispatcherFixture _f = new();
    private readonly TestClock _clock = new(Kyiv(15, 9));
    private readonly FakeScheduleProvider _schedules = new();
    private readonly ScheduleNotifier _notifier;

    public ScheduleNotifierTests()
    {
        _notifier = new ScheduleNotifier(
            _f.Db, _f.Bot, _schedules, _f.Options, _clock, NullLogger<ScheduleNotifier>.Instance);
        _schedules.Snapshots["kyiv"] = RealSnapshot();
    }

    public void Dispose() => _f.Dispose();

    // ---- helpers ----

    private Channel AddChannel(long chatId = ChatA, Action<Channel>? configure = null, string regionId = "kyiv") =>
        _f.AddChannel(chatId, c =>
        {
            c.GroupId = "45.1";
            c.ScheduleNotify = true;
            configure?.Invoke(c);
        }, regionId);

    private Task<ScheduleTickSummary> TickAsync(DateTimeOffset? at = null)
    {
        if (at is { } moment)
        {
            _clock.Now = moment;
        }

        return _notifier.TickAsync(CancellationToken.None);
    }

    private ScheduleThread? ThreadOf(long chatId) =>
        _f.Db.ScheduleThreads.AsNoTracking().SingleOrDefault(t => t.ChatId == chatId);

    private static string PhotoUrl(SendPhotoRequest request) => ((InputFileUrl)request.Photo).Url.ToString();

    private static string MediaUrl(EditMessageMediaRequest request) =>
        ((InputFileUrl)((InputMediaPhoto)request.Media).Media).Url.ToString();

    private static string? MediaCaption(EditMessageMediaRequest request) => ((InputMediaPhoto)request.Media).Caption;

    private static ScheduleSnapshot WithOutages(ScheduleSnapshot snapshot, string group, params OutageInterval[] outages) =>
        snapshot with
        {
            Groups = new Dictionary<string, GroupSchedule>(snapshot.Groups) { [group] = new GroupSchedule(group, outages) },
        };

    private static ScheduleSnapshot WithVersion(ScheduleSnapshot snapshot, string version) => snapshot with { Version = version };

    private static OutageInterval Out(DateTimeOffset start, DateTimeOffset end) => new(start, end);

    private static readonly long Outage1Start = Kyiv(15, 11).ToUnixTimeSeconds();
    private static readonly long Outage2Start = Kyiv(15, 20, 30).ToUnixTimeSeconds();

    private const string OnCaption09 =
        "🟢 Світло є за графіком\nм. Київ · черга 45.1\n🗓 Наступне відключення за графіком о 11:00";

    private const string OffCaption12 =
        "🔴 11:00 Світло вимкнено за графіком\nм. Київ · черга 45.1\n🗓 Очікуємо повернення о 14:00";

    // ---- eligibility ----

    [Theory]
    [InlineData("off")]
    [InlineData("paused")]
    [InlineData("inactive")]
    [InlineData("nogroup")]
    public async Task IneligibleChannels_GetNothing(string reason)
    {
        AddChannel(configure: c =>
        {
            switch (reason)
            {
                case "off": c.ScheduleNotify = false; break;
                case "paused": c.IsPaused = true; break;
                case "inactive": c.IsActive = false; break;
                case "nogroup": c.GroupId = null; break;
            }
        });

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(0, 0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.Empty(_schedules.Calls);
        Assert.Null(ThreadOf(ChatA));
    }

    [Fact]
    public async Task NoChannelsAtAll_IsAnEmptyTick()
    {
        Assert.Equal(new ScheduleTickSummary(0, 0, 0, 0, 0), await TickAsync());
    }

    // ---- first message ----

    [Fact]
    public async Task FirstTick_WhilePowerIsOn_PostsOnePhoto_WithoutTheOutageDuration()
    {
        AddChannel();

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        var photo = Assert.Single(_f.Bot.Photos);
        Assert.Equal(ChatA, photo.ChatId.Identifier);
        Assert.Equal(ImageBase + "gpv-45-1-emergency.png?v=" + Version12, PhotoUrl(photo));
        Assert.Equal(OnCaption09, photo.Caption);
        Assert.DoesNotContain("не було", photo.Caption);
        Assert.Equal(default, photo.ParseMode);
        Assert.Single(_f.Bot.Requests);

        var thread = ThreadOf(ChatA)!;
        Assert.Equal(SchedulePhase.On, thread.Phase);
        Assert.Equal("kyiv", thread.RegionId);
        Assert.Equal("45.1", thread.GroupId);
        Assert.True(thread.IsPhoto);
        Assert.Equal(0, thread.EpisodeStart);
        Assert.Equal(RealVersion, thread.ImageVersion);
        Assert.True(thread.MessageId >= 1000);
        Assert.False(string.IsNullOrEmpty(thread.ContentHash));
        Assert.Equal(_clock.Now, thread.UpdatedAt);
    }

    [Fact]
    public async Task FirstTick_BetweenOutages_ShowsHowLongThePowerHasBeenOn()
    {
        AddChannel();

        await TickAsync(Kyiv(15, 15));

        var caption = Assert.Single(_f.Bot.Photos).Caption;
        Assert.Equal(
            "🟢 Світло є за графіком\nм. Київ · черга 45.1\n🕓 Світло є вже 1 год\n🗓 Наступне відключення за графіком о 20:30",
            caption);
    }

    [Fact]
    public async Task SecondTick_WithUnchangedData_IsSkipped()
    {
        AddChannel();
        await TickAsync();

        var summary = await TickAsync(Kyiv(15, 9, 10));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Requests);
    }

    // ---- outage episodes ----

    [Fact]
    public async Task OutageStart_PostsANewPhoto_AndStartsANewEpisode()
    {
        AddChannel();
        await TickAsync();
        var onThread = ThreadOf(ChatA)!;

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Photos.Count());
        var photo = _f.Bot.Photos.Last();
        Assert.Equal(OffCaption12, photo.Caption);
        Assert.DoesNotContain("Світло було", photo.Caption);
        Assert.StartsWith("🔴 11:00 Світло вимкнено за графіком", photo.Caption);

        var thread = ThreadOf(ChatA)!;
        Assert.Equal(SchedulePhase.Off, thread.Phase);
        Assert.Equal(Outage1Start, thread.EpisodeStart);
        Assert.NotEqual(onThread.MessageId, thread.MessageId);
        Assert.Empty(_f.Bot.Edits);
        Assert.Empty(_f.Bot.CaptionEdits);
    }

    [Fact]
    public async Task OutageCaption_ShowsPreviousPowerDuration_WhenThePreviousEndIsInsideTheData()
    {
        AddChannel();

        await TickAsync(Kyiv(15, 21));

        var caption = Assert.Single(_f.Bot.Photos).Caption;
        Assert.Equal(
            "🔴 20:30 Світло вимкнено за графіком\nм. Київ · черга 45.1\n🕓 Світло було 6 год 30 хв\n🗓 Очікуємо повернення о 00:00 (16.10)",
            caption);
        Assert.Equal(Outage2Start, ThreadOf(ChatA)!.EpisodeStart);
    }

    [Fact]
    public async Task SameOutage_WithUnchangedData_IsSkipped()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Requests);
    }

    [Fact]
    public async Task ChangedScheduleVersion_EditsTheMessageMedia_WithNewUrlAndCaption()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        var thread = ThreadOf(ChatA)!;
        _schedules.Snapshots["kyiv"] = WithVersion(RealSnapshot(), "newversion123456789");

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        var edit = Assert.Single(_f.Bot.MediaEdits);
        Assert.Equal(ChatA, edit.ChatId.Identifier);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Equal(ImageBase + "gpv-45-1-emergency.png?v=newversion12", MediaUrl(edit));
        Assert.Equal(OffCaption12, MediaCaption(edit));
        Assert.Empty(_f.Bot.CaptionEdits);

        var updated = ThreadOf(ChatA)!;
        Assert.Equal("newversion123456789", updated.ImageVersion);
        Assert.Equal(thread.MessageId, updated.MessageId);
        Assert.Equal(Outage1Start, updated.EpisodeStart);
    }

    [Fact]
    public async Task ChangedText_WithSameVersion_EditsOnlyTheCaption()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        var thread = ThreadOf(ChatA)!;
        _schedules.Snapshots["kyiv"] = WithOutages(RealSnapshot(), "45.1", Out(Kyiv(15, 11), Kyiv(15, 15)));

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        var edit = Assert.Single(_f.Bot.CaptionEdits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Contains("🗓 Очікуємо повернення о 15:00", edit.Caption);
        Assert.Empty(_f.Bot.MediaEdits);
        Assert.Empty(_f.Bot.Edits);
    }

    [Fact]
    public async Task PowerBack_EditsTheSameMessage_ToTheRestoredText()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        var thread = ThreadOf(ChatA)!;

        var summary = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        Assert.Single(_f.Bot.Photos);
        var edit = Assert.Single(_f.Bot.CaptionEdits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Equal(
            "🟢 14:00 Світло повернулось за графіком\nм. Київ · черга 45.1\n🕓 Світла не було 3 год\n🕓 Світло є вже 5 хв\n🗓 Наступне відключення за графіком о 20:30",
            edit.Caption);

        var updated = ThreadOf(ChatA)!;
        Assert.Equal(SchedulePhase.On, updated.Phase);
        Assert.Equal(Outage1Start, updated.EpisodeStart);
        Assert.Equal(thread.MessageId, updated.MessageId);
    }

    [Fact]
    public async Task AfterPowerIsBack_ALaterTick_EditsOnlyBecauseTheElapsedTimeChanged()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        await TickAsync(Kyiv(15, 14, 5));

        var changed = await TickAsync(Kyiv(15, 14, 20));
        var unchanged = await TickAsync(Kyiv(15, 14, 20, 30));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), changed);
        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), unchanged);
        Assert.Contains("🕓 Світло є вже 20 хв", _f.Bot.CaptionEdits.Last().Caption);
        Assert.Equal(2, _f.Bot.CaptionEdits.Count());
        Assert.Equal(Outage1Start, ThreadOf(ChatA)!.EpisodeStart);
    }

    [Fact]
    public async Task NextOutage_AfterTheRestoredMessage_StartsAFreshEpisode_WithANewMessage()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        await TickAsync(Kyiv(15, 14, 5));
        var restored = ThreadOf(ChatA)!;

        var summary = await TickAsync(Kyiv(15, 21));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Photos.Count());
        var thread = ThreadOf(ChatA)!;
        Assert.Equal(SchedulePhase.Off, thread.Phase);
        Assert.Equal(Outage2Start, thread.EpisodeStart);
        Assert.NotEqual(restored.MessageId, thread.MessageId);
    }

    // ---- EditInPlace = false ----

    [Fact]
    public async Task WithoutEditInPlace_PhaseChangesAndNewEpisodesSendNewMessages_AndNothingIsEdited()
    {
        AddChannel(configure: c => c.EditInPlace = false);

        var first = await TickAsync(Kyiv(15, 9));
        var outage = await TickAsync(Kyiv(15, 12));
        var same = await TickAsync(Kyiv(15, 12, 15));
        var back = await TickAsync(Kyiv(15, 14, 5));
        var later = await TickAsync(Kyiv(15, 14, 20));

        Assert.Equal(1, first.Sent);
        Assert.Equal(1, outage.Sent);
        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), same);
        Assert.Equal(1, back.Sent);
        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), later);

        Assert.Equal(3, _f.Bot.Photos.Count());
        Assert.Empty(_f.Bot.Edits);
        Assert.Empty(_f.Bot.CaptionEdits);
        Assert.Empty(_f.Bot.MediaEdits);

        var thread = ThreadOf(ChatA)!;
        Assert.Equal(SchedulePhase.On, thread.Phase);
        Assert.Equal(Outage1Start, thread.EpisodeStart);
        Assert.Contains("повернулось", _f.Bot.Photos.Last().Caption);
    }

    [Fact]
    public async Task WithoutEditInPlace_ChangedDataInTheSameEpisode_IsNotEdited()
    {
        AddChannel(configure: c => c.EditInPlace = false);
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["kyiv"] = WithVersion(WithOutages(RealSnapshot(), "45.1", Out(Kyiv(15, 11), Kyiv(15, 15))), "other");

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Requests);
    }

    // ---- thread lifecycle ----

    [Fact]
    public async Task TurningNotificationsOff_RemovesTheThread_OnTheNextTick()
    {
        var channel = AddChannel();
        await TickAsync();
        Assert.NotNull(ThreadOf(ChatA));

        channel.ScheduleNotify = false;
        _f.Db.SaveChanges();
        _f.Bot.Requests.Clear();
        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(0, 0, 0, 0, 0), summary);
        Assert.Null(ThreadOf(ChatA));
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task TurningNotificationsBackOn_StartsFresh_WithANewMessage()
    {
        var channel = AddChannel();
        await TickAsync();
        channel.ScheduleNotify = false;
        _f.Db.SaveChanges();
        await TickAsync();

        channel.ScheduleNotify = true;
        _f.Db.SaveChanges();
        var summary = await TickAsync();

        Assert.Equal(1, summary.Sent);
        Assert.Equal(2, _f.Bot.Photos.Count());
        Assert.NotNull(ThreadOf(ChatA));
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("inactive")]
    [InlineData("nogroup")]
    public async Task ThreadOfAChannelThatBecameIneligible_IsRemoved(string reason)
    {
        var channel = AddChannel();
        await TickAsync();

        switch (reason)
        {
            case "paused": channel.IsPaused = true; break;
            case "inactive": channel.IsActive = false; break;
            case "nogroup": channel.GroupId = null; break;
        }

        _f.Db.SaveChanges();
        _f.Bot.Requests.Clear();
        await TickAsync();

        Assert.Null(ThreadOf(ChatA));
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task ChangingTheGroup_DropsTheOldThread_AndStartsFresh()
    {
        var channel = AddChannel();
        await TickAsync(Kyiv(15, 12));
        var old = ThreadOf(ChatA)!;

        channel.GroupId = "1.1";
        _f.Db.SaveChanges();
        var summary = await TickAsync();

        Assert.Equal(1, summary.Sent);
        Assert.Equal(0, summary.Edited);
        Assert.Contains("gpv-1-1-emergency.png", PhotoUrl(_f.Bot.Photos.Last()));
        var thread = Assert.Single(_f.Db.ScheduleThreads.AsNoTracking());
        Assert.Equal("1.1", thread.GroupId);
        Assert.NotEqual(old.MessageId, thread.MessageId);
    }

    [Fact]
    public async Task ChangingTheRegion_DropsTheOldThread()
    {
        var channel = AddChannel();
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["odesa"] = null;

        channel.RegionId = "odesa";
        _f.Db.SaveChanges();
        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Null(ThreadOf(ChatA));
    }

    // ---- midnight ----

    private static ScheduleSnapshot EveningOutageSnapshot(string version) =>
        ScheduleParser.Parse(
            Json(version, "15.10.2026 21:00",
                (Day1, "45.1", Hours("no", 21, 22, 23, 24)),
                (Day2, "45.1", Hours("no", 1, 2, 3))),
            "kyiv",
            Kyiv(15, 21))!;

    private static ScheduleSnapshot NextDaySnapshot(string version) =>
        ScheduleParser.Parse(
            Json(version, "16.10.2026 00:10",
                (Day2, "45.1", Hours("no", 1, 2, 3)),
                (Day2 + 86400, "45.1", [])),
            "kyiv",
            Kyiv(16, 0, 10))!;

    [Fact]
    public async Task OutageStraddlingMidnight_KeepsItsEpisode_WhenTheDataWindowRollsForward()
    {
        AddChannel();
        _schedules.Snapshots["kyiv"] = EveningOutageSnapshot("v1");
        await TickAsync(Kyiv(15, 22));
        var thread = ThreadOf(ChatA)!;
        Assert.Equal(SchedulePhase.Off, thread.Phase);
        Assert.Equal(Kyiv(15, 20).ToUnixTimeSeconds(), thread.EpisodeStart);

        // After midnight the feed starts at today: the outage now touches the start of the data.
        _schedules.Snapshots["kyiv"] = NextDaySnapshot("v2");
        var summary = await TickAsync(Kyiv(16, 0, 30));

        Assert.Equal(0, summary.Sent);
        Assert.Equal(1, summary.Edited);
        Assert.Single(_f.Bot.Photos);
        var edit = Assert.Single(_f.Bot.MediaEdits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Equal("🔴 Світло вимкнено за графіком\nм. Київ · черга 45.1\n🗓 Очікуємо повернення о 03:00", MediaCaption(edit));

        var after = ThreadOf(ChatA)!;
        Assert.Equal(SchedulePhase.Off, after.Phase);
        Assert.Equal(thread.EpisodeStart, after.EpisodeStart);
        Assert.Equal(thread.MessageId, after.MessageId);
    }

    [Fact]
    public async Task OutageStraddlingMidnight_ThenPowerBack_ReportsTheFullDuration()
    {
        AddChannel();
        _schedules.Snapshots["kyiv"] = EveningOutageSnapshot("v1");
        await TickAsync(Kyiv(15, 22));
        _schedules.Snapshots["kyiv"] = NextDaySnapshot("v2");
        await TickAsync(Kyiv(16, 0, 30));

        var summary = await TickAsync(Kyiv(16, 3, 30));

        Assert.Equal(1, summary.Edited);
        Assert.Equal(0, summary.Sent);
        var caption = _f.Bot.CaptionEdits.Last().Caption;
        Assert.Contains("🟢 03:00 Світло повернулось за графіком", caption);
        Assert.Contains("🕓 Світла не було 7 год", caption);
        Assert.Contains("🕓 Світло є вже 30 хв", caption);
    }

    [Fact]
    public async Task OutageAlreadyRunningAtTheStartOfTheData_WithoutAThread_UsesTheCoverageStartAsEpisode()
    {
        AddChannel();
        _schedules.Snapshots["kyiv"] = NextDaySnapshot("v2");

        var first = await TickAsync(Kyiv(16, 0, 30));
        var second = await TickAsync(Kyiv(16, 1));

        Assert.Equal(1, first.Sent);
        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), second);
        Assert.Equal(Kyiv(16, 0).ToUnixTimeSeconds(), ThreadOf(ChatA)!.EpisodeStart);
        Assert.StartsWith("🔴 Світло вимкнено за графіком", Assert.Single(_f.Bot.Photos).Caption);
    }

    // ---- picture fallbacks ----

    [Fact]
    public async Task PhotoRejected_FallsBackToText_AndLaterEditsUseEditMessageText()
    {
        AddChannel();
        _f.Bot.Failure = r => r is SendPhotoRequest
            ? new ApiRequestException("Bad Request: wrong type of the web page content", 400)
            : null;

        var summary = await TickAsync(Kyiv(15, 15));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Single(_f.Bot.Photos);
        var text = Assert.Single(_f.Bot.Sent);
        Assert.StartsWith("🟢 Світло є за графіком", text.Text);
        Assert.Equal(default, text.ParseMode);
        var thread = ThreadOf(ChatA)!;
        Assert.False(thread.IsPhoto);

        await TickAsync(Kyiv(15, 15, 10));

        var edit = Assert.Single(_f.Bot.Edits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Contains("🕓 Світло є вже 1 год 10 хв", edit.Text);
        Assert.Empty(_f.Bot.CaptionEdits);
        Assert.Empty(_f.Bot.MediaEdits);
        Assert.False(ThreadOf(ChatA)!.IsPhoto);
    }

    [Fact]
    public async Task PhotoRejected_ButChatNotFound_IsNotAFallback_ItDeactivatesTheChannel()
    {
        AddChannel();
        _f.Bot.Failure = r => r is SendPhotoRequest ? new ApiRequestException("Bad Request: chat not found", 400) : null;

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 0, 1), summary);
        Assert.Empty(_f.Bot.Sent);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
        Assert.Null(ThreadOf(ChatA));
    }

    [Fact]
    public async Task MediaEditRejected_FallsBackToACaptionEdit()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["kyiv"] = WithVersion(RealSnapshot(), "brandnewversion");
        _f.Bot.Failure = r => r is EditMessageMediaRequest
            ? new ApiRequestException("Bad Request: wrong file identifier/HTTP URL specified", 400)
            : null;

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        Assert.Single(_f.Bot.MediaEdits);
        Assert.Single(_f.Bot.CaptionEdits);
        Assert.Single(_f.Bot.Photos);
        Assert.Equal("brandnewversion", ThreadOf(ChatA)!.ImageVersion);
    }

    // ---- edit failures ----

    [Theory]
    [InlineData("Bad Request: message to edit not found")]
    [InlineData("Bad Request: message can't be edited")]
    public async Task CaptionEditOfAMissingMessage_PostsANewOne(string error)
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        var old = ThreadOf(ChatA)!;
        _f.Bot.Failure = r => r is EditMessageCaptionRequest ? new ApiRequestException(error, 400) : null;

        var summary = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Photos.Count());
        var thread = ThreadOf(ChatA)!;
        Assert.NotEqual(old.MessageId, thread.MessageId);
        Assert.Equal(SchedulePhase.On, thread.Phase);
        Assert.Contains("повернулось", _f.Bot.Photos.Last().Caption);
    }

    [Fact]
    public async Task MediaEditOfAMissingMessage_PostsANewOne()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        var old = ThreadOf(ChatA)!;
        _schedules.Snapshots["kyiv"] = WithVersion(RealSnapshot(), "versionchanged1");
        _f.Bot.Failure = r => r is EditMessageMediaRequest
            ? new ApiRequestException("Bad Request: message to edit not found", 400)
            : null;

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(1, summary.Sent);
        Assert.Equal(0, summary.Edited);
        Assert.Empty(_f.Bot.CaptionEdits);
        Assert.NotEqual(old.MessageId, ThreadOf(ChatA)!.MessageId);
        Assert.Equal("versionchanged1", ThreadOf(ChatA)!.ImageVersion);
    }

    [Fact]
    public async Task TextEditOfAMissingMessage_PostsANewOne()
    {
        AddChannel();
        _f.Bot.Failure = r => r is SendPhotoRequest ? new ApiRequestException("Bad Request: wrong type of the web page content", 400) : null;
        await TickAsync(Kyiv(15, 15));
        var old = ThreadOf(ChatA)!;
        _f.Bot.Failure = r => r is EditMessageTextRequest ? new ApiRequestException("Bad Request: message to edit not found", 400) : null;

        var summary = await TickAsync(Kyiv(15, 15, 10));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.NotEqual(old.MessageId, ThreadOf(ChatA)!.MessageId);
    }

    [Fact]
    public async Task MessageNotModified_OnCaptionEdit_IsTreatedAsSuccess()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        _f.Bot.Failure = r => r is EditMessageCaptionRequest ? FakeTelegramBotClient.NotModified() : null;

        var summary = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        Assert.Single(_f.Bot.Photos);
        Assert.Equal(SchedulePhase.On, ThreadOf(ChatA)!.Phase);

        _f.Bot.Failure = null;
        Assert.Equal(1, (await TickAsync(Kyiv(15, 14, 5, 10))).Skipped);
    }

    [Fact]
    public async Task MessageNotModified_OnMediaEdit_IsTreatedAsSuccess_WithoutACaptionEdit()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["kyiv"] = WithVersion(RealSnapshot(), "notmodified123");
        _f.Bot.Failure = r => r is EditMessageMediaRequest ? FakeTelegramBotClient.NotModified() : null;

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        Assert.Empty(_f.Bot.CaptionEdits);
        Assert.Equal("notmodified123", ThreadOf(ChatA)!.ImageVersion);
    }

    // ---- Telegram failures ----

    [Fact]
    public async Task Telegram403_OnSend_DeactivatesTheChannel_AndCountsFailed()
    {
        AddChannel();
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 0, 1), summary);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
        Assert.Null(ThreadOf(ChatA));
    }

    [Fact]
    public async Task Telegram403_OnEdit_DeactivatesTheChannel_AndRemovesTheThread()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var summary = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 0, 1), summary);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
        Assert.Null(ThreadOf(ChatA));
    }

    [Fact]
    public async Task Telegram403ForOneChat_DoesNotStopTheOthers()
    {
        AddChannel(ChatA);
        AddChannel(ChatB);
        _f.Bot.Failure = r => r is SendPhotoRequest { ChatId.Identifier: ChatA } ? FakeTelegramBotClient.Forbidden() : null;

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(2, 1, 0, 0, 1), summary);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
        Assert.True(_f.LoadChannel(ChatB).IsActive);
        Assert.NotNull(ThreadOf(ChatB));
    }

    [Fact]
    public async Task GenericFailure_CountsFailed_KeepsTheChannelActive_AndRetriesNextTick()
    {
        AddChannel();
        _f.Bot.Failure = _ => new HttpRequestException("network down");

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 0, 1), summary);
        Assert.True(_f.LoadChannel(ChatA).IsActive);
        Assert.Null(ThreadOf(ChatA));

        _f.Bot.Failure = null;
        Assert.Equal(1, (await TickAsync()).Sent);
    }

    [Fact]
    public async Task RateLimit429_IsRetriedOnce()
    {
        AddChannel();
        var calls = 0;
        _f.Bot.Failure = r => r is SendPhotoRequest && calls++ == 0
            ? new ApiRequestException("Too Many Requests: retry after 0", 429, new ResponseParameters { RetryAfter = 0 })
            : null;

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Photos.Count());
        Assert.NotNull(ThreadOf(ChatA));
    }

    [Fact]
    public async Task RateLimit429_TwiceInARow_IsFailed()
    {
        AddChannel();
        _f.Bot.Failure = r => r is SendPhotoRequest
            ? new ApiRequestException("Too Many Requests: retry after 0", 429, new ResponseParameters { RetryAfter = 0 })
            : null;

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 0, 1), summary);
        Assert.Equal(2, _f.Bot.Photos.Count());
        Assert.True(_f.LoadChannel(ChatA).IsActive);
    }

    // ---- provider interplay ----

    [Fact]
    public async Task RegionWithoutData_SkipsAllItsChats_AndSendsNothing()
    {
        _schedules.Snapshots["kyiv"] = null;
        AddChannel(ChatA);
        AddChannel(ChatB);

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(2, 0, 0, 2, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.Null(ThreadOf(ChatA));
    }

    [Fact]
    public async Task ExistingThread_IsKept_WhenTheDataIsTemporarilyUnavailable()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        var thread = ThreadOf(ChatA)!;
        _schedules.Snapshots["kyiv"] = null;

        var summary = await TickAsync(Kyiv(15, 12, 30));

        Assert.Equal(1, summary.Skipped);
        var after = ThreadOf(ChatA)!;
        Assert.Equal(thread.MessageId, after.MessageId);
        Assert.Equal(SchedulePhase.Off, after.Phase);
    }

    [Fact]
    public async Task GroupMissingFromTheSnapshot_IsSkipped()
    {
        AddChannel(configure: c => c.GroupId = "99.9");

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.Null(ThreadOf(ChatA));
    }

    [Theory]
    [InlineData(14, 23, 59)]
    [InlineData(17, 0, 0)]
    [InlineData(20, 12, 0)]
    public async Task OutsideTheCoverageOfTheData_IsSkipped(int day, int hour, int minute)
    {
        AddChannel();

        var summary = await TickAsync(Kyiv(day, hour, minute));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task TwoChatsInTheSameRegion_BothGetMessages_WithOneProviderCall()
    {
        AddChannel(ChatA);
        AddChannel(ChatB);

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(2, 2, 0, 0, 0), summary);
        Assert.Equal(["kyiv"], _schedules.Calls);
        Assert.Single(_f.Bot.Photos, p => p.ChatId.Identifier == ChatA);
        Assert.Single(_f.Bot.Photos, p => p.ChatId.Identifier == ChatB);
        Assert.NotEqual(ThreadOf(ChatA)!.MessageId, ThreadOf(ChatB)!.MessageId);
    }

    [Fact]
    public async Task ChatsOfDifferentRegions_OneProviderCallPerRegion_AndEachRegionIsIndependent()
    {
        _schedules.Snapshots["odesa"] = null;
        AddChannel(ChatA);
        AddChannel(ChatB);
        AddChannel(ChatC, regionId: "odesa");

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(3, 2, 0, 1, 0), summary);
        Assert.Equal(["kyiv", "odesa"], _schedules.Calls.OrderBy(c => c));
        Assert.Null(ThreadOf(ChatC));
        Assert.NotNull(ThreadOf(ChatA));
    }

    [Fact]
    public async Task ChatsInTheSameRegion_WithDifferentGroups_GetTheirOwnSchedules()
    {
        AddChannel(ChatA);
        AddChannel(ChatB, c => c.GroupId = "1.1");

        await TickAsync();

        Assert.Contains("gpv-45-1-emergency.png", PhotoUrl(_f.Bot.Photos.Single(p => p.ChatId.Identifier == ChatA)));
        Assert.Contains("gpv-1-1-emergency.png", PhotoUrl(_f.Bot.Photos.Single(p => p.ChatId.Identifier == ChatB)));
    }

    [Fact]
    public async Task RegionOfAnotherDataFile_UsesItsOwnImageFolder()
    {
        _schedules.Snapshots["kyiv-oblast"] = RealSnapshot(dataFile: "kyiv-region");
        AddChannel(ChatA, regionId: "kyiv-oblast");

        await TickAsync();

        Assert.Contains("/images/kyiv-region/gpv-45-1-emergency.png", PhotoUrl(Assert.Single(_f.Bot.Photos)));
    }

    // ---- picture kinds ----

    private const string CardUrl = ImageBase + "gpv-45-1-summary.png?v=" + Version12;
    private const string GridUrl = ImageBase + "gpv-45-1-emergency.png?v=" + Version12;

    [Fact]
    public async Task DefaultImageKind_IsTheGrid_AndIsStoredOnTheThread()
    {
        AddChannel();

        await TickAsync();

        Assert.Equal(GridUrl, PhotoUrl(Assert.Single(_f.Bot.Photos)));
        Assert.Equal(ScheduleImage.Grid, ThreadOf(ChatA)!.ImageKind);
    }

    [Fact]
    public async Task CardKind_PostsTheSummaryPicture()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Card);

        await TickAsync();

        var photo = Assert.Single(_f.Bot.Photos);
        Assert.Equal(CardUrl, PhotoUrl(photo));
        Assert.Equal(OnCaption09, photo.Caption);
        var thread = ThreadOf(ChatA)!;
        Assert.True(thread.IsPhoto);
        Assert.Equal(ScheduleImage.Card, thread.ImageKind);
    }

    [Fact]
    public async Task NoneKind_SendsTextOnly_AndRecordsIt()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.None);

        var summary = await TickAsync();

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Photos);
        var message = Assert.Single(_f.Bot.Sent);
        Assert.Equal(OnCaption09, message.Text);
        Assert.Equal(default, message.ParseMode);
        var thread = ThreadOf(ChatA)!;
        Assert.False(thread.IsPhoto);
        Assert.Equal(ScheduleImage.None, thread.ImageKind);
    }

    [Fact]
    public async Task NoneKind_LaterChanges_AreTextEdits_NeverCaptionOrMediaEdits()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.None);
        await TickAsync(Kyiv(15, 15));
        var thread = ThreadOf(ChatA)!;

        var summary = await TickAsync(Kyiv(15, 15, 10));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        var edit = Assert.Single(_f.Bot.Edits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Contains("🕓 Світло є вже 1 год 10 хв", edit.Text);
        Assert.Empty(_f.Bot.CaptionEdits);
        Assert.Empty(_f.Bot.MediaEdits);
        Assert.Empty(_f.Bot.Photos);
    }

    [Fact]
    public async Task NoneKind_OutageStartAndPowerBack_FollowTheNormalEpisodeFlow_AsText()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.None);

        await TickAsync(Kyiv(15, 9));
        var outage = await TickAsync(Kyiv(15, 12));
        var back = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(1, outage.Sent);
        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), back);
        Assert.Equal(2, _f.Bot.Sent.Count());
        Assert.Contains("повернулось", Assert.Single(_f.Bot.Edits).Text);
        Assert.Empty(_f.Bot.Photos);
        Assert.Equal(ScheduleImage.None, ThreadOf(ChatA)!.ImageKind);
    }

    [Theory]
    [InlineData(ScheduleImage.Grid, ScheduleImage.Card, CardUrl)]
    [InlineData(ScheduleImage.Card, ScheduleImage.Grid, GridUrl)]
    public async Task SwitchingBetweenGridAndCard_EditsTheMedia_EvenWithTheSameDataVersion(
        ScheduleImage from, ScheduleImage to, string expectedUrl)
    {
        var channel = AddChannel(configure: c => c.ScheduleImage = from);
        await TickAsync(Kyiv(15, 12));
        var thread = ThreadOf(ChatA)!;
        Assert.Equal(from, thread.ImageKind);

        channel.ScheduleImage = to;
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        var edit = Assert.Single(_f.Bot.MediaEdits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Equal(expectedUrl, MediaUrl(edit));
        Assert.Equal(OffCaption12, MediaCaption(edit));
        Assert.Single(_f.Bot.Photos);

        var updated = ThreadOf(ChatA)!;
        Assert.Equal(to, updated.ImageKind);
        Assert.Equal(thread.MessageId, updated.MessageId);
        Assert.True(updated.IsPhoto);

        // Nothing is left to change on the following tick.
        Assert.Equal(1, (await TickAsync(Kyiv(15, 12, 6))).Skipped);
    }

    [Fact]
    public async Task SwitchingGridToCard_WithoutEditInPlace_IsLeftAlone()
    {
        var channel = AddChannel(configure: c => c.EditInPlace = false);
        await TickAsync(Kyiv(15, 12));

        channel.ScheduleImage = ScheduleImage.Card;
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Empty(_f.Bot.MediaEdits);
    }

    [Fact]
    public async Task SwitchingPictureToNone_PostsANewTextMessage_AndReplacesTheThread()
    {
        var channel = AddChannel();
        await TickAsync(Kyiv(15, 12));
        var old = ThreadOf(ChatA)!;

        channel.ScheduleImage = ScheduleImage.None;
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.MediaEdits);
        Assert.Empty(_f.Bot.CaptionEdits);
        Assert.Empty(_f.Bot.Edits);
        var message = Assert.Single(_f.Bot.Sent);
        Assert.Equal(OffCaption12, message.Text);

        var thread = Assert.Single(_f.Db.ScheduleThreads.AsNoTracking());
        Assert.NotEqual(old.MessageId, thread.MessageId);
        Assert.False(thread.IsPhoto);
        Assert.Equal(ScheduleImage.None, thread.ImageKind);
        Assert.Equal(Outage1Start, thread.EpisodeStart);
    }

    [Theory]
    [InlineData(ScheduleImage.Grid, GridUrl)]
    [InlineData(ScheduleImage.Card, CardUrl)]
    public async Task SwitchingNoneToPicture_PostsANewPhoto_AndReplacesTheThread(ScheduleImage kind, string url)
    {
        var channel = AddChannel(configure: c => c.ScheduleImage = ScheduleImage.None);
        await TickAsync(Kyiv(15, 12));
        var old = ThreadOf(ChatA)!;

        channel.ScheduleImage = kind;
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(url, PhotoUrl(Assert.Single(_f.Bot.Photos)));
        Assert.Empty(_f.Bot.Edits);

        var thread = Assert.Single(_f.Db.ScheduleThreads.AsNoTracking());
        Assert.NotEqual(old.MessageId, thread.MessageId);
        Assert.True(thread.IsPhoto);
        Assert.Equal(kind, thread.ImageKind);
    }

    [Fact]
    public async Task SwitchingPictureMode_PostsANewMessage_EvenWithoutEditInPlace()
    {
        var channel = AddChannel(configure: c => c.EditInPlace = false);
        await TickAsync(Kyiv(15, 12));

        channel.ScheduleImage = ScheduleImage.None;
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(1, summary.Sent);
    }

    [Fact]
    public async Task TextOnlyFallback_KeepsTheRequestedKind_SoLaterTicksDoNotRepost()
    {
        AddChannel();
        _f.Bot.Failure = r => r is SendPhotoRequest
            ? new ApiRequestException("Bad Request: wrong type of the web page content", 400)
            : null;

        await TickAsync(Kyiv(15, 12));
        var thread = ThreadOf(ChatA)!;
        Assert.False(thread.IsPhoto);
        Assert.Equal(ScheduleImage.Grid, thread.ImageKind);

        var second = await TickAsync(Kyiv(15, 12, 5));
        var third = await TickAsync(Kyiv(15, 12, 10));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), second);
        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), third);
        Assert.Single(_f.Bot.Photos);
        Assert.Single(_f.Bot.Sent);
        Assert.Equal(thread.MessageId, ThreadOf(ChatA)!.MessageId);
    }

    [Fact]
    public async Task TextOnlyFallback_WithANewDataVersion_EditsTheText_InsteadOfPostingAgain()
    {
        AddChannel();
        _f.Bot.Failure = r => r is SendPhotoRequest
            ? new ApiRequestException("Bad Request: wrong type of the web page content", 400)
            : null;
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["kyiv"] = WithVersion(RealSnapshot(), "freshversion123");

        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(0, summary.Sent);
        Assert.Single(_f.Bot.Photos);
        Assert.Empty(_f.Bot.MediaEdits);
        Assert.Empty(_f.Bot.CaptionEdits);
    }

    [Fact]
    public async Task ChangedKind_IsSavedOnTheThread_OnEveryPostAndEdit()
    {
        var channel = AddChannel(configure: c => c.ScheduleImage = ScheduleImage.None);
        await TickAsync(Kyiv(15, 9));
        Assert.Equal(ScheduleImage.None, ThreadOf(ChatA)!.ImageKind);

        channel.ScheduleImage = ScheduleImage.Card;
        _f.Db.SaveChanges();
        await TickAsync(Kyiv(15, 9, 1));
        Assert.Equal(ScheduleImage.Card, ThreadOf(ChatA)!.ImageKind);

        channel.ScheduleImage = ScheduleImage.Grid;
        _f.Db.SaveChanges();
        await TickAsync(Kyiv(15, 9, 2));
        Assert.Equal(ScheduleImage.Grid, ThreadOf(ChatA)!.ImageKind);
    }

    [Fact]
    public async Task Captions_NeverMatchThePowerOutTrigger()
    {
        AddChannel();
        var regex = new System.Text.RegularExpressions.Regex(@"світло\s+зникло", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var at in new[] { Kyiv(15, 9), Kyiv(15, 12), Kyiv(15, 14, 5), Kyiv(15, 21) })
        {
            await TickAsync(at);
        }

        var texts = _f.Bot.Photos.Select(p => p.Caption!).Concat(_f.Bot.CaptionEdits.Select(e => e.Caption!)).ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.DoesNotMatch(regex, t));
    }
}
