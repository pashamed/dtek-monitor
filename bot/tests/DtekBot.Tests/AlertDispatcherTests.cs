using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Shared;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DtekBot.Tests;

public sealed class AlertDispatcherTests : IDisposable
{
    private const long ChatA = 101;
    private const long ChatB = 102;

    private readonly DispatcherFixture _f = new();

    public void Dispose() => _f.Dispose();

    [Fact]
    public async Task NewAlert_SendsOneMessageToEachActiveMatchingChannel_AndCreatesDeliveries()
    {
        _f.AddChannel(ChatA);
        _f.AddChannel(ChatB);

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(2, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Requests.Count);
        Assert.Single(_f.Bot.SentTo(ChatA));
        Assert.Single(_f.Bot.SentTo(ChatB));

        var expectedHash = _f.Classifier.Classify("kyiv", DispatcherFixture.EmergencyText).Hash;
        var delivery = Assert.Single(_f.Deliveries(ChatA));
        Assert.False(delivery.IsClosed);
        Assert.Equal("kyiv", delivery.RegionId);
        Assert.Equal(expectedHash, delivery.ContentHash);
        Assert.True(delivery.TelegramMessageId >= 1000);
        Assert.Single(_f.Deliveries(ChatB));
    }

    [Fact]
    public async Task NewAlert_DefaultTemplate_RendersEmojiRegionAndSite()
    {
        _f.AddChannel(ChatA);

        await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        var text = Assert.Single(_f.Bot.Sent).Text;
        Assert.Contains("🚨", text);
        Assert.Contains("м. Київ", text);
        Assert.Contains(_f.Kyiv.SiteUrl, text);
    }

    [Fact]
    public async Task NewAlert_SendsHtmlWithoutLinkPreview()
    {
        _f.AddChannel(ChatA);

        await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        var request = Assert.Single(_f.Bot.Sent);
        Assert.Equal(ParseMode.Html, request.ParseMode);
        Assert.True(request.LinkPreviewOptions?.IsDisabled);
        Assert.Null(request.ReplyParameters);
    }

    [Fact]
    public async Task IdenticalSecondDispatch_IsNoOp()
    {
        _f.AddChannel(ChatA);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        var requestsAfterFirst = _f.Bot.Requests.Count;

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Equal(requestsAfterFirst, _f.Bot.Requests.Count);
        Assert.Single(_f.Deliveries(ChatA));
    }

    [Fact]
    public async Task ChangedText_EditsTheSameMessage_WhenEditInPlace()
    {
        _f.AddChannel(ChatA, c => c.EditInPlace = true);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        var original = Assert.Single(_f.Deliveries(ChatA));

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText + " Оновлення: діє до 23:00.");

        Assert.Equal(new(0, 1, 0, 0), summary);
        Assert.Single(_f.Bot.Sent);
        var edit = Assert.Single(_f.Bot.Edits);
        Assert.Equal(ChatA, edit.ChatId.Identifier);
        Assert.Equal(original.TelegramMessageId, edit.MessageId);
        Assert.Equal(ParseMode.Html, edit.ParseMode);

