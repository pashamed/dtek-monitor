using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.Enums;

namespace DtekBot.Tests;

public sealed class GroupAlertDispatcherTests : IDisposable
{
    private const string Group = "45.1";
    private const long ChatA = 201;
    private const long ChatB = 202;

    private readonly DispatcherFixture _f = new();
    private readonly string _key = AlertDispatcher.GroupDeliveryKey("kyiv", Group);

    public void Dispose() => _f.Dispose();

    private Task<DispatchSummary> GroupAsync(bool emergency, string group = Group, string regionId = "kyiv") =>
        _f.Dispatcher.DispatchGroupAsync(
            _f.Settings.Regions.Single(r => r.Id == regionId), group, emergency, CancellationToken.None);

    private List<Delivery> AllDeliveries(long chatId) => _f.Deliveries(chatId);

    [Fact]
    public void GroupDeliveryKey_IsDistinctFromRegionIdAndPerGroup()
    {
        var a = AlertDispatcher.GroupDeliveryKey("kyiv", "45.1");
        var b = AlertDispatcher.GroupDeliveryKey("kyiv", "46.1");
        var c = AlertDispatcher.GroupDeliveryKey("odesa", "45.1");

        Assert.NotEqual("kyiv", a);
        Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
        Assert.Contains("kyiv", a);
        Assert.Contains("45.1", a);
    }

