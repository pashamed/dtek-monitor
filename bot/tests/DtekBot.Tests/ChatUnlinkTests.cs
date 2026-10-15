using DtekBot.Api.Data;
using DtekBot.Api.MiniApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Types.Enums;

namespace DtekBot.Tests;

public sealed class ChatUnlinkTests : IDisposable
{
    private const long Chat = -1001;
    private const long OtherChat = -1002;
    private const long UserA = 11;
    private const long UserB = 12;

    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly DispatcherFixture _f = new();
    private readonly ChatAccessService _access;

    public ChatUnlinkTests()
    {
        _access = new ChatAccessService(_f.Bot, _f.Db, NullLogger<ChatAccessService>.Instance);
    }

    public void Dispose() => _f.Dispose();

    // ---- helpers ----

    private Channel AddFullChannel(long chatId = Chat)
    {
        var channel = _f.AddChannel(chatId, c =>
        {
            c.PartId = "right-bank";
            c.Title = "Мій будинок";
            c.GroupId = "45.1";
            c.AddressLabel = "вул. Драгоманова, 12";
            c.AddressStreet = "вул. Драгоманова";
            c.AddressHouse = "12";
            c.OutageTrigger = true;
            c.ScheduleNotify = true;
            c.Template = "<b>{message}</b>";
            c.Filter = DtekBot.Shared.AlertFilter.EmergencyOnly;
            c.IsPaused = true;
            c.NotifyOnEnd = false;
            c.EditInPlace = false;
        });
        return channel;
    }

    private void Link(long chatId, long userId)
    {
        _f.Db.ChannelAdmins.Add(new ChannelAdmin { ChatId = chatId, UserId = userId, LinkedAt = Now.AddDays(-1) });
        _f.Db.SaveChanges();
    }

    private AddressCheck AddCheck(long chatId, AddressCheckStatus status, string house = "12")
    {
        var check = new AddressCheck
        {
            ChatId = chatId,
            RegionId = "kyiv",
            Street = "вул. Драгоманова",
            House = house,
            Label = "x",
            Status = status,
            RequestedAt = Now.AddMinutes(-5),
            CompletedAt = status == AddressCheckStatus.Pending ? null : Now.AddMinutes(-4),
        };
        _f.Db.AddressChecks.Add(check);
        _f.Db.SaveChanges();
        return check;
    }

    private Task<UnlinkOutcome> UnlinkAsync(long chatId = Chat, long userId = UserA) =>
        _access.UnlinkAsync(chatId, userId, Now, CancellationToken.None);

    private List<(long ChatId, long UserId)> Links() =>
        _f.Db.ChannelAdmins.AsNoTracking().AsEnumerable()
            .Select(a => (a.ChatId, a.UserId)).OrderBy(a => a.ChatId).ThenBy(a => a.UserId).ToList();

    private AddressCheck LoadCheck(long id) => _f.Db.AddressChecks.AsNoTracking().Single(c => c.Id == id);

    // ---- not linked ----

    [Fact]
    public async Task NotLinked_ReturnsNotLinked_AndChangesNothing()
    {
        AddFullChannel();
        Link(Chat, UserB);
        var pending = AddCheck(Chat, AddressCheckStatus.Pending);

        var outcome = await UnlinkAsync(userId: UserA);

        Assert.Equal(UnlinkOutcome.NotLinked, outcome);
        Assert.Equal([(Chat, UserB)], Links());
        var channel = _f.LoadChannel(Chat);
        Assert.True(channel.IsActive);
        Assert.True(channel.OutageTrigger);
        Assert.Equal("45.1", channel.GroupId);
        Assert.Equal(AddressCheckStatus.Pending, LoadCheck(pending.Id).Status);
    }

    [Fact]
    public async Task NotLinked_WhenChatHasNoLinksAtAll_ReturnsNotLinked()
    {
        AddFullChannel();

        Assert.Equal(UnlinkOutcome.NotLinked, await UnlinkAsync());
        Assert.True(_f.LoadChannel(Chat).IsActive);
    }