        var updated = Assert.Single(_f.Deliveries(ChatA));
        Assert.Equal(original.TelegramMessageId, updated.TelegramMessageId);
        Assert.NotEqual(original.ContentHash, updated.ContentHash);
        Assert.False(updated.IsClosed);
    }

    [Fact]
    public async Task ChangedText_SendsNewMessage_WhenNotEditInPlace()
    {
        _f.AddChannel(ChatA, c => c.EditInPlace = false);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        var original = Assert.Single(_f.Deliveries(ChatA));

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText + " Оновлення: діє до 23:00.");

        Assert.Equal(new(1, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Edits);
        Assert.Equal(2, _f.Bot.Sent.Count());

        var updated = Assert.Single(_f.Deliveries(ChatA));
        Assert.NotEqual(original.TelegramMessageId, updated.TelegramMessageId);
        Assert.False(updated.IsClosed);
    }

    [Theory]
    [InlineData(DispatcherFixture.StabilizationText)]
    [InlineData("")]
    public async Task AlertEnding_SendsReplyToDeliveryMessage_AndClosesDelivery(string endText)
    {
        _f.AddChannel(ChatA);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        var original = Assert.Single(_f.Deliveries(ChatA));

        var summary = await _f.DispatchAsync(endText);

        Assert.Equal(new(0, 0, 1, 0), summary);
        var reply = _f.Bot.Sent.Last();
        Assert.Equal(2, _f.Bot.Sent.Count());
        Assert.Equal(original.TelegramMessageId, reply.ReplyParameters?.MessageId);
        Assert.Contains("ℹ️", reply.Text);
        Assert.Equal(ParseMode.Html, reply.ParseMode);

        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task AlertEnding_ThenNewAlert_StartsAFreshThread()
    {
        _f.AddChannel(ChatA);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        await _f.DispatchAsync("");

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(1, 0, 0, 0), summary);
        var deliveries = _f.Deliveries(ChatA);
        Assert.Equal(2, deliveries.Count);
        Assert.True(deliveries[0].IsClosed);
        Assert.False(deliveries[1].IsClosed);
        Assert.Null(_f.Bot.Sent.Last().ReplyParameters);
    }

    [Fact]
    public async Task AlertEnding_WithEndedSlotOff_ClosesDeliveryWithoutMessage()
    {
        _f.AddChannel(ChatA, c => c.SlotSettingMap = Slots(("ended", false, true)));
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        var summary = await _f.DispatchAsync("");

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Requests);
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task AlertEnding_NewStyle_SendsAPlainMessageWithoutReply()
    {
        _f.AddChannel(ChatA, c => c.SlotSettingMap = EndStyleSlots(EndStyle.New));
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        var summary = await _f.DispatchAsync("");

        Assert.Equal(new(0, 0, 1, 0), summary);
        var notice = _f.Bot.Sent.Last();
        Assert.Equal(2, _f.Bot.Sent.Count());
        Assert.Null(notice.ReplyParameters);
        Assert.Contains("ℹ️", notice.Text);
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task AlertEnding_EditStyle_EditsTheOriginalMessage_AndClosesDelivery()
    {
        _f.AddChannel(ChatA, c => c.SlotSettingMap = EndStyleSlots(EndStyle.Edit));
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        var original = Assert.Single(_f.Deliveries(ChatA));

        var summary = await _f.DispatchAsync("");

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Sent);
        var edit = Assert.Single(_f.Bot.Edits);
        Assert.Equal(original.TelegramMessageId, edit.MessageId);
        Assert.Contains("ℹ️", edit.Text);
        Assert.Equal(ParseMode.Html, edit.ParseMode);
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task AlertEnding_EditStyle_FallsBackToAReply_WhenTheOriginalIsGone()
    {
        _f.AddChannel(ChatA, c => c.SlotSettingMap = EndStyleSlots(EndStyle.Edit));
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        var original = Assert.Single(_f.Deliveries(ChatA));
        _f.Bot.Failure = r => r is EditMessageTextRequest ? FakeTelegramBotClient.MessageToEditNotFound() : null;

        var summary = await _f.DispatchAsync("");

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Equal(2, _f.Bot.Sent.Count());
        Assert.Equal(original.TelegramMessageId, _f.Bot.Sent.Last().ReplyParameters?.MessageId);
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task AlertEnding_EditStyle_NotModified_CountsAsDone()
    {
        _f.AddChannel(ChatA, c => c.SlotSettingMap = EndStyleSlots(EndStyle.Edit));
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        _f.Bot.Failure = r => r is EditMessageTextRequest ? FakeTelegramBotClient.NotModified() : null;

        var summary = await _f.DispatchAsync("");

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Sent);
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task AlertEnding_EditStyle_RetriesWithoutTags_WhenTheTemplateMarkupIsRejected()
    {
        _f.AddChannel(ChatA, c =>
        {
            c.SlotSettingMap = EndStyleSlots(EndStyle.Edit);
            c.Template = "<b>{message}</b>";
        });
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        var calls = 0;
        _f.Bot.Failure = r => r is EditMessageTextRequest && calls++ == 0 ? FakeTelegramBotClient.CantParseEntities() : null;

        var summary = await _f.DispatchAsync("");

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Equal(2, _f.Bot.Edits.Count());
        Assert.Single(_f.Bot.Sent);
    }

    private static Dictionary<string, SlotSettingDto> EndStyleSlots(EndStyle style) =>
        new() { ["ended"] = new SlotSettingDto(true, true, style) };

    private static Dictionary<string, SlotSettingDto> Slots(params (string Slot, bool Enabled, bool EditInPlace)[] items) =>
        items.ToDictionary(i => i.Slot, i => new SlotSettingDto(i.Enabled, i.EditInPlace));

    [Fact]
    public async Task LegacyNotifyOnEndOff_StillDisablesTheEndedSlot_WhenNoSlotSettings()
    {
        _f.AddChannel(ChatA, c => c.NotifyOnEnd = false);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        await _f.DispatchAsync("");

        Assert.Single(_f.Bot.Requests);
    }

    [Fact]
    public async Task DisabledEmergencySlot_GetsNothing_ButRegionalStillArrives()
    {
        _f.AddChannel(ChatA, c => c.SlotSettingMap = Slots(("emergency", false, true)));

        Assert.Equal(new(0, 0, 0, 0), await _f.DispatchAsync(DispatcherFixture.EmergencyText));
        Assert.Equal(new(0, 0, 0, 0), await _f.DispatchAsync(DispatcherFixture.UnknownText));
        Assert.Empty(_f.Bot.Requests);

        Assert.Equal(new(1, 0, 0, 0), await _f.DispatchAsync(DispatcherFixture.AccidentRightBankText));
    }

    [Fact]
    public async Task SlotSwitchedOff_WithOpenThread_ClosesItSilently()
    {
        var channel = _f.AddChannel(ChatA);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        channel.SlotSettingMap = Slots(("emergency", false, true));
        _f.Db.SaveChanges();

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Requests);
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task EditInPlace_IsPerSlot()
    {
        _f.AddChannel(ChatA, c => c.SlotSettingMap = Slots(("emergency", true, false), ("regional", true, true)));
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(1, 0, 0, 0), await _f.DispatchAsync(DispatcherFixture.EmergencyText + " Оновлення: діє до 23:00."));
        Assert.Empty(_f.Bot.Edits);
    }

    [Fact]
    public void LegacyColumns_ProvideTheDefaults()
    {
        var channel = new Channel { NotifyOnEnd = false, EditInPlace = false };

        Assert.True(channel.SlotEnabled("emergency"));
        Assert.True(channel.SlotEnabled("local"));
        Assert.False(channel.SlotEnabled("ended"));
        Assert.False(channel.SlotEditInPlace("regional"));

        channel.SlotSettingMap = Slots(("ended", true, false), ("regional", true, true));
        Assert.True(channel.SlotEnabled("ended"));
        Assert.True(channel.SlotEditInPlace("regional"));
        Assert.False(channel.SlotEditInPlace("emergency"));
    }

    [Fact]
    public async Task NoAlertAndNoDelivery_DoesNothing()
    {
        _f.AddChannel(ChatA);

        var summary = await _f.DispatchAsync("");

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task PausedChannel_GetsNothing()
    {
        _f.AddChannel(ChatA, c => c.IsPaused = true);

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.Empty(_f.Deliveries(ChatA));
    }

    [Fact]
    public async Task PausedChannel_WithOpenDelivery_IsClosedSilently()
    {
        var channel = _f.AddChannel(ChatA);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        channel.IsPaused = true;
        _f.Db.SaveChanges();

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Single(_f.Bot.Requests);
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    [Theory]
    [InlineData(DispatcherFixture.EmergencyText)]
    [InlineData(DispatcherFixture.AccidentRightBankText)]
    [InlineData(DispatcherFixture.UnknownText)]
    public async Task LegacyFilter_IsRetired_EveryAlertKindIsDelivered(string text)
    {
        _f.AddChannel(ChatA, c => c.Filter = AlertFilter.EmergencyOnly);

        var summary = await _f.DispatchAsync(text);

        Assert.Equal(new(1, 0, 0, 0), summary);
        Assert.Single(_f.Bot.Sent);
    }

    [Fact]
    public async Task AllFilter_DeliversUnknownAlerts()
    {
        _f.AddChannel(ChatA, c => c.Filter = AlertFilter.All);

        var summary = await _f.DispatchAsync(DispatcherFixture.UnknownText);

        Assert.Equal(new(1, 0, 0, 0), summary);
        Assert.Contains("ℹ", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task PartFilter_AccidentOnRightBank_ReachesRightBankAndUnscopedChannelsOnly()
    {
        _f.AddChannel(ChatA, c => c.PartId = "right-bank");
        _f.AddChannel(ChatB, c => c.PartId = "left-bank");
        _f.AddChannel(103, c => c.PartId = null);

        var summary = await _f.DispatchAsync(DispatcherFixture.AccidentRightBankText);

        Assert.Equal(new(2, 0, 0, 0), summary);
        Assert.Single(_f.Bot.SentTo(ChatA));
        Assert.Empty(_f.Bot.SentTo(ChatB));
        Assert.Single(_f.Bot.SentTo(103));
    }

    [Fact]
    public async Task PartFilter_IsCaseInsensitive()
    {
        _f.AddChannel(ChatA, c => c.PartId = "RIGHT-BANK");

        var summary = await _f.DispatchAsync(DispatcherFixture.AccidentRightBankText);

        Assert.Equal(new(1, 0, 0, 0), summary);
    }

    [Fact]
    public async Task PartFilter_AccidentOnBothBanks_ReachesBothBanks()
    {
        _f.AddChannel(ChatA, c => c.PartId = "right-bank");
        _f.AddChannel(ChatB, c => c.PartId = "left-bank");

        var summary = await _f.DispatchAsync(DispatcherFixture.AccidentBothBanksText);

        Assert.Equal(new(2, 0, 0, 0), summary);
    }

    [Fact]
    public async Task PartFilter_AlertWithoutParts_ReachesEveryPart()
    {
        _f.AddChannel(ChatA, c => c.PartId = "right-bank");
        _f.AddChannel(ChatB, c => c.PartId = "left-bank");

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(2, 0, 0, 0), summary);
    }

    [Fact]
    public async Task PartFilter_AlertMovesToOtherBank_EndsThreadForTheUnaffectedChannel()
    {
        _f.AddChannel(ChatA, c => c.PartId = "right-bank");
        await _f.DispatchAsync(DispatcherFixture.AccidentRightBankText);
        var original = Assert.Single(_f.Deliveries(ChatA));

        var summary = await _f.DispatchAsync(DispatcherFixture.AccidentLeftBankText);

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Equal(original.TelegramMessageId, _f.Bot.Sent.Last().ReplyParameters?.MessageId);
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task InactiveChannel_GetsNothing()
    {
        _f.AddChannel(ChatA, c => c.IsActive = false);

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task ChannelOfAnotherRegion_GetsNothing_AndItsDeliveriesAreUntouched()
    {
        _f.AddChannel(ChatA, regionId: "odesa");
        var odesaDelivery = _f.AddOpenDelivery(ChatA, 7, "odesa");

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.False(_f.Deliveries(ChatA).Single(d => d.Id == odesaDelivery.Id).IsClosed);
    }

    [Fact]
    public async Task StaleOpenDelivery_ForInactiveChat_IsClosedSilently()
    {
        _f.AddChannel(ChatA, c => c.IsActive = false);
        _f.AddOpenDelivery(ChatA, 7);

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task CustomTemplate_IsRendered_AndSentAsHtml()
    {
        _f.AddChannel(ChatA, c =>
        {
            c.PartId = "obolonskyi";
            c.Template = "<b>{where}</b>";
        });

        await _f.DispatchAsync(DispatcherFixture.AccidentRightBankText);

        var request = Assert.Single(_f.Bot.Sent);
        Assert.Equal(
            "<b>м. Київ (Правий берег)</b>",
            request.Text);
        Assert.Equal(ParseMode.Html, request.ParseMode);
    }

    [Fact]
    public async Task CustomTemplate_PopupValueIsHtmlEscaped()
    {
        _f.AddChannel(ChatA, c => c.Template = "{popup}");

        await _f.DispatchAsync("Через аварію <b>x</b> & знеструмлено");

        Assert.Equal("Через аварію &lt;b&gt;x&lt;/b&gt; &amp; знеструмлено", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task Telegram403_DeactivatesChannel_AndCountsAsFailed()
    {
        _f.AddChannel(ChatA);
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 0, 1), summary);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
        Assert.Empty(_f.Deliveries(ChatA));
    }

    [Fact]
    public async Task Telegram400ChatNotFound_DeactivatesChannel()
    {
        _f.AddChannel(ChatA);
        _f.Bot.Failure = _ => new ApiRequestException("Bad Request: chat not found", 400);

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 0, 1), summary);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
    }

    [Fact]
    public async Task FailureForOneChat_DoesNotBlockOthers()
    {
        _f.AddChannel(ChatA);
        _f.AddChannel(ChatB);
        _f.Bot.Failure = r => r is SendMessageRequest { ChatId.Identifier: ChatA }
            ? FakeTelegramBotClient.Forbidden()
            : null;

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(1, 0, 0, 1), summary);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
        Assert.True(_f.LoadChannel(ChatB).IsActive);
        Assert.Single(_f.Deliveries(ChatB));
    }

    [Fact]
    public async Task GenericFailure_CountsAsFailed_ButKeepsChannelActive()
    {
        _f.AddChannel(ChatA);
        _f.Bot.Failure = _ => new HttpRequestException("network down");

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 0, 1), summary);
        Assert.True(_f.LoadChannel(ChatA).IsActive);
        Assert.Empty(_f.Deliveries(ChatA));
    }

    [Fact]
    public async Task ParseEntitiesError_WithCustomTemplate_RetriesOnceAsPlainText_AndStillDelivers()
    {
        _f.AddChannel(ChatA, c => c.Template = "<b>{where}</b> {emoji}");
        var calls = 0;
        _f.Bot.Failure = _ => calls++ == 0 ? FakeTelegramBotClient.CantParseEntities() : null;

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Sent.Count());
        Assert.Equal("<b>м. Київ</b> 🚨", _f.Bot.Sent.First().Text);
        Assert.Equal("м. Київ 🚨", _f.Bot.Sent.Last().Text);
        Assert.True(_f.LoadChannel(ChatA).IsActive);
        Assert.Single(_f.Deliveries(ChatA));
    }

    [Fact]
    public async Task ParseEntitiesError_Fallback_DoesNotDoubleEscapeValues()
    {
        _f.AddChannel(ChatA, c => c.Template = "<b>{popup}</b>");
        var calls = 0;
        _f.Bot.Failure = _ => calls++ == 0 ? FakeTelegramBotClient.CantParseEntities() : null;

        await _f.DispatchAsync("Аварія A & B");

        Assert.Equal("Аварія A &amp; B", _f.Bot.Sent.Last().Text);
    }

    [Fact]
    public async Task ParseEntitiesError_WithCustomTemplate_FailingAgain_IsFailedButChannelStaysActive()
    {
        _f.AddChannel(ChatA, c => c.Template = "<b>{where}</b>");
        _f.Bot.Failure = _ => FakeTelegramBotClient.CantParseEntities();

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 0, 1), summary);
        Assert.Equal(2, _f.Bot.Sent.Count());
        Assert.True(_f.LoadChannel(ChatA).IsActive);
    }

    [Fact]
    public async Task ParseEntitiesError_WithDefaultTemplate_IsNotRetried()
    {
        _f.AddChannel(ChatA);
        _f.Bot.Failure = _ => FakeTelegramBotClient.CantParseEntities();

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 0, 1), summary);
        Assert.Single(_f.Bot.Sent);
        Assert.True(_f.LoadChannel(ChatA).IsActive);
    }

    [Fact]
    public async Task RateLimit429_IsRetriedOnce()
    {
        _f.AddChannel(ChatA);
        var calls = 0;
        _f.Bot.Failure = _ => calls++ == 0
            ? new ApiRequestException("Too Many Requests: retry after 0", 429, new ResponseParameters { RetryAfter = 0 })
            : null;

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(1, 0, 0, 0), summary);
        Assert.Equal(2, _f.Bot.Sent.Count());
    }

    [Fact]
    public async Task EditFailsWithMessageNotModified_IsTreatedAsUnchanged()
    {
        _f.AddChannel(ChatA);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        _f.Bot.Failure = r => r is EditMessageTextRequest ? FakeTelegramBotClient.NotModified() : null;

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText + " Додатково.");

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.True(_f.LoadChannel(ChatA).IsActive);
    }

    [Fact]
    public async Task EditFailsWith403_DeactivatesChannel()
    {
        _f.AddChannel(ChatA);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        _f.Bot.Failure = r => r is EditMessageTextRequest ? FakeTelegramBotClient.Forbidden() : null;

        var summary = await _f.DispatchAsync(DispatcherFixture.EmergencyText + " Додатково.");

        Assert.Equal(new(0, 0, 0, 1), summary);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
    }

    [Fact]
    public async Task EndMessageFailure_StillClosesDelivery_SoItIsNotRetriedForever()
    {
        _f.AddChannel(ChatA);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var summary = await _f.DispatchAsync("");

        Assert.Equal(new(0, 0, 0, 1), summary);
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
        Assert.False(_f.LoadChannel(ChatA).IsActive);
    }
}