    [Fact]
    public async Task Emergency_SendsToEveryActiveUnpausedChannelOfTheGroup_AndOnlyThem()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);
        _f.AddChannel(ChatB, c => c.GroupId = Group);
        _f.AddChannel(203, c => c.GroupId = "46.1");
        _f.AddChannel(204);
        _f.AddChannel(205, c => c.GroupId = Group, regionId: "odesa");
        _f.AddChannel(206, c => { c.GroupId = Group; c.IsActive = false; });
        _f.AddChannel(207, c => { c.GroupId = Group; c.IsPaused = true; });

        var summary = await GroupAsync(emergency: true);

        Assert.Equal(new(2, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Requests.Count);
        Assert.Single(_f.Bot.SentTo(ChatA));
        Assert.Single(_f.Bot.SentTo(ChatB));
        foreach (var other in new long[] { 203, 204, 205, 206, 207 })
        {
            Assert.Empty(_f.Bot.SentTo(other));
            Assert.Empty(AllDeliveries(other));
        }
    }

    [Fact]
    public async Task Emergency_MessageMentionsTheGroup_AndIsSentAsHtml()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);

        await GroupAsync(emergency: true);

        var request = Assert.Single(_f.Bot.Sent);
        Assert.Contains("черга 45.1", request.Text);
        Assert.Contains("м. Київ", request.Text);
        Assert.Contains("🚨", request.Text);
        Assert.Equal(ParseMode.Html, request.ParseMode);
        Assert.Null(request.ReplyParameters);
    }

    [Fact]
    public async Task Emergency_CreatesOpenDelivery_UnderGroupKey()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);

        await GroupAsync(emergency: true);

        var delivery = Assert.Single(AllDeliveries(ChatA));
        Assert.Equal(_key, delivery.RegionId);
        Assert.False(delivery.IsClosed);
        Assert.True(delivery.TelegramMessageId >= 1000);
    }

    [Fact]
    public async Task IdenticalSecondEmergency_IsNoOp()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);
        await GroupAsync(emergency: true);
        var requests = _f.Bot.Requests.Count;

        var summary = await GroupAsync(emergency: true);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Equal(requests, _f.Bot.Requests.Count);
        Assert.Single(AllDeliveries(ChatA));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RepeatedEmergency_NeverEditsOrResends_RegardlessOfEditInPlace(bool editInPlace)
    {
        _f.AddChannel(ChatA, c => { c.GroupId = Group; c.EditInPlace = editInPlace; });
        await GroupAsync(emergency: true);

        await GroupAsync(emergency: true);
        await GroupAsync(emergency: true);

        Assert.Single(_f.Bot.Requests);
        Assert.Empty(_f.Bot.Edits);
    }

    [Fact]
    public async Task NoLongerEmergency_RepliesToOriginalMessage_AndClosesDelivery()
    {
        _f.AddChannel(ChatA, c => { c.GroupId = Group; c.NotifyOnEnd = true; });
        await GroupAsync(emergency: true);
        var original = Assert.Single(AllDeliveries(ChatA));

        var summary = await GroupAsync(emergency: false);

        Assert.Equal(new(0, 0, 1, 0), summary);
        var reply = _f.Bot.Sent.Last();
        Assert.Equal(2, _f.Bot.Sent.Count());
        Assert.Equal(original.TelegramMessageId, reply.ReplyParameters?.MessageId);
        Assert.Contains("✅", reply.Text);
        Assert.Contains("черга 45.1", reply.Text);
        Assert.Equal(ParseMode.Html, reply.ParseMode);
        Assert.True(Assert.Single(AllDeliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task NoLongerEmergency_WithNotifyOnEndOff_ClosesDeliverySilently()
    {
        _f.AddChannel(ChatA, c => { c.GroupId = Group; c.NotifyOnEnd = false; });
        await GroupAsync(emergency: true);

        var summary = await GroupAsync(emergency: false);

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Requests);
        Assert.True(Assert.Single(AllDeliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task NotEmergency_WithoutOpenDelivery_DoesNothing()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);

        var summary = await GroupAsync(emergency: false);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task NoMatchingChannels_DoesNothing()
    {
        _f.AddChannel(ChatA, c => c.GroupId = "46.1");

        var summary = await GroupAsync(emergency: true);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task EmergencyAfterEnd_StartsAFreshThread()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);
        await GroupAsync(emergency: true);
        await GroupAsync(emergency: false);

        var summary = await GroupAsync(emergency: true);

        Assert.Equal(new(1, 0, 0, 0), summary);
        var deliveries = AllDeliveries(ChatA);
        Assert.Equal(2, deliveries.Count);
        Assert.True(deliveries[0].IsClosed);
        Assert.False(deliveries[1].IsClosed);
        Assert.Null(_f.Bot.Sent.Last().ReplyParameters);
    }

    [Fact]
    public async Task PausedChannel_GetsNothing()
    {
        _f.AddChannel(ChatA, c => { c.GroupId = Group; c.IsPaused = true; });

        var summary = await GroupAsync(emergency: true);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.Empty(AllDeliveries(ChatA));
    }

    [Fact]
    public async Task PausedChannel_WithOpenDelivery_IsClosedSilently()
    {
        var channel = _f.AddChannel(ChatA, c => c.GroupId = Group);
        await GroupAsync(emergency: true);
        channel.IsPaused = true;
        _f.Db.SaveChanges();

        var summary = await GroupAsync(emergency: true);

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Requests);
        Assert.True(Assert.Single(AllDeliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task CustomTemplate_IsUsed_WithGroupInWherePlaceholder()
    {
        _f.AddChannel(ChatA, c => { c.GroupId = Group; c.Template = "<b>{where}</b>"; });

        await GroupAsync(emergency: true);

        var request = Assert.Single(_f.Bot.Sent);
        Assert.Equal("<b>м. Київ (черга 45.1)</b>", request.Text);
        Assert.Equal(ParseMode.Html, request.ParseMode);
    }

    [Fact]
    public async Task CustomTemplate_PartPlaceholder_IsTheGroup()
    {
        _f.AddChannel(ChatA, c => { c.GroupId = Group; c.Template = "{part}|{region}|{message}"; });

        await GroupAsync(emergency: true);

        Assert.StartsWith("черга 45.1|м. Київ|", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task ChannelAddressLabel_DoesNotAffectDelivery()
    {
        _f.AddChannel(ChatA, c => { c.GroupId = Group; c.AddressLabel = "вул. Драгоманова, 12"; });

        var summary = await GroupAsync(emergency: true);

        Assert.Equal(new(1, 0, 0, 0), summary);
    }

    // ---- coexistence with region-wide deliveries ----

    [Fact]
    public async Task GroupAndRegionWideThreads_CoexistForTheSameChat()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);

        var regionWide = await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        var group = await GroupAsync(emergency: true);

        Assert.Equal(new(1, 0, 0, 0), regionWide);
        Assert.Equal(new(1, 0, 0, 0), group);
        Assert.Equal(2, _f.Bot.Sent.Count());

        var deliveries = AllDeliveries(ChatA);
        Assert.Equal(2, deliveries.Count);
        Assert.Contains(deliveries, d => d.RegionId == "kyiv" && !d.IsClosed);
        Assert.Contains(deliveries, d => d.RegionId == _key && !d.IsClosed);
        Assert.NotEqual(deliveries[0].TelegramMessageId, deliveries[1].TelegramMessageId);
    }

    [Fact]
    public async Task RepeatingEitherAlert_StaysANoOp_WhenBothThreadsAreOpen()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        await GroupAsync(emergency: true);
        var requests = _f.Bot.Requests.Count;

        var regionWide = await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        var group = await GroupAsync(emergency: true);

        Assert.Equal(new(0, 0, 0, 0), regionWide);
        Assert.Equal(new(0, 0, 0, 0), group);
        Assert.Equal(requests, _f.Bot.Requests.Count);
        Assert.Equal(2, AllDeliveries(ChatA).Count);
    }

    [Fact]
    public async Task EndingTheGroupAlert_LeavesTheRegionWideThreadOpen()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        await GroupAsync(emergency: true);
        var groupDelivery = AllDeliveries(ChatA).Single(d => d.RegionId == _key);

        var summary = await GroupAsync(emergency: false);

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Equal(groupDelivery.TelegramMessageId, _f.Bot.Sent.Last().ReplyParameters?.MessageId);
        var deliveries = AllDeliveries(ChatA);
        Assert.True(deliveries.Single(d => d.RegionId == _key).IsClosed);
        Assert.False(deliveries.Single(d => d.RegionId == "kyiv").IsClosed);
    }

    [Fact]
    public async Task EndingTheRegionWideAlert_LeavesTheGroupThreadOpen()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        await GroupAsync(emergency: true);
        var regionDelivery = AllDeliveries(ChatA).Single(d => d.RegionId == "kyiv");

        var summary = await _f.DispatchAsync("");

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Equal(regionDelivery.TelegramMessageId, _f.Bot.Sent.Last().ReplyParameters?.MessageId);
        var deliveries = AllDeliveries(ChatA);
        Assert.True(deliveries.Single(d => d.RegionId == "kyiv").IsClosed);
        Assert.False(deliveries.Single(d => d.RegionId == _key).IsClosed);
    }

    [Fact]
    public async Task RegionWideDispatch_DoesNotCloseGroupDeliveryOfAnInactiveChat()
    {
        _f.AddChannel(ChatA, c => { c.GroupId = Group; c.IsActive = false; });
        _f.AddOpenDelivery(ChatA, 9, _key);

        await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.False(Assert.Single(AllDeliveries(ChatA)).IsClosed);
    }

    // ---- failures and stale threads ----

    [Fact]
    public async Task Telegram403_DeactivatesChannel_AndCountsAsFailed()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var summary = await GroupAsync(emergency: true);

        Assert.Equal(new(0, 0, 0, 1), summary);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
        Assert.Empty(AllDeliveries(ChatA));
    }

    [Fact]
    public async Task FailureForOneChat_DoesNotBlockOthers()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);
        _f.AddChannel(ChatB, c => c.GroupId = Group);
        _f.Bot.Failure = r => r is SendMessageRequest { ChatId.Identifier: ChatA }
            ? FakeTelegramBotClient.Forbidden()
            : null;

        var summary = await GroupAsync(emergency: true);

        Assert.Equal(new(1, 0, 0, 1), summary);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
        Assert.True(_f.LoadChannel(ChatB).IsActive);
        Assert.Single(AllDeliveries(ChatB));
    }

    [Fact]
    public async Task EndMessageFailure_StillClosesDelivery()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);
        await GroupAsync(emergency: true);
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var summary = await GroupAsync(emergency: false);

        Assert.Equal(new(0, 0, 0, 1), summary);
        Assert.True(Assert.Single(AllDeliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task StaleOpenDelivery_ForChatThatDroppedTheGroup_IsClosedSilently()
    {
        _f.AddChannel(ChatA, c => c.GroupId = "46.1");
        _f.AddOpenDelivery(ChatA, 9, _key);

        var summary = await GroupAsync(emergency: true);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.True(Assert.Single(AllDeliveries(ChatA)).IsClosed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleOpenDelivery_ForChatWithoutGroupOrInactive_IsClosedSilently(bool inactive)
    {
        _f.AddChannel(ChatA, c => c.IsActive = !inactive);
        _f.AddOpenDelivery(ChatA, 9, _key);

        await GroupAsync(emergency: true);

        Assert.Empty(_f.Bot.Requests);
        Assert.True(Assert.Single(AllDeliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task StaleOpenDelivery_ForChatWithNoChannelRow_IsClosedSilently()
    {
        _f.AddOpenDelivery(ChatA, 9, _key);

        await GroupAsync(emergency: false);

        Assert.Empty(_f.Bot.Requests);
        Assert.True(Assert.Single(AllDeliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task DispatchForOneGroup_DoesNotTouchDeliveriesOfAnotherGroup()
    {
        var otherKey = AlertDispatcher.GroupDeliveryKey("kyiv", "46.1");
        _f.AddChannel(ChatA, c => c.GroupId = "46.1");
        _f.AddOpenDelivery(ChatA, 9, otherKey);

        await GroupAsync(emergency: false);

        Assert.False(Assert.Single(AllDeliveries(ChatA)).IsClosed);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task GroupDeliveries_ArePersisted_NotJustTracked()
    {
        _f.AddChannel(ChatA, c => c.GroupId = Group);

        await GroupAsync(emergency: true);

        Assert.Equal(1, _f.Db.Deliveries.AsNoTracking().Count(d => d.RegionId == _key));
    }
}