    [Fact]
    public async Task NotLinked_LinkOfTheSameUserInAnotherChat_DoesNotCount()
    {
        AddFullChannel();
        Link(OtherChat, UserA);

        var outcome = await UnlinkAsync(Chat, UserA);

        Assert.Equal(UnlinkOutcome.NotLinked, outcome);
        Assert.Equal([(OtherChat, UserA)], Links());
    }

    // ---- other admins remain ----

    [Fact]
    public async Task OtherAdminsStillLinked_OnlyTheCallersLinkIsRemoved()
    {
        var before = AddFullChannel();
        Link(Chat, UserA);
        Link(Chat, UserB);
        var pending = AddCheck(Chat, AddressCheckStatus.Pending);

        var outcome = await UnlinkAsync(userId: UserA);

        Assert.Equal(UnlinkOutcome.Unlinked, outcome);
        Assert.Equal([(Chat, UserB)], Links());

        var channel = _f.LoadChannel(Chat);
        Assert.True(channel.IsActive);
        Assert.True(channel.OutageTrigger);
        Assert.True(channel.ScheduleNotify);
        Assert.Equal(before.RegionId, channel.RegionId);
        Assert.Equal("45.1", channel.GroupId);
        Assert.Equal("вул. Драгоманова, 12", channel.AddressLabel);
        Assert.Equal("вул. Драгоманова", channel.AddressStreet);
        Assert.Equal("12", channel.AddressHouse);

        var check = LoadCheck(pending.Id);
        Assert.Equal(AddressCheckStatus.Pending, check.Status);
        Assert.Null(check.CompletedAt);
    }

    [Fact]
    public async Task OtherAdminsStillLinked_ThenTheLastOneLeaving_Unsubscribes()
    {
        AddFullChannel();
        Link(Chat, UserA);
        Link(Chat, UserB);

        Assert.Equal(UnlinkOutcome.Unlinked, await UnlinkAsync(userId: UserA));
        Assert.True(_f.LoadChannel(Chat).IsActive);

        Assert.Equal(UnlinkOutcome.UnlinkedAndUnsubscribed, await UnlinkAsync(userId: UserB));
        Assert.False(_f.LoadChannel(Chat).IsActive);
        Assert.Empty(Links());
    }

    // ---- last admin ----

    [Fact]
    public async Task LastAdmin_Unsubscribes_AndClearsPersonalData()
    {
        AddFullChannel();
        Link(Chat, UserA);

        var outcome = await UnlinkAsync();

        Assert.Equal(UnlinkOutcome.UnlinkedAndUnsubscribed, outcome);
        Assert.Empty(Links());

        var channel = _f.LoadChannel(Chat);
        Assert.False(channel.IsActive);
        Assert.False(channel.OutageTrigger);
        Assert.False(channel.ScheduleNotify);
        Assert.Null(channel.GroupId);
        Assert.Null(channel.AddressLabel);
        Assert.Null(channel.AddressStreet);
        Assert.Null(channel.AddressHouse);
    }

    [Fact]
    public async Task LastAdmin_KeepsOtherSettings()
    {
        AddFullChannel();
        Link(Chat, UserA);

        await UnlinkAsync();

        var channel = _f.LoadChannel(Chat);
        Assert.Equal("kyiv", channel.RegionId);
        Assert.Equal("right-bank", channel.PartId);
        Assert.Equal("Мій будинок", channel.Title);
        Assert.Equal("<b>{message}</b>", channel.Template);
        Assert.Equal(DtekBot.Shared.AlertFilter.EmergencyOnly, channel.Filter);
        Assert.True(channel.IsPaused);
        Assert.False(channel.NotifyOnEnd);
        Assert.False(channel.EditInPlace);
    }

