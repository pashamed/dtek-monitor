using System.Text.Json;
using DtekBot.Api.Data;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class ScheduleNotifierTests : IDisposable
{
    private const long ChatA = 801;
    private const long ChatB = 802;
    private const long ChatC = 803;
    private const string Version12 = "1991457b4408";
    private const string ImageBase = "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/images/kyiv/";
    private const string GridUrl = ImageBase + "gpv-45-1-emergency.png?v=" + Version12;
    private const string CardUrl = ImageBase + "gpv-45-1-summary.png?v=" + Version12;

    private const string OffText12 = "🔴 11:00 Світло вимкнено за графіком\n🗓 Очікуємо за графіком о 14:00";
    private const string OffText21 = "🔴 20:30 Світло вимкнено за графіком\n🕓 Воно було 6год 30хв\n🗓 Очікуємо за графіком о 00:00 (16.10)";
    private const string OnText1405 =
        "🟢 14:00 Світло з'явилося за графіком\n🕓 Його не було 3год\n🗓 Наступне відключення за графіком о 20:30";

    private const string PostText12 =
        "❗️ Графік відключень на сьогодні\n📅 Четвер 15.10.2026\n\n🔴 11:00 - 14:00 (~3 год)\n🔴 20:30 - 24:00 (~3,5 год)";

    private static readonly long Outage1Start = Kyiv(15, 11).ToUnixTimeSeconds();
    private static readonly long Outage2Start = Kyiv(15, 20, 30).ToUnixTimeSeconds();

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

    // Event messages (light off/on) for the group 45.1; the model defaults apply unless configured.
    private Channel AddChannel(long chatId = ChatA, Action<Channel>? configure = null, string regionId = "kyiv") =>
        _f.AddChannel(chatId, c =>
        {
            c.GroupId = "45.1";
            c.ScheduleNotify = true;
            configure?.Invoke(c);
        }, regionId);

    // Schedule post only.
    private Channel AddPostChannel(long chatId = ChatA, Action<Channel>? configure = null, string regionId = "kyiv") =>
        _f.AddChannel(chatId, c =>
        {
            c.GroupId = "45.1";
            c.SchedulePost = true;
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

    private SchedulePostThread? PostOf(long chatId) =>
        _f.Db.SchedulePostThreads.AsNoTracking().SingleOrDefault(t => t.ChatId == chatId);

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

    private static ScheduleSnapshot LaterEnd(ScheduleSnapshot snapshot) =>
        WithOutages(snapshot, "45.1", Out(Kyiv(15, 11), Kyiv(15, 15)), Out(Kyiv(15, 20, 30), Kyiv(16, 0)));

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

    private static string TemplatesJson(params (string Slot, string Text)[] templates) =>
        JsonSerializer.Serialize(templates.ToDictionary(t => t.Slot, t => t.Text));

    // ================= event messages: eligibility =================

    [Theory]
    [InlineData("off")]
    [InlineData("paused")]
    [InlineData("inactive")]
    [InlineData("nogroup")]
    public async Task IneligibleChannels_GetNothing(string reason)
    {
        AddChannel(configure: c =>
        {
            c.SchedulePost = true;
            switch (reason)
            {
                case "off": c.ScheduleNotify = false; c.SchedulePost = false; break;
                case "paused": c.IsPaused = true; break;
                case "inactive": c.IsActive = false; break;
                case "nogroup": c.GroupId = null; break;
            }
        });

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(0, 0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.Empty(_schedules.Calls);
        Assert.Null(ThreadOf(ChatA));
        Assert.Null(PostOf(ChatA));
    }

    [Fact]
    public async Task NoChannelsAtAll_IsAnEmptyTick()
    {
        Assert.Equal(new ScheduleTickSummary(0, 0, 0, 0, 0), await TickAsync());
    }

    [Fact]
    public async Task DisabledScheduleEventNotifications_DoNotSendForLegacyChannelSettings()
    {
        _f.Settings.Schedules.EnableEventNotifications = false;
        AddChannel();
        _f.Db.ScheduleThreads.Add(new ScheduleThread
        {
            ChatId = ChatA,
            RegionId = "kyiv",
            GroupId = "45.1",
            MessageId = 1,
            Phase = SchedulePhase.Off,
            EpisodeStart = Outage1Start,
            ContentHash = "legacy",
        });
        await _f.Db.SaveChangesAsync();

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(0, 0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.Null(ThreadOf(ChatA));
    }

    [Fact]
    public async Task ModelDefaults_AreTextOnlyEvents_InEditMode_AndAGridPostInNewMode()
    {
        var channel = new Channel();

        Assert.Equal(ScheduleImage.None, channel.ScheduleImage);
        Assert.Equal(PostMode.Edit, channel.ScheduleEventMode);
        Assert.Equal(PostMode.New, channel.SchedulePostMode);
        Assert.Equal(ScheduleImage.Grid, channel.SchedulePostImage);
        Assert.False(channel.ScheduleNotify);
        Assert.False(channel.SchedulePost);
        await Task.CompletedTask;
    }

    // ================= event messages: episodes =================

    [Fact]
    public async Task PowerOnWithoutAnAnnouncedOutage_IsSkipped_NothingIsSent()
    {
        AddChannel();

        var summary = await TickAsync(Kyiv(15, 9));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.Null(ThreadOf(ChatA));
    }

    [Fact]
    public async Task PowerOnBetweenOutages_WithoutAThread_IsSkipped()
    {
        AddChannel();

        var summary = await TickAsync(Kyiv(15, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task OutageStart_PostsATextMessage_AndStartsAnEpisode()
    {
        AddChannel();

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Photos);
        var message = Assert.Single(_f.Bot.Sent);
        Assert.Equal(ChatA, message.ChatId.Identifier);
        Assert.Equal(OffText12, message.Text);
        Assert.Equal(ParseMode.Html, message.ParseMode);

        var thread = ThreadOf(ChatA)!;
        Assert.Equal(SchedulePhase.Off, thread.Phase);
        Assert.Equal(Outage1Start, thread.EpisodeStart);
        Assert.Equal("kyiv", thread.RegionId);
        Assert.Equal("45.1", thread.GroupId);
        Assert.False(thread.IsPhoto);
        Assert.Equal(ScheduleImage.None, thread.ImageKind);
        Assert.Equal(RealVersion, thread.ImageVersion);
        Assert.True(thread.MessageId >= 1000);
        Assert.False(string.IsNullOrEmpty(thread.ContentHash));
        Assert.Equal(_clock.Now, thread.UpdatedAt);
    }

    [Fact]
    public async Task OutageStart_WithAPictureKind_PostsAPhotoWithTheTextAsCaption()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Grid);

        await TickAsync(Kyiv(15, 12));

        var photo = Assert.Single(_f.Bot.Photos);
        Assert.Equal(GridUrl, PhotoUrl(photo));
        Assert.Equal(OffText12, photo.Caption);
        Assert.Equal(ParseMode.Html, photo.ParseMode);
        Assert.Empty(_f.Bot.Sent);
        var thread = ThreadOf(ChatA)!;
        Assert.True(thread.IsPhoto);
        Assert.Equal(ScheduleImage.Grid, thread.ImageKind);
    }

    [Fact]
    public async Task CardKind_PostsTheSummaryPicture()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Card);

        await TickAsync(Kyiv(15, 12));

        Assert.Equal(CardUrl, PhotoUrl(Assert.Single(_f.Bot.Photos)));
        Assert.Equal(ScheduleImage.Card, ThreadOf(ChatA)!.ImageKind);
    }

    [Fact]
    public async Task EveningOutage_ShowsHowLongThePowerWas_AndTheNextDayReturn()
    {
        AddChannel();

        await TickAsync(Kyiv(15, 21));

        Assert.Equal(OffText21, Assert.Single(_f.Bot.Sent).Text);
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
    public async Task EditMode_ChangedText_EditsTheMessageText()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        var thread = ThreadOf(ChatA)!;
        _schedules.Snapshots["kyiv"] = LaterEnd(RealSnapshot());

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        var edit = Assert.Single(_f.Bot.Edits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Equal("🔴 11:00 Світло вимкнено за графіком\n🗓 Очікуємо за графіком о 15:00", edit.Text);
        Assert.Equal(ParseMode.Html, edit.ParseMode);
        Assert.Single(_f.Bot.Sent);
        Assert.Equal(thread.MessageId, ThreadOf(ChatA)!.MessageId);
    }

    [Fact]
    public async Task EditMode_ChangedText_OnAPhoto_EditsTheCaptionOnly()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Grid);
        await TickAsync(Kyiv(15, 12));
        var thread = ThreadOf(ChatA)!;
        _schedules.Snapshots["kyiv"] = LaterEnd(RealSnapshot());

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        var edit = Assert.Single(_f.Bot.CaptionEdits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Contains("о 15:00", edit.Caption);
        Assert.Equal(ParseMode.Html, edit.ParseMode);
        Assert.Empty(_f.Bot.MediaEdits);
    }

    [Fact]
    public async Task EditMode_ChangedScheduleVersion_EditsTheMedia_WithNewUrl()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Grid);
        await TickAsync(Kyiv(15, 12));
        var thread = ThreadOf(ChatA)!;
        _schedules.Snapshots["kyiv"] = WithVersion(RealSnapshot(), "newversion123456789");

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        var edit = Assert.Single(_f.Bot.MediaEdits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Equal(ImageBase + "gpv-45-1-emergency.png?v=newversion12", MediaUrl(edit));
        Assert.Equal(OffText12, MediaCaption(edit));
        Assert.Equal(ParseMode.Html, ((InputMediaPhoto)edit.Media).ParseMode);
        Assert.Empty(_f.Bot.CaptionEdits);
        Assert.Equal("newversion123456789", ThreadOf(ChatA)!.ImageVersion);
    }

    [Fact]
    public async Task EditMode_PowerBack_EditsTheSameMessage_ToTheRestoredText()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        var thread = ThreadOf(ChatA)!;

        var summary = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        var edit = Assert.Single(_f.Bot.Edits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Equal(OnText1405, edit.Text);
        Assert.Single(_f.Bot.Sent);

        var updated = ThreadOf(ChatA)!;
        Assert.Equal(SchedulePhase.On, updated.Phase);
        Assert.Equal(Outage1Start, updated.EpisodeStart);
        Assert.Equal(thread.MessageId, updated.MessageId);
    }

    [Fact]
    public async Task EditMode_AfterPowerIsBack_LaterTicksAreSkipped()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        await TickAsync(Kyiv(15, 14, 5));

        var later = await TickAsync(Kyiv(15, 14, 20));
        var evening = await TickAsync(Kyiv(15, 19));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), later);
        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), evening);
        Assert.Single(_f.Bot.Edits);
    }

    [Fact]
    public async Task EditMode_NextOutage_StartsAFreshEpisode_WithANewMessage_AndKeepsTheOldOne()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        await TickAsync(Kyiv(15, 14, 5));
        var restored = ThreadOf(ChatA)!;

        var summary = await TickAsync(Kyiv(15, 21));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Sent.Count());
        Assert.Empty(_f.Bot.Deletes);
        var thread = ThreadOf(ChatA)!;
        Assert.Equal(SchedulePhase.Off, thread.Phase);
        Assert.Equal(Outage2Start, thread.EpisodeStart);
        Assert.NotEqual(restored.MessageId, thread.MessageId);
    }

    // ================= event messages: modes =================

    [Fact]
    public async Task NewMode_EveryPhaseChange_IsANewMessage_AndNothingIsEditedOrDeleted()
    {
        AddChannel(configure: c => c.ScheduleEventMode = PostMode.New);

        var outage = await TickAsync(Kyiv(15, 12));
        var same = await TickAsync(Kyiv(15, 12, 15));
        var back = await TickAsync(Kyiv(15, 14, 5));
        var later = await TickAsync(Kyiv(15, 14, 20));
        var next = await TickAsync(Kyiv(15, 21));

        Assert.Equal(1, outage.Sent);
        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), same);
        Assert.Equal(1, back.Sent);
        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), later);
        Assert.Equal(1, next.Sent);

        var texts = _f.Bot.Sent.Select(s => s.Text).ToList();
        Assert.Equal([OffText12, OnText1405, OffText21], texts);
        Assert.Empty(_f.Bot.Edits);
        Assert.Empty(_f.Bot.Deletes);
        Assert.Equal(Outage2Start, ThreadOf(ChatA)!.EpisodeStart);
    }

    [Fact]
    public async Task NewMode_ChangedDataInTheSameEpisode_IsNotEdited()
    {
        AddChannel(configure: c => c.ScheduleEventMode = PostMode.New);
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["kyiv"] = WithVersion(LaterEnd(RealSnapshot()), "other");

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Requests);
    }

    [Fact]
    public async Task ReplaceDeleteMode_SendsTheNewMessageFirst_ThenDeletesTheOldOne()
    {
        AddChannel(configure: c => c.ScheduleEventMode = PostMode.ReplaceDelete);
        await TickAsync(Kyiv(15, 12));
        var first = ThreadOf(ChatA)!;
        Assert.Empty(_f.Bot.Deletes);

        var summary = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        var delete = Assert.Single(_f.Bot.Deletes);
        Assert.Equal(ChatA, delete.ChatId.Identifier);
        Assert.Equal(first.MessageId, delete.MessageId);

        var sendIndex = _f.Bot.Requests.FindLastIndex(r => r is SendMessageRequest);
        var deleteIndex = _f.Bot.Requests.FindIndex(r => r is DeleteMessageRequest);
        Assert.True(sendIndex < deleteIndex, "the new message must be sent before the old one is deleted");

        var thread = ThreadOf(ChatA)!;
        Assert.NotEqual(first.MessageId, thread.MessageId);
        Assert.Equal(SchedulePhase.On, thread.Phase);
        Assert.Equal(OnText1405, _f.Bot.Sent.Last().Text);
    }

    [Fact]
    public async Task ReplaceDeleteMode_NextEpisodeReplacesThePreviousMessageToo()
    {
        AddChannel(configure: c => c.ScheduleEventMode = PostMode.ReplaceDelete);
        await TickAsync(Kyiv(15, 12));
        await TickAsync(Kyiv(15, 14, 5));
        var restored = ThreadOf(ChatA)!;

        await TickAsync(Kyiv(15, 21));

        Assert.Equal(2, _f.Bot.Deletes.Count());
        Assert.Equal(restored.MessageId, _f.Bot.Deletes.Last().MessageId);
    }

    [Fact]
    public async Task ReplaceDeleteMode_SameEpisodeChangedData_IsLeftAlone()
    {
        AddChannel(configure: c => c.ScheduleEventMode = PostMode.ReplaceDelete);
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["kyiv"] = LaterEnd(RealSnapshot());

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(1, summary.Skipped);
        Assert.Empty(_f.Bot.Edits);
        Assert.Empty(_f.Bot.Deletes);
    }

    [Fact]
    public async Task ReplaceDeleteMode_DeleteFailure_IsIgnored_AndTheThreadStillMovesOn()
    {
        AddChannel(configure: c => c.ScheduleEventMode = PostMode.ReplaceDelete);
        await TickAsync(Kyiv(15, 12));
        var first = ThreadOf(ChatA)!;
        _f.Bot.Failure = r => r is DeleteMessageRequest
            ? new ApiRequestException("Bad Request: message can't be deleted for everyone", 400)
            : null;

        var summary = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Single(_f.Bot.Deletes);
        Assert.NotEqual(first.MessageId, ThreadOf(ChatA)!.MessageId);
        Assert.True(_f.LoadChannel(ChatA).IsActive);
    }

    [Fact]
    public async Task ReplaceDeleteMode_Delete403_DoesNotDeactivateTheChannel()
    {
        AddChannel(configure: c => c.ScheduleEventMode = PostMode.ReplaceDelete);
        await TickAsync(Kyiv(15, 12));
        _f.Bot.Failure = r => r is DeleteMessageRequest ? FakeTelegramBotClient.Forbidden() : null;

        var summary = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(1, summary.Sent);
        Assert.Equal(0, summary.Failed);
        Assert.True(_f.LoadChannel(ChatA).IsActive);
    }

    // ================= event messages: pictures =================

    [Fact]
    public async Task EditMode_FlippingTextToPicture_PostsANewMessage_AndDeletesTheOldOne()
    {
        var channel = AddChannel();
        await TickAsync(Kyiv(15, 12));
        var old = ThreadOf(ChatA)!;

        channel.ScheduleImage = ScheduleImage.Grid;
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(GridUrl, PhotoUrl(Assert.Single(_f.Bot.Photos)));
        Assert.Equal(old.MessageId, Assert.Single(_f.Bot.Deletes).MessageId);
        Assert.Empty(_f.Bot.Edits);

        var thread = ThreadOf(ChatA)!;
        Assert.NotEqual(old.MessageId, thread.MessageId);
        Assert.True(thread.IsPhoto);
        Assert.Equal(ScheduleImage.Grid, thread.ImageKind);
        Assert.Equal(Outage1Start, thread.EpisodeStart);
    }

    [Fact]
    public async Task EditMode_FlippingPictureToText_PostsANewTextMessage_AndDeletesTheOldOne()
    {
        var channel = AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Card);
        await TickAsync(Kyiv(15, 12));
        var old = ThreadOf(ChatA)!;

        channel.ScheduleImage = ScheduleImage.None;
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Photos.Count() + _f.Bot.Sent.Count());
        Assert.Equal(old.MessageId, Assert.Single(_f.Bot.Deletes).MessageId);
        var thread = ThreadOf(ChatA)!;
        Assert.False(thread.IsPhoto);
        Assert.Equal(ScheduleImage.None, thread.ImageKind);
    }

    [Fact]
    public async Task NewMode_FlippingPictureMode_IsNotActedOnWithinTheEpisode()
    {
        var channel = AddChannel(configure: c => c.ScheduleEventMode = PostMode.New);
        await TickAsync(Kyiv(15, 12));

        channel.ScheduleImage = ScheduleImage.Grid;
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Empty(_f.Bot.Deletes);
        Assert.Empty(_f.Bot.Photos);
    }

    [Theory]
    [InlineData(ScheduleImage.Grid, ScheduleImage.Card, CardUrl)]
    [InlineData(ScheduleImage.Card, ScheduleImage.Grid, GridUrl)]
    public async Task EditMode_SwitchingGridAndCard_EditsTheMedia_EvenWithTheSameDataVersion(
        ScheduleImage from, ScheduleImage to, string expectedUrl)
    {
        var channel = AddChannel(configure: c => c.ScheduleImage = from);
        await TickAsync(Kyiv(15, 12));
        var thread = ThreadOf(ChatA)!;

        channel.ScheduleImage = to;
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        var edit = Assert.Single(_f.Bot.MediaEdits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Equal(expectedUrl, MediaUrl(edit));
        Assert.Single(_f.Bot.Photos);
        Assert.Equal(to, ThreadOf(ChatA)!.ImageKind);
        Assert.Equal(1, (await TickAsync(Kyiv(15, 12, 6))).Skipped);
    }

    [Fact]
    public async Task PhotoRejected_FallsBackToText_AndLaterEditsUseEditMessageText()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Grid);
        _f.Bot.Failure = r => r is SendPhotoRequest
            ? new ApiRequestException("Bad Request: wrong type of the web page content", 400)
            : null;

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Single(_f.Bot.Photos);
        var text = Assert.Single(_f.Bot.Sent);
        Assert.Equal(OffText12, text.Text);
        var thread = ThreadOf(ChatA)!;
        Assert.False(thread.IsPhoto);
        Assert.Equal(ScheduleImage.Grid, thread.ImageKind);

        _schedules.Snapshots["kyiv"] = LaterEnd(RealSnapshot());
        await TickAsync(Kyiv(15, 12, 15));

        var edit = Assert.Single(_f.Bot.Edits);
        Assert.Equal(thread.MessageId, edit.MessageId);
        Assert.Empty(_f.Bot.CaptionEdits);
        Assert.Empty(_f.Bot.MediaEdits);
    }

    [Fact]
    public async Task TextOnlyFallback_DoesNotRepostOnLaterTicks()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Grid);
        _f.Bot.Failure = r => r is SendPhotoRequest
            ? new ApiRequestException("Bad Request: wrong type of the web page content", 400)
            : null;
        await TickAsync(Kyiv(15, 12));

        var second = await TickAsync(Kyiv(15, 12, 5));
        var third = await TickAsync(Kyiv(15, 12, 10));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), second);
        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), third);
        Assert.Single(_f.Bot.Photos);
        Assert.Single(_f.Bot.Sent);
    }

    [Fact]
    public async Task PhotoRejected_ButChatNotFound_IsNotAFallback_ItDeactivatesTheChannel()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Grid);
        _f.Bot.Failure = r => r is SendPhotoRequest ? new ApiRequestException("Bad Request: chat not found", 400) : null;

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 0, 1), summary);
        Assert.Empty(_f.Bot.Sent);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
        Assert.Null(ThreadOf(ChatA));
    }

    [Fact]
    public async Task MediaEditRejected_FallsBackToACaptionEdit()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Grid);
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

    // ================= event messages: edit failures =================

    [Theory]
    [InlineData("Bad Request: message to edit not found")]
    [InlineData("Bad Request: message can't be edited")]
    public async Task CaptionEditOfAMissingMessage_PostsANewOne(string error)
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Grid);
        await TickAsync(Kyiv(15, 12));
        var old = ThreadOf(ChatA)!;
        _f.Bot.Failure = r => r is EditMessageCaptionRequest ? new ApiRequestException(error, 400) : null;

        var summary = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Photos.Count());
        var thread = ThreadOf(ChatA)!;
        Assert.NotEqual(old.MessageId, thread.MessageId);
        Assert.Equal(SchedulePhase.On, thread.Phase);
        Assert.Contains("з'явилося", _f.Bot.Photos.Last().Caption);
    }

    [Fact]
    public async Task TextEditOfAMissingMessage_PostsANewOne()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        var old = ThreadOf(ChatA)!;
        _f.Bot.Failure = r => r is EditMessageTextRequest ? new ApiRequestException("Bad Request: message to edit not found", 400) : null;

        var summary = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.NotEqual(old.MessageId, ThreadOf(ChatA)!.MessageId);
    }

    [Fact]
    public async Task MediaEditOfAMissingMessage_PostsANewOne()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Grid);
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
    }

    [Fact]
    public async Task MessageNotModified_OnTextEdit_IsTreatedAsSuccess()
    {
        AddChannel();
        await TickAsync(Kyiv(15, 12));
        _f.Bot.Failure = r => r is EditMessageTextRequest ? FakeTelegramBotClient.NotModified() : null;

        var summary = await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        Assert.Equal(SchedulePhase.On, ThreadOf(ChatA)!.Phase);
        _f.Bot.Failure = null;
        Assert.Equal(1, (await TickAsync(Kyiv(15, 14, 6))).Skipped);
    }

    [Fact]
    public async Task MessageNotModified_OnMediaEdit_IsTreatedAsSuccess_WithoutACaptionEdit()
    {
        AddChannel(configure: c => c.ScheduleImage = ScheduleImage.Grid);
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["kyiv"] = WithVersion(RealSnapshot(), "notmodified123");
        _f.Bot.Failure = r => r is EditMessageMediaRequest ? FakeTelegramBotClient.NotModified() : null;

        var summary = await TickAsync(Kyiv(15, 12, 15));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        Assert.Empty(_f.Bot.CaptionEdits);
        Assert.Equal("notmodified123", ThreadOf(ChatA)!.ImageVersion);
    }

    // ================= event messages: Telegram failures =================

    [Fact]
    public async Task Telegram403_OnSend_DeactivatesTheChannel_AndCountsFailed()
    {
        AddChannel();
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var summary = await TickAsync(Kyiv(15, 12));

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
    public async Task Telegram403_RemovesBothThreadKinds_ForAChatWithBothFeatures()
    {
        AddChannel(configure: c => c.SchedulePost = true);
        await TickAsync(Kyiv(15, 12));
        Assert.NotNull(ThreadOf(ChatA));
        Assert.NotNull(PostOf(ChatA));
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        await TickAsync(Kyiv(15, 14, 5));

        Assert.False(_f.LoadChannel(ChatA).IsActive);
        Assert.Null(ThreadOf(ChatA));
        Assert.Null(PostOf(ChatA));
    }

    [Fact]
    public async Task Telegram403ForOneChat_DoesNotStopTheOthers()
    {
        AddChannel(ChatA);
        AddChannel(ChatB);
        _f.Bot.Failure = r => r is SendMessageRequest { ChatId.Identifier: ChatA } ? FakeTelegramBotClient.Forbidden() : null;

        var summary = await TickAsync(Kyiv(15, 12));

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

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 0, 1), summary);
        Assert.True(_f.LoadChannel(ChatA).IsActive);
        Assert.Null(ThreadOf(ChatA));

        _f.Bot.Failure = null;
        Assert.Equal(1, (await TickAsync(Kyiv(15, 12, 1))).Sent);
    }

    [Fact]
    public async Task ScheduleReminder_SendsAtConfiguredLeadTimeOnlyOnce()
    {
        AddChannel(configure: c =>
        {
            c.ScheduleNotify = false;
            c.ScheduleReminder = true;
            c.ScheduleReminderMinutes = 15;
            c.Templates = TemplatesJson((MessageSlots.BeforeOff, "Черга {group}: {time} за {minutes} хв"));
        });

        var first = await TickAsync(Kyiv(15, 10, 50));
        var duplicate = await TickAsync(Kyiv(15, 10, 55));

        Assert.Equal(1, first.Sent);
        Assert.Equal(1, duplicate.Skipped);
        Assert.Equal("Черга 45.1: 11:00 за 15 хв", Assert.Single(_f.Bot.Sent).Text);
        Assert.Single(_f.Db.ScheduleReminderDeliveries);
    }

    [Fact]
    public async Task ScheduleReminder_RetriesAfterSendFailure()
    {
        AddChannel(configure: c => { c.ScheduleNotify = false; c.ScheduleReminder = true; });
        _f.Bot.Failure = _ => new HttpRequestException("network down");

        Assert.Equal(1, (await TickAsync(Kyiv(15, 10, 50))).Failed);
        Assert.Empty(_f.Db.ScheduleReminderDeliveries);

        _f.Bot.Failure = null;
        Assert.Equal(1, (await TickAsync(Kyiv(15, 10, 55))).Sent);
        Assert.Single(_f.Db.ScheduleReminderDeliveries);
    }

    [Fact]
    public async Task SendScheduleNow_PublishesTheCurrentScheduleEvenWhenAutomaticPostsAreOff()
    {
        var channel = AddPostChannel(configure: c => c.SchedulePost = false);

        var result = await _notifier.SendScheduleNowAsync(channel, CancellationToken.None);

        Assert.True(result.Sent);
        Assert.Contains("надіслано", result.Message);
        Assert.NotNull(PostOf(ChatA));
        Assert.Single(_f.Bot.Photos);
    }

    [Fact]
    public async Task RateLimit429_IsRetriedOnce()
    {
        AddChannel();
        var calls = 0;
        _f.Bot.Failure = r => r is SendMessageRequest && calls++ == 0
            ? new ApiRequestException("Too Many Requests: retry after 0", 429, new ResponseParameters { RetryAfter = 0 })
            : null;

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Sent.Count());
        Assert.NotNull(ThreadOf(ChatA));
    }

    [Fact]
    public async Task RateLimit429_TwiceInARow_IsFailed_AndKeepsTheChannel()
    {
        AddChannel();
        _f.Bot.Failure = r => r is SendMessageRequest
            ? new ApiRequestException("Too Many Requests: retry after 0", 429, new ResponseParameters { RetryAfter = 0 })
            : null;

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 0, 1), summary);
        Assert.True(_f.LoadChannel(ChatA).IsActive);
    }

    [Fact]
    public async Task MarkupRejected_RetriesAsPlainText_WithoutTags()
    {
        AddChannel(configure: c => c.Templates = TemplatesJson((MessageSlots.Off, "<b>{time}</b> &amp; x")));
        var calls = 0;
        _f.Bot.Failure = r => r is SendMessageRequest && calls++ == 0 ? FakeTelegramBotClient.CantParseEntities() : null;

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(1, summary.Sent);
        var requests = _f.Bot.Sent.ToList();
        Assert.Equal(2, requests.Count);
        Assert.Equal("<b>11:00</b> &amp; x", requests[0].Text);
        Assert.Equal(ParseMode.Html, requests[0].ParseMode);
        Assert.Equal("11:00 & x", requests[1].Text);
        Assert.Equal(ParseMode.None, requests[1].ParseMode);
    }

    // ================= event messages: templates =================

    [Fact]
    public async Task CustomOffAndOnTemplates_AreUsed()
    {
        AddChannel(configure: c => c.Templates = TemplatesJson(
            (MessageSlots.Off, "OFF {time} {group} {region}"),
            (MessageSlots.On, "ON {time} {offDuration}")));

        await TickAsync(Kyiv(15, 12));
        await TickAsync(Kyiv(15, 14, 5));

        Assert.Equal("OFF 11:00 45.1 м. Київ", Assert.Single(_f.Bot.Sent).Text);
        Assert.Equal("ON 14:00 3год", Assert.Single(_f.Bot.Edits).Text);
    }

    [Fact]
    public async Task LegacySingleTemplate_IsNotUsedForOffAndOnMessages()
    {
        AddChannel(configure: c => c.Template = "LEGACY {message}");

        await TickAsync(Kyiv(15, 12));

        Assert.Equal(OffText12, Assert.Single(_f.Bot.Sent).Text);
    }

    // ================= event messages: midnight =================

    [Fact]
    public async Task OutageStraddlingMidnight_KeepsItsEpisode_WhenTheDataWindowRollsForward()
    {
        AddChannel();
        _schedules.Snapshots["kyiv"] = EveningOutageSnapshot("v1");
        await TickAsync(Kyiv(15, 22));
        var thread = ThreadOf(ChatA)!;
        Assert.Equal(SchedulePhase.Off, thread.Phase);
        Assert.Equal(Kyiv(15, 20).ToUnixTimeSeconds(), thread.EpisodeStart);
        Assert.Equal("🔴 20:00 Світло вимкнено за графіком\n🗓 Очікуємо за графіком о 03:00 (16.10)", Assert.Single(_f.Bot.Sent).Text);

        // After midnight the feed starts at today: the outage now touches the start of the data.
        _schedules.Snapshots["kyiv"] = NextDaySnapshot("v2");
        var summary = await TickAsync(Kyiv(16, 0, 30));

        Assert.Equal(0, summary.Sent);
        Assert.Equal(1, summary.Edited);
        Assert.Single(_f.Bot.Sent);
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
        Assert.Equal("🟢 03:00 Світло з'явилося за графіком\n🕓 Його не було 7год", _f.Bot.Edits.Last().Text);
    }

    [Fact]
    public async Task OutageAlreadyRunningAtTheStartOfTheData_WithoutAThread_UsesTheCoverageStartAsEpisode()
    {
        AddChannel();
        _schedules.Snapshots["kyiv"] = NextDaySnapshot("v2");

        var first = await TickAsync(Kyiv(16, 0, 30));

        Assert.Equal(1, first.Sent);
        Assert.Equal(Kyiv(16, 0).ToUnixTimeSeconds(), ThreadOf(ChatA)!.EpisodeStart);
    }

    [Fact]
    public async Task OutageWithUnknownStart_IsNotRewrittenOnEveryTick()
    {
        AddChannel();
        _schedules.Snapshots["kyiv"] = EveningOutageSnapshot("v1");
        await TickAsync(Kyiv(15, 22));
        _schedules.Snapshots["kyiv"] = NextDaySnapshot("v2");
        await TickAsync(Kyiv(16, 0, 30));

        var next = await TickAsync(Kyiv(16, 0, 45));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), next);
    }

    // ================= event messages: lifecycle and providers =================

    [Fact]
    public async Task TurningNotificationsOff_RemovesTheThread_OnTheNextTick()
    {
        var channel = AddChannel();
        await TickAsync(Kyiv(15, 12));
        Assert.NotNull(ThreadOf(ChatA));

        channel.ScheduleNotify = false;
        _f.Db.SaveChanges();
        _f.Bot.Requests.Clear();
        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(new ScheduleTickSummary(0, 0, 0, 0, 0), summary);
        Assert.Null(ThreadOf(ChatA));
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task TurningNotificationsOff_KeepsTheSchedulePost_WhenThePostIsStillOn()
    {
        var channel = AddChannel(configure: c => c.SchedulePost = true);
        await TickAsync(Kyiv(15, 12));
        Assert.NotNull(ThreadOf(ChatA));
        Assert.NotNull(PostOf(ChatA));

        channel.ScheduleNotify = false;
        _f.Db.SaveChanges();
        await TickAsync(Kyiv(15, 12, 5));

        Assert.Null(ThreadOf(ChatA));
        Assert.NotNull(PostOf(ChatA));
    }

    [Fact]
    public async Task TurningTheSchedulePostOff_RemovesThePostThread_ButKeepsTheEventThread()
    {
        var channel = AddChannel(configure: c => c.SchedulePost = true);
        await TickAsync(Kyiv(15, 12));

        channel.SchedulePost = false;
        _f.Db.SaveChanges();
        await TickAsync(Kyiv(15, 12, 5));

        Assert.NotNull(ThreadOf(ChatA));
        Assert.Null(PostOf(ChatA));
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("inactive")]
    [InlineData("nogroup")]
    public async Task ThreadsOfAChannelThatBecameIneligible_AreRemoved(string reason)
    {
        var channel = AddChannel(configure: c => c.SchedulePost = true);
        await TickAsync(Kyiv(15, 12));

        switch (reason)
        {
            case "paused": channel.IsPaused = true; break;
            case "inactive": channel.IsActive = false; break;
            case "nogroup": channel.GroupId = null; break;
        }

        _f.Db.SaveChanges();
        _f.Bot.Requests.Clear();
        await TickAsync(Kyiv(15, 12, 5));

        Assert.Null(ThreadOf(ChatA));
        Assert.Null(PostOf(ChatA));
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task ChangingTheGroup_DropsBothThreads_AndStartsFresh()
    {
        _schedules.Snapshots["kyiv"] = Parse(Json("v1", "15.10.2026 10:00",
            (Day1, "45.1", Hours("no", 12)),
            (Day1, "1.1", Hours("no", 13)),
            (Day2, "45.1", []),
            (Day2, "1.1", [])))!;
        var channel = AddChannel(configure: c => c.SchedulePost = true);
        await TickAsync(Kyiv(15, 11, 30));
        var oldThread = ThreadOf(ChatA)!;
        var oldPost = PostOf(ChatA)!;
        Assert.Equal("45.1", oldThread.GroupId);

        channel.GroupId = "1.1";
        _f.Db.SaveChanges();
        await TickAsync(Kyiv(15, 12, 30));

        var thread = Assert.Single(_f.Db.ScheduleThreads.AsNoTracking());
        var post = Assert.Single(_f.Db.SchedulePostThreads.AsNoTracking());
        Assert.Equal("1.1", thread.GroupId);
        Assert.Equal("1.1", post.GroupId);
        Assert.NotEqual(oldThread.MessageId, thread.MessageId);
        Assert.NotEqual(oldPost.MessageId, post.MessageId);
        Assert.Equal(Kyiv(15, 12).ToUnixTimeSeconds(), thread.EpisodeStart);
    }

    [Fact]
    public async Task ChangingTheRegion_DropsTheThreads()
    {
        var channel = AddChannel(configure: c => c.SchedulePost = true);
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["odesa"] = null;

        channel.RegionId = "odesa";
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 5));

        Assert.Equal(1, summary.Skipped);
        Assert.Null(ThreadOf(ChatA));
        Assert.Null(PostOf(ChatA));
    }

    [Fact]
    public async Task RegionWithoutData_SkipsAllItsChats_AndSendsNothing()
    {
        _schedules.Snapshots["kyiv"] = null;
        AddChannel(ChatA);
        AddChannel(ChatB);

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(2, 0, 0, 2, 0), summary);
        Assert.Empty(_f.Bot.Requests);
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
        Assert.Equal(thread.MessageId, ThreadOf(ChatA)!.MessageId);
        Assert.Equal(SchedulePhase.Off, ThreadOf(ChatA)!.Phase);
    }

    [Fact]
    public async Task GroupMissingFromTheSnapshot_IsSkipped()
    {
        AddChannel(configure: c => { c.GroupId = "99.9"; c.SchedulePost = true; });

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(0, summary.Sent);
        Assert.Equal(2, summary.Skipped);
        Assert.Empty(_f.Bot.Requests);
    }

    [Theory]
    [InlineData(14, 23, 59)]
    [InlineData(17, 0, 0)]
    [InlineData(20, 12, 0)]
    public async Task OutsideTheCoverageOfTheData_IsSkipped(int day, int hour, int minute)
    {
        AddChannel(configure: c => c.SchedulePost = true);

        var summary = await TickAsync(Kyiv(day, hour, minute));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 2, 0), summary);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task TwoChatsInTheSameRegion_BothGetMessages_WithOneProviderCall()
    {
        AddChannel(ChatA);
        AddChannel(ChatB);

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(2, 2, 0, 0, 0), summary);
        Assert.Equal(["kyiv"], _schedules.Calls);
        Assert.NotEqual(ThreadOf(ChatA)!.MessageId, ThreadOf(ChatB)!.MessageId);
    }

    [Fact]
    public async Task ChatsOfDifferentRegions_OneProviderCallPerRegion_AndEachRegionIsIndependent()
    {
        _schedules.Snapshots["odesa"] = null;
        AddChannel(ChatA);
        AddChannel(ChatB);
        AddChannel(ChatC, regionId: "odesa");

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(3, 2, 0, 1, 0), summary);
        Assert.Equal(["kyiv", "odesa"], _schedules.Calls.OrderBy(c => c));
        Assert.Null(ThreadOf(ChatC));
    }

    [Fact]
    public async Task ChatsWithDifferentGroups_GetTheirOwnSchedules_Pictures()
    {
        AddChannel(ChatA, c => c.ScheduleImage = ScheduleImage.Grid);
        AddChannel(ChatB, c => { c.GroupId = "1.1"; c.ScheduleImage = ScheduleImage.Grid; });

        await TickAsync(Kyiv(15, 12));

        Assert.Contains("gpv-45-1-emergency.png", PhotoUrl(_f.Bot.Photos.Single(p => p.ChatId.Identifier == ChatA)));
        var other = _f.Bot.Photos.SingleOrDefault(p => p.ChatId.Identifier == ChatB);
        if (other is not null)
        {
            Assert.Contains("gpv-1-1-emergency.png", PhotoUrl(other));
        }
    }

    [Fact]
    public async Task RegionOfAnotherDataFile_UsesItsOwnImageFolder()
    {
        _schedules.Snapshots["kyiv-oblast"] = RealSnapshot(dataFile: "kyiv-region");
        AddChannel(ChatA, c => c.ScheduleImage = ScheduleImage.Grid, "kyiv-oblast");

        await TickAsync(Kyiv(15, 12));

        Assert.Contains("/images/kyiv-region/gpv-45-1-emergency.png", PhotoUrl(Assert.Single(_f.Bot.Photos)));
    }

    // ================= schedule post =================

    [Fact]
    public async Task Post_FirstOfTheDay_UsesTheFirstHeading_AndAPhoto()
    {
        AddPostChannel();

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        var photo = Assert.Single(_f.Bot.Photos);
        Assert.Equal(GridUrl, PhotoUrl(photo));
        Assert.Equal(PostText12, photo.Caption);
        Assert.Equal(ParseMode.Html, photo.ParseMode);
        Assert.Equal(ChatA, photo.ChatId.Identifier);

        var post = PostOf(ChatA)!;
        Assert.Equal("kyiv", post.RegionId);
        Assert.Equal("45.1", post.GroupId);
        Assert.Equal("2026-10-15", post.DateKey);
        Assert.True(post.IsPhoto);
        Assert.Equal(ScheduleImage.Grid, post.ImageKind);
        Assert.Equal(RealVersion, post.ImageVersion);
        Assert.False(string.IsNullOrEmpty(post.ContentHash));
        Assert.True(post.MessageId >= 1000);
        Assert.Equal(_clock.Now, post.UpdatedAt);
        Assert.Null(ThreadOf(ChatA));
    }

    [Fact]
    public async Task Post_Unchanged_IsSkipped()
    {
        AddPostChannel();
        await TickAsync(Kyiv(15, 12));

        var summary = await TickAsync(Kyiv(15, 13));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Requests);
    }

    [Fact]
    public async Task Post_NewMode_ChangedOutages_PostsAnUpdatedMessage_AndLeavesTheOldOne()
    {
        AddPostChannel(configure: c => c.SchedulePostMode = PostMode.New);
        await TickAsync(Kyiv(15, 12));
        var first = PostOf(ChatA)!;
        _schedules.Snapshots["kyiv"] = LaterEnd(RealSnapshot());

        var summary = await TickAsync(Kyiv(15, 12, 30));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Photos.Count());
        var caption = _f.Bot.Photos.Last().Caption!;
        Assert.StartsWith("❗️ Оновлений графік відключень на сьогодні\n📅 Четвер 15.10.2026", caption);
        Assert.Contains("🔴 11:00 - 15:00 (~4 год)", caption);
        Assert.Empty(_f.Bot.Deletes);
        Assert.Empty(_f.Bot.CaptionEdits);
        Assert.NotEqual(first.MessageId, PostOf(ChatA)!.MessageId);
    }

    [Fact]
    public async Task Post_EditMode_ChangedOutages_EditsTheCaption_WithTheUpdatedHeading()
    {
        AddPostChannel(configure: c => c.SchedulePostMode = PostMode.Edit);
        await TickAsync(Kyiv(15, 12));
        var first = PostOf(ChatA)!;
        _schedules.Snapshots["kyiv"] = LaterEnd(RealSnapshot());

        var summary = await TickAsync(Kyiv(15, 12, 30));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        var edit = Assert.Single(_f.Bot.CaptionEdits);
        Assert.Equal(first.MessageId, edit.MessageId);
        Assert.StartsWith("❗️ Оновлений графік", edit.Caption);
        Assert.Single(_f.Bot.Photos);
        Assert.Equal(first.MessageId, PostOf(ChatA)!.MessageId);
    }

    [Fact]
    public async Task Post_EditMode_ChangedVersion_EditsTheMedia()
    {
        AddPostChannel(configure: c => c.SchedulePostMode = PostMode.Edit);
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["kyiv"] = WithVersion(LaterEnd(RealSnapshot()), "postversion1234");

        await TickAsync(Kyiv(15, 12, 30));

        var edit = Assert.Single(_f.Bot.MediaEdits);
        Assert.Equal(ImageBase + "gpv-45-1-emergency.png?v=postversion1", MediaUrl(edit));
        Assert.StartsWith("❗️ Оновлений графік", MediaCaption(edit));
    }

    [Fact]
    public async Task Post_EditMode_MessageGone_PostsAFreshOne()
    {
        AddPostChannel(configure: c => c.SchedulePostMode = PostMode.Edit);
        await TickAsync(Kyiv(15, 12));
        var first = PostOf(ChatA)!;
        _schedules.Snapshots["kyiv"] = LaterEnd(RealSnapshot());
        _f.Bot.Failure = r => r is EditMessageCaptionRequest
            ? new ApiRequestException("Bad Request: message to edit not found", 400)
            : null;

        var summary = await TickAsync(Kyiv(15, 12, 30));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Photos.Count());
        Assert.NotEqual(first.MessageId, PostOf(ChatA)!.MessageId);
    }

    [Fact]
    public async Task Post_EditMode_NotModified_IsSuccess()
    {
        AddPostChannel(configure: c => c.SchedulePostMode = PostMode.Edit);
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["kyiv"] = LaterEnd(RealSnapshot());
        _f.Bot.Failure = r => r is EditMessageCaptionRequest ? FakeTelegramBotClient.NotModified() : null;

        var summary = await TickAsync(Kyiv(15, 12, 30));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        Assert.Equal(1, (await TickAsync(Kyiv(15, 12, 31))).Skipped);
    }

    [Fact]
    public async Task Post_ReplaceDeleteMode_SendsFirst_ThenDeletesThePrevious()
    {
        AddPostChannel(configure: c => c.SchedulePostMode = PostMode.ReplaceDelete);
        await TickAsync(Kyiv(15, 12));
        var first = PostOf(ChatA)!;
        Assert.Empty(_f.Bot.Deletes);
        _schedules.Snapshots["kyiv"] = LaterEnd(RealSnapshot());

        var summary = await TickAsync(Kyiv(15, 12, 30));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(first.MessageId, Assert.Single(_f.Bot.Deletes).MessageId);
        var sendIndex = _f.Bot.Requests.FindLastIndex(r => r is SendPhotoRequest);
        var deleteIndex = _f.Bot.Requests.FindIndex(r => r is DeleteMessageRequest);
        Assert.True(sendIndex < deleteIndex);
        Assert.NotEqual(first.MessageId, PostOf(ChatA)!.MessageId);
    }

    [Fact]
    public async Task Post_ReplaceDeleteMode_DeleteFailureIsIgnored()
    {
        AddPostChannel(configure: c => c.SchedulePostMode = PostMode.ReplaceDelete);
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["kyiv"] = LaterEnd(RealSnapshot());
        _f.Bot.Failure = r => r is DeleteMessageRequest ? FakeTelegramBotClient.Forbidden() : null;

        var summary = await TickAsync(Kyiv(15, 12, 30));

        Assert.Equal(1, summary.Sent);
        Assert.Equal(0, summary.Failed);
        Assert.True(_f.LoadChannel(ChatA).IsActive);
    }

    [Theory]
    [InlineData(PostMode.New, false)]
    [InlineData(PostMode.Edit, false)]
    [InlineData(PostMode.ReplaceDelete, true)]
    public async Task Post_NewDay_IsANewMessage_WithTheFirstHeading_AndDeletesOnlyInReplaceDeleteMode(PostMode mode, bool deletes)
    {
        AddPostChannel(configure: c => c.SchedulePostMode = mode);
        await TickAsync(Kyiv(15, 12));
        var first = PostOf(ChatA)!;

        var summary = await TickAsync(Kyiv(16, 8));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Photos.Count());
        Assert.Equal(
            "❗️ Графік відключень на сьогодні\n📅 П\u02BCятниця 16.10.2026\n\n🔴 06:00 - 09:30 (~3,5 год)\n🔴 17:00 - 20:30 (~3,5 год)",
            _f.Bot.Photos.Last().Caption);
        Assert.Equal(deletes ? 1 : 0, _f.Bot.Deletes.Count());
        Assert.Empty(_f.Bot.CaptionEdits);
        var post = PostOf(ChatA)!;
        Assert.Equal("2026-10-16", post.DateKey);
        Assert.NotEqual(first.MessageId, post.MessageId);
    }

    [Fact]
    public async Task Post_DayWithoutOutages_AndNoPriorPost_IsSkipped()
    {
        AddPostChannel();
        _schedules.Snapshots["kyiv"] = Parse(Json("v", "u", (Day1, "45.1", []), (Day2, "45.1", [])))!;

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.Null(PostOf(ChatA));
    }

    [Fact]
    public async Task Post_NewDayWithoutOutages_WithAPriorPost_IsSkipped_AndLeavesTheOldPost()
    {
        AddPostChannel();
        await TickAsync(Kyiv(15, 12));
        var first = PostOf(ChatA)!;
        _schedules.Snapshots["kyiv"] = Parse(Json("v", "u", (Day1, "45.1", []), (Day2, "45.1", [])))!;

        var summary = await TickAsync(Kyiv(16, 8));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Photos);
        Assert.Equal(first.MessageId, PostOf(ChatA)!.MessageId);
        Assert.Equal("2026-10-15", PostOf(ChatA)!.DateKey);
    }

    [Fact]
    public async Task Post_TextOnly_WhenNoPictureIsWanted()
    {
        AddPostChannel(configure: c => c.SchedulePostImage = ScheduleImage.None);

        await TickAsync(Kyiv(15, 12));

        Assert.Empty(_f.Bot.Photos);
        Assert.Equal(PostText12, Assert.Single(_f.Bot.Sent).Text);
        var post = PostOf(ChatA)!;
        Assert.False(post.IsPhoto);
        Assert.Equal(ScheduleImage.None, post.ImageKind);
    }

    [Fact]
    public async Task Post_TextOnly_EditMode_EditsTheText()
    {
        AddPostChannel(configure: c => { c.SchedulePostImage = ScheduleImage.None; c.SchedulePostMode = PostMode.Edit; });
        await TickAsync(Kyiv(15, 12));
        _schedules.Snapshots["kyiv"] = LaterEnd(RealSnapshot());

        await TickAsync(Kyiv(15, 12, 30));

        var edit = Assert.Single(_f.Bot.Edits);
        Assert.StartsWith("❗️ Оновлений графік", edit.Text);
        Assert.Empty(_f.Bot.CaptionEdits);
    }

    [Fact]
    public async Task Post_EditMode_FlippingTextToPicture_PostsNew_AndDeletesTheOld()
    {
        var channel = AddPostChannel(configure: c => { c.SchedulePostImage = ScheduleImage.None; c.SchedulePostMode = PostMode.Edit; });
        await TickAsync(Kyiv(15, 12));
        var first = PostOf(ChatA)!;

        channel.SchedulePostImage = ScheduleImage.Grid;
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 30));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 0, 0), summary);
        Assert.Single(_f.Bot.Photos);
        Assert.Equal(first.MessageId, Assert.Single(_f.Bot.Deletes).MessageId);
        var post = PostOf(ChatA)!;
        Assert.True(post.IsPhoto);
        Assert.Equal(ScheduleImage.Grid, post.ImageKind);
    }

    [Fact]
    public async Task Post_EditMode_SwitchingGridToCard_EditsTheMedia()
    {
        var channel = AddPostChannel(configure: c => c.SchedulePostMode = PostMode.Edit);
        await TickAsync(Kyiv(15, 12));

        channel.SchedulePostImage = ScheduleImage.Card;
        _f.Db.SaveChanges();
        var summary = await TickAsync(Kyiv(15, 12, 30));

        Assert.Equal(new ScheduleTickSummary(1, 0, 1, 0, 0), summary);
        Assert.Equal(CardUrl, MediaUrl(Assert.Single(_f.Bot.MediaEdits)));
        Assert.Equal(ScheduleImage.Card, PostOf(ChatA)!.ImageKind);
    }

    [Fact]
    public async Task Post_UsesTheChatsScheduleTemplate()
    {
        AddPostChannel(configure: c => c.Templates = TemplatesJson(
            (MessageSlots.Schedule, "{header}|{weekday}|{date}|{total}|{updated}|{region}|{group}\n{outages}\n{#tomorrow}Завтра:\n{tomorrow}{/tomorrow}")));

        await TickAsync(Kyiv(15, 12));

        var caption = Assert.Single(_f.Bot.Photos).Caption!;
        Assert.StartsWith(
            "Графік відключень на сьогодні|Четвер|15.10.2026|~6,5 год|15.10.2026 20:24|м. Київ|45.1\n🔴 11:00 - 14:00 (~3 год)\n🔴 20:30 - 24:00 (~3,5 год)\nЗавтра:\n🔴 06:00 - 09:30",
            caption);
    }

    [Fact]
    public async Task Post_LongText_IsSentAsTextOnly_BecauseTheCaptionLimitIsExceeded()
    {
        AddPostChannel(configure: c => c.Templates = TemplatesJson((MessageSlots.Schedule, new string('x', 1100) + "{outages}")));

        await TickAsync(Kyiv(15, 12));

        Assert.Empty(_f.Bot.Photos);
        Assert.Equal(1100, Assert.Single(_f.Bot.Sent).Text.IndexOf("🔴", StringComparison.Ordinal));
        Assert.False(PostOf(ChatA)!.IsPhoto);
    }

    [Fact]
    public async Task Post_Telegram403_DeactivatesTheChannel()
    {
        AddPostChannel();
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(1, 0, 0, 0, 1), summary);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
        Assert.Null(PostOf(ChatA));
    }

    [Fact]
    public async Task Post_PhotoRejected_FallsBackToText()
    {
        AddPostChannel();
        _f.Bot.Failure = r => r is SendPhotoRequest
            ? new ApiRequestException("Bad Request: wrong type of the web page content", 400)
            : null;

        var summary = await TickAsync(Kyiv(15, 12));

        Assert.Equal(1, summary.Sent);
        Assert.Equal(PostText12, Assert.Single(_f.Bot.Sent).Text);
        var post = PostOf(ChatA)!;
        Assert.False(post.IsPhoto);
        Assert.Equal(ScheduleImage.Grid, post.ImageKind);
    }

    [Fact]
    public async Task Post_GenericFailure_IsFailed_AndRetriedNextTick()
    {
        AddPostChannel();
        _f.Bot.Failure = _ => new HttpRequestException("down");
        Assert.Equal(1, (await TickAsync(Kyiv(15, 12))).Failed);
        Assert.Null(PostOf(ChatA));

        _f.Bot.Failure = null;

        Assert.Equal(1, (await TickAsync(Kyiv(15, 12, 1))).Sent);
    }

    [Fact]
    public async Task PostAndEvents_AreIndependent_ForOneChat()
    {
        AddChannel(configure: c => c.SchedulePost = true);

        var summary = await TickAsync(Kyiv(15, 9));

        // Power is on with nothing announced: the event path skips; the day's post goes out.
        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Photos);
        Assert.Null(ThreadOf(ChatA));
        Assert.NotNull(PostOf(ChatA));

        var later = await TickAsync(Kyiv(15, 12));

        Assert.Equal(new ScheduleTickSummary(1, 1, 0, 1, 0), later);
        Assert.NotNull(ThreadOf(ChatA));
    }

    [Fact]
    public async Task PostWithoutEvents_DoesNotCreateAnEventThread()
    {
        AddPostChannel();

        await TickAsync(Kyiv(15, 12));

        Assert.Null(ThreadOf(ChatA));
        Assert.NotNull(PostOf(ChatA));
    }

    // ================= public SendNewAsync =================

    [Fact]
    public async Task SendNewAsync_WithAnImage_SendsAPhotoWithTheCaption()
    {
        var (id, isPhoto) = await _notifier.SendNewAsync(ChatA, "<b>Тест</b>", GridUrl, CancellationToken.None);

        var photo = Assert.Single(_f.Bot.Photos);
        Assert.Equal(GridUrl, PhotoUrl(photo));
        Assert.Equal("<b>Тест</b>", photo.Caption);
        Assert.Equal(ParseMode.Html, photo.ParseMode);
        Assert.True(isPhoto);
        Assert.True(id >= 1000);
    }

    [Fact]
    public async Task SendNewAsync_WithoutAnImage_SendsText_WithoutLinkPreview()
    {
        var (id, isPhoto) = await _notifier.SendNewAsync(ChatA, "текст", null, CancellationToken.None);

        var message = Assert.Single(_f.Bot.Sent);
        Assert.Equal("текст", message.Text);
        Assert.Equal(ParseMode.Html, message.ParseMode);
        Assert.True(message.LinkPreviewOptions?.IsDisabled);
        Assert.False(isPhoto);
        Assert.True(id >= 1000);
        Assert.Empty(_f.Bot.Photos);
    }

    [Fact]
    public async Task SendNewAsync_PictureRejected_FallsBackToText()
    {
        _f.Bot.Failure = r => r is SendPhotoRequest ? new ApiRequestException("Bad Request: wrong file identifier", 400) : null;

        var (_, isPhoto) = await _notifier.SendNewAsync(ChatA, "текст", GridUrl, CancellationToken.None);

        Assert.False(isPhoto);
        Assert.Single(_f.Bot.Photos);
        Assert.Equal("текст", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task SendNewAsync_CaptionOverTheLimit_GoesAsTextWithoutTryingThePicture()
    {
        var text = new string('x', 1025);

        var (_, isPhoto) = await _notifier.SendNewAsync(ChatA, text, GridUrl, CancellationToken.None);

        Assert.False(isPhoto);
        Assert.Empty(_f.Bot.Photos);
        Assert.Equal(text, Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task SendNewAsync_CaptionAtTheLimit_IsStillAPhoto()
    {
        var (_, isPhoto) = await _notifier.SendNewAsync(ChatA, new string('x', 1024), GridUrl, CancellationToken.None);

        Assert.True(isPhoto);
        Assert.Single(_f.Bot.Photos);
    }

    [Fact]
    public async Task SendNewAsync_MarkupRejected_RetriesAsPlainText()
    {
        var calls = 0;
        _f.Bot.Failure = r => r is SendPhotoRequest && calls++ == 0 ? FakeTelegramBotClient.CantParseEntities() : null;

        var (_, isPhoto) = await _notifier.SendNewAsync(ChatA, "<b>x</b> y", GridUrl, CancellationToken.None);

        Assert.True(isPhoto);
        var photos = _f.Bot.Photos.ToList();
        Assert.Equal(2, photos.Count);
        Assert.Equal("<b>x</b> y", photos[0].Caption);
        Assert.Equal("x y", photos[1].Caption);
        Assert.Equal(ParseMode.None, photos[1].ParseMode);
    }

    [Fact]
    public async Task SendNewAsync_ChatNotFound_Propagates()
    {
        _f.Bot.Failure = _ => new ApiRequestException("Bad Request: chat not found", 400);

        await Assert.ThrowsAsync<ApiRequestException>(() => _notifier.SendNewAsync(ChatA, "x", GridUrl, CancellationToken.None));
    }

    [Fact]
    public async Task SendNewAsync_Forbidden_Propagates()
    {
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var ex = await Assert.ThrowsAsync<ApiRequestException>(() => _notifier.SendNewAsync(ChatA, "x", null, CancellationToken.None));

        Assert.Equal(403, ex.ErrorCode);
    }

    [Fact]
    public async Task SendNewAsync_RateLimited_RetriesOnce()
    {
        var calls = 0;
        _f.Bot.Failure = r => r is SendMessageRequest && calls++ == 0
            ? new ApiRequestException("Too Many Requests", 429, new ResponseParameters { RetryAfter = 0 })
            : null;

        await _notifier.SendNewAsync(ChatA, "x", null, CancellationToken.None);

        Assert.Equal(2, _f.Bot.Sent.Count());
    }

    // ================= texts never re-trigger the channel reaction =================

    [Fact]
    public async Task Messages_NeverMatchThePowerOutTrigger()
    {
        AddChannel(configure: c => { c.SchedulePost = true; c.ScheduleImage = ScheduleImage.Grid; });
        var regex = new System.Text.RegularExpressions.Regex(@"світло\s+зникло", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var at in new[] { Kyiv(15, 9), Kyiv(15, 12), Kyiv(15, 14, 5), Kyiv(15, 21) })
        {
            await TickAsync(at);
        }

        var texts = _f.Bot.Photos.Select(p => p.Caption!)
            .Concat(_f.Bot.CaptionEdits.Select(e => e.Caption!))
            .Concat(_f.Bot.Sent.Select(s => s.Text))
            .ToList();
        Assert.NotEmpty(texts);
        Assert.All(texts, t => Assert.DoesNotMatch(regex, t));
    }
}