    [Fact]
    public async Task LastAdmin_ExpiresPendingChecks_WithCompletedAt_AndSendsNoMessage()
    {
        AddFullChannel();
        Link(Chat, UserA);
        var first = AddCheck(Chat, AddressCheckStatus.Pending, "12");
        var second = AddCheck(Chat, AddressCheckStatus.Pending, "14");

        await UnlinkAsync();

        foreach (var id in new[] { first.Id, second.Id })
        {
            var check = LoadCheck(id);
            Assert.Equal(AddressCheckStatus.Expired, check.Status);
            Assert.Equal(Now, check.CompletedAt);
        }

        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task LastAdmin_LeavesDoneAndExpiredChecksAsTheyWere()
    {
        AddFullChannel();
        Link(Chat, UserA);
        var done = AddCheck(Chat, AddressCheckStatus.Done, "1");
        var expired = AddCheck(Chat, AddressCheckStatus.Expired, "2");
        var doneBefore = LoadCheck(done.Id);
        var expiredBefore = LoadCheck(expired.Id);

        await UnlinkAsync();

        var doneAfter = LoadCheck(done.Id);
        var expiredAfter = LoadCheck(expired.Id);
        Assert.Equal(AddressCheckStatus.Done, doneAfter.Status);
        Assert.Equal(doneBefore.CompletedAt, doneAfter.CompletedAt);
        Assert.Equal(AddressCheckStatus.Expired, expiredAfter.Status);
        Assert.Equal(expiredBefore.CompletedAt, expiredAfter.CompletedAt);
    }

    [Fact]
    public async Task LastAdmin_WithoutChannelRow_DoesNotThrow_AndStillUnlinks()
    {
        Link(Chat, UserA);
        var pending = AddCheck(Chat, AddressCheckStatus.Pending);

        var outcome = await UnlinkAsync();

        Assert.Equal(UnlinkOutcome.UnlinkedAndUnsubscribed, outcome);
        Assert.Empty(Links());
        Assert.False(_f.Db.Channels.AsNoTracking().Any(c => c.ChatId == Chat));
        Assert.Equal(AddressCheckStatus.Expired, LoadCheck(pending.Id).Status);
    }

    [Fact]
    public async Task LastAdmin_AlreadyInactiveChannel_StaysInactive_AndStillGetsCleared()
    {
        _f.AddChannel(Chat, c =>
        {
            c.IsActive = false;
            c.AddressStreet = "вул. Драгоманова";
            c.GroupId = "45.1";
        });
        Link(Chat, UserA);

        Assert.Equal(UnlinkOutcome.UnlinkedAndUnsubscribed, await UnlinkAsync());

        var channel = _f.LoadChannel(Chat);
        Assert.False(channel.IsActive);
        Assert.Null(channel.AddressStreet);
        Assert.Null(channel.GroupId);
    }

    [Fact]
    public async Task Unlinking_Twice_SecondIsNotLinked()
    {
        AddFullChannel();
        Link(Chat, UserA);

        Assert.Equal(UnlinkOutcome.UnlinkedAndUnsubscribed, await UnlinkAsync());
        Assert.Equal(UnlinkOutcome.NotLinked, await UnlinkAsync());
    }

    // ---- no Telegram involvement ----

    [Fact]
    public async Task Unlink_WorksWhenUserIsNoLongerATelegramAdmin_WithoutAnyTelegramCall()
    {
        AddFullChannel();
        Link(Chat, UserA);
        _f.Bot.SetStatus(UserA, ChatMemberStatus.Left);
        _f.Bot.Failure = _ => new HttpRequestException("Telegram is unreachable");

        var outcome = await UnlinkAsync();

        Assert.Equal(UnlinkOutcome.UnlinkedAndUnsubscribed, outcome);
        Assert.Empty(_f.Bot.Requests);
        Assert.False(_f.LoadChannel(Chat).IsActive);
    }

    [Fact]
    public async Task Unlink_WhenOtherAdminsRemain_MakesNoTelegramCallEither()
    {
        AddFullChannel();
        Link(Chat, UserA);
        Link(Chat, UserB);

        await UnlinkAsync(userId: UserA);

        Assert.Empty(_f.Bot.Requests);
    }

    // ---- isolation ----

    [Fact]
    public async Task Unlink_NeverTouchesOtherChatsOrOtherUsersLinks()
    {
        AddFullChannel(Chat);
        AddFullChannel(OtherChat);
        Link(Chat, UserA);
        Link(OtherChat, UserA);
        Link(OtherChat, UserB);
        var otherPending = AddCheck(OtherChat, AddressCheckStatus.Pending);
        var ownPending = AddCheck(Chat, AddressCheckStatus.Pending);

        await UnlinkAsync(Chat, UserA);

        Assert.Equal([(OtherChat, UserA), (OtherChat, UserB)], Links());

        var other = _f.LoadChannel(OtherChat);
        Assert.True(other.IsActive);
        Assert.True(other.OutageTrigger);
        Assert.Equal("45.1", other.GroupId);
        Assert.Equal("вул. Драгоманова", other.AddressStreet);
        Assert.Equal("12", other.AddressHouse);
        Assert.Equal("вул. Драгоманова, 12", other.AddressLabel);

        Assert.Equal(AddressCheckStatus.Pending, LoadCheck(otherPending.Id).Status);
        Assert.Equal(AddressCheckStatus.Expired, LoadCheck(ownPending.Id).Status);
    }

    [Fact]
    public async Task Unlink_UsesTheGivenTimestamp_NotTheSystemClock()
    {
        AddFullChannel();
        Link(Chat, UserA);
        var pending = AddCheck(Chat, AddressCheckStatus.Pending);
        var custom = new DateTimeOffset(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);

        await _access.UnlinkAsync(Chat, UserA, custom, CancellationToken.None);

        Assert.Equal(custom, LoadCheck(pending.Id).CompletedAt);
    }

    // ---- effect on alert delivery ----

    [Fact]
    public async Task UnlinkedAndUnsubscribedChannel_NoLongerReceivesRegionAlerts()
    {
        _f.AddChannel(Chat);
        Link(Chat, UserA);
        _f.AddChannel(OtherChat);

        var before = await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        Assert.Equal(new(2, 0, 0, 0), before);
        _f.Bot.Requests.Clear();

        await UnlinkAsync();
        var after = await _f.DispatchAsync(DispatcherFixture.EmergencyText + " Оновлення.");

        Assert.Empty(_f.Bot.SentTo(Chat));
        Assert.DoesNotContain(_f.Bot.Edits, e => e.ChatId.Identifier == Chat);
        Assert.Equal(1, after.Edited);
        Assert.Equal(0, after.Sent);
        Assert.Single(_f.Bot.Edits);
        Assert.Equal(OtherChat, _f.Bot.Edits.Single().ChatId.Identifier);
    }

    [Fact]
    public async Task UnlinkedAndUnsubscribedChannel_GetsNothingFromAFreshRegionAlert()
    {
        _f.AddChannel(Chat);
        Link(Chat, UserA);

        await UnlinkAsync();
        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task UnlinkedAndUnsubscribedChannel_OpenRegionThreadIsClosedSilently()
    {
        _f.AddChannel(Chat);
        Link(Chat, UserA);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        _f.Bot.Requests.Clear();

        await UnlinkAsync();
        var summary = await _f.DispatchAsync("");

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.True(Assert.Single(_f.Deliveries(Chat)).IsClosed);
    }

    [Fact]
    public async Task UnlinkedAndUnsubscribedChannel_NoLongerReceivesGroupAlerts()
    {
        _f.AddChannel(Chat, c => c.GroupId = "45.1");
        Link(Chat, UserA);

        await UnlinkAsync();
        var summary = await _f.Dispatcher.DispatchGroupAsync(_f.Kyiv, "45.1", true, CancellationToken.None);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task OnlyUnlinked_ChannelWithRemainingAdmins_KeepsReceivingAlerts()
    {
        _f.AddChannel(Chat);
        Link(Chat, UserA);
        Link(Chat, UserB);

        await UnlinkAsync(userId: UserA);
        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(1, 0, 0, 0), summary);
        Assert.Single(_f.Bot.SentTo(Chat));
    }
}
