using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Requests;

namespace DtekBot.Tests;

public class ChannelPartIdListTests
{
    [Fact]
    public void Default_IsEmpty()
    {
        Assert.Empty(new Channel().PartIdList);
    }

    [Fact]
    public void Set_StoresCommaSeparated_AndGetReadsItBack()
    {
        var channel = new Channel { PartIdList = ["bucha", "fastiv"] };

        Assert.Equal("bucha,fastiv", channel.PartIds);
        Assert.Equal(["bucha", "fastiv"], channel.PartIdList);
    }

    [Fact]
    public void Set_Empty_ClearsPartIds()
    {
        var channel = new Channel { PartIds = "bucha" };

        channel.PartIdList = [];

        Assert.Null(channel.PartIds);
        Assert.Empty(channel.PartIdList);
    }

    [Fact]
    public void Set_RemovesDuplicates_KeepingTheFirstOccurrenceOrder()
    {
        var channel = new Channel { PartIdList = ["fastiv", "bucha", "fastiv", "bucha", "obukhiv"] };

        Assert.Equal("fastiv,bucha,obukhiv", channel.PartIds);
        Assert.Equal(["fastiv", "bucha", "obukhiv"], channel.PartIdList);
    }

    [Fact]
    public void Set_DuplicatesAreCaseSensitive()
    {
        var channel = new Channel { PartIdList = ["bucha", "BUCHA"] };

        Assert.Equal("bucha,BUCHA", channel.PartIds);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LegacyPartId_IsUsed_WhenPartIdsIsEmpty(string? partIds)
    {
        var channel = new Channel { PartId = "right-bank", PartIds = partIds };

        Assert.Equal(["right-bank"], channel.PartIdList);
    }

    [Fact]
    public void PartIds_WinsOverTheLegacyPartId()
    {
        var channel = new Channel { PartId = "right-bank", PartIds = "bucha" };

        Assert.Equal(["bucha"], channel.PartIdList);
    }

    [Fact]
    public void NoPartIds_AndNoLegacyPartId_MeansWholeRegion()
    {
        Assert.Empty(new Channel { PartId = null, PartIds = null }.PartIdList);
        Assert.Empty(new Channel { PartId = "", PartIds = null }.PartIdList);
    }

    [Fact]
    public void Get_TrimsAndSkipsEmptyEntries()
    {
        var channel = new Channel { PartIds = " bucha , fastiv,, obukhiv ," };

        Assert.Equal(["bucha", "fastiv", "obukhiv"], channel.PartIdList);
    }

    [Fact]
    public void Set_ThenLegacyPartIdStillPresent_ReappearsWhenCleared()
    {
        var channel = new Channel { PartId = "right-bank" };
        channel.PartIdList = ["bucha"];
        Assert.Equal(["bucha"], channel.PartIdList);

        channel.PartIdList = [];

        Assert.Equal(["right-bank"], channel.PartIdList);
    }

    [Fact]
    public void RoundTripsThroughTheDatabase()
    {
        using var f = new DispatcherFixture();
        f.AddChannel(10, c => c.PartIdList = ["bucha", "fastiv"], "kyiv-oblast");

        var loaded = f.LoadChannel(10);

        Assert.Equal("bucha,fastiv", loaded.PartIds);
        Assert.Equal(["bucha", "fastiv"], loaded.PartIdList);
    }

    [Fact]
    public void EmptyList_IsStoredAsNull()
    {
        using var f = new DispatcherFixture();
        f.AddChannel(10, c => c.PartIdList = []);

        Assert.Null(f.LoadChannel(10).PartIds);
    }
}

public sealed class DistrictMatchingTests : IDisposable
{
    private const long ChatA = 901;
    private const long ChatB = 902;
    private const long ChatC = 903;

    private const string Oblast = "kyiv-oblast";

    private const string BuchaText = "Через аварію знеструмлено Бучанський район.";
    private const string ObukhivFastivText = "Через аварію знеструмлено Обухівський та Фастівський райони.";
    private const string BrovaryText = "Через аварію знеструмлено Броварський район.";
    private const string WholeRegionText = "Через аварію знеструмлено частину споживачів Київщини.";

    private const string RealPopup =
        "Шановні клієнти! Через атаку на енергообʼєкти на Київщині зафіксовані аварійні відключення у Обухівському, Фастівському і Бучанському районах. "
        + "Енергетики вже працюють, щоб якомога швидше покращити ситуацію і повернути світло.";

    private readonly DispatcherFixture _f = new();
    private readonly RegionSettings _oblast;

    public DistrictMatchingTests()
    {
        _oblast = _f.Settings.Regions.Single(r => r.Id == Oblast);
    }

    public void Dispose() => _f.Dispose();

    private Channel AddChannel(long chatId, Action<Channel>? configure = null) =>
        _f.AddChannel(chatId, configure, Oblast);

    private Task<DispatchSummary> DispatchAsync(string text, StructuredPopup? structured = null)
    {
        var result = _f.Classifier.Classify(Oblast, text, structured);
        return _f.Dispatcher.DispatchAsync(_oblast, result, text, CancellationToken.None);
    }

    // ---- who gets what ----

    [Theory]
    [InlineData(BuchaText)]
    [InlineData(ObukhivFastivText)]
    [InlineData(BrovaryText)]
    [InlineData(WholeRegionText)]
    public async Task ChannelWithoutDistricts_GetsEveryAlert(string text)
    {
        AddChannel(ChatA);

        var summary = await DispatchAsync(text);

        Assert.Equal(new(1, 0, 0, 0), summary);
        Assert.Single(_f.Bot.SentTo(ChatA));
    }

    [Fact]
    public async Task ChannelWithADistrict_GetsAnAlertNamingIt()
    {
        AddChannel(ChatA, c => c.PartIdList = ["bucha"]);

        var summary = await DispatchAsync(BuchaText);

        Assert.Equal(new(1, 0, 0, 0), summary);
    }

    [Fact]
    public async Task ChannelWithADistrict_DoesNotGetAnAlertNamingOnlyOtherDistricts()
    {
        AddChannel(ChatA, c => c.PartIdList = ["bucha"]);

        var summary = await DispatchAsync(ObukhivFastivText);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
        Assert.Empty(_f.Deliveries(ChatA));
    }

    [Fact]
    public async Task AlertWithoutDistricts_ReachesChannelsWithDistrictsToo()
    {
        AddChannel(ChatA, c => c.PartIdList = ["bucha"]);
        AddChannel(ChatB, c => c.PartIdList = ["brovary", "fastiv"]);

        var summary = await DispatchAsync(WholeRegionText);

        Assert.Equal(new(2, 0, 0, 0), summary);
    }

    [Fact]
    public async Task StructuredAll_ReachesEveryChannel_EvenWhenTheTextNamesDistricts()
    {
        AddChannel(ChatA, c => c.PartIdList = ["brovary"]);

        var summary = await DispatchAsync(BuchaText, new StructuredPopup("all", null));

        Assert.Equal(new(1, 0, 0, 0), summary);
    }

    [Fact]
    public async Task StructuredDistricts_DecideWhoGetsIt()
    {
        AddChannel(ChatA, c => c.PartIdList = ["brovary"]);
        AddChannel(ChatB, c => c.PartIdList = ["bucha"]);

        await DispatchAsync(BuchaText, new StructuredPopup("districts", ["brovary"]));

        Assert.Single(_f.Bot.SentTo(ChatA));
        Assert.Empty(_f.Bot.SentTo(ChatB));
    }

    [Theory]
    [InlineData(BuchaText, true)]
    [InlineData(ObukhivFastivText, true)]
    [InlineData(BrovaryText, false)]
    [InlineData(WholeRegionText, true)]
    public async Task MultipleChosenDistricts_ReceiveOnAnyOverlap(string text, bool delivered)
    {
        AddChannel(ChatA, c => c.PartIdList = ["bucha", "fastiv"]);

        var summary = await DispatchAsync(text);

        Assert.Equal(delivered ? 1 : 0, summary.Sent);
    }

    [Fact]
    public async Task IdMatching_IsCaseInsensitive()
    {
        AddChannel(ChatA, c => c.PartIds = "BUCHA");

        var summary = await DispatchAsync(BuchaText);

        Assert.Equal(new(1, 0, 0, 0), summary);
    }

    // ---- legacy PartId ----

    [Fact]
    public async Task LegacyPartId_StillWorks()
    {
        AddChannel(ChatA, c => c.PartId = "bucha");
        AddChannel(ChatB, c => c.PartId = "brovary");

        await DispatchAsync(BuchaText);

        Assert.Single(_f.Bot.SentTo(ChatA));
        Assert.Empty(_f.Bot.SentTo(ChatB));
    }

    [Fact]
    public async Task LegacyPartId_IsIgnored_WhenPartIdsIsSet()
    {
        AddChannel(ChatA, c => { c.PartId = "brovary"; c.PartIds = "bucha"; });

        var brovary = await DispatchAsync(BrovaryText);
        var bucha = await DispatchAsync(BuchaText);

        Assert.Equal(0, brovary.Sent);
        Assert.Equal(1, bucha.Sent);
    }

    // ---- threads when the alert moves away ----

    [Fact]
    public async Task OpenDelivery_GetsTheEndedReply_WhenTheNewTextNoLongerNamesItsDistrict()
    {
        AddChannel(ChatA, c => c.PartIdList = ["bucha"]);
        await DispatchAsync(BuchaText);
        var original = Assert.Single(_f.Deliveries(ChatA));

        var summary = await DispatchAsync(ObukhivFastivText);

        Assert.Equal(new(0, 0, 1, 0), summary);
        var reply = _f.Bot.Sent.Last();
        Assert.Equal(original.TelegramMessageId, reply.ReplyParameters?.MessageId);
        Assert.Contains("✅", reply.Text);
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    [Fact]
    public async Task OpenDelivery_IsEditedInPlace_WhenTheAlertStillNamesTheDistrict()
    {
        AddChannel(ChatA, c => c.PartIdList = ["bucha"]);
        await DispatchAsync(BuchaText);

        var summary = await DispatchAsync("Через аварію знеструмлено Бучанський та Обухівський райони.");

        Assert.Equal(new(0, 1, 0, 0), summary);
        Assert.Single(_f.Bot.Edits);
        Assert.Single(_f.Bot.Sent);
    }

    [Fact]
    public async Task OpenDelivery_StaysOpen_WhenTheAlertTurnsIntoAWholeRegionOne()
    {
        AddChannel(ChatA, c => c.PartIdList = ["bucha"]);
        await DispatchAsync(BuchaText);

        var summary = await DispatchAsync(WholeRegionText);

        Assert.Equal(new(0, 1, 0, 0), summary);
        Assert.False(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    // ---- full flow with the real popup ----

    [Fact]
    public async Task RealPopup_FullFlow_ReachesBucha_NotBrovary_AndUnscopedChannels()
    {
        AddChannel(ChatA, c => c.PartIdList = ["bucha"]);
        AddChannel(ChatB, c => c.PartIdList = ["brovary"]);
        AddChannel(ChatC);

        var summary = await DispatchAsync(RealPopup);

        Assert.Equal(new(2, 0, 0, 0), summary);
        Assert.Single(_f.Bot.SentTo(ChatA));
        Assert.Empty(_f.Bot.SentTo(ChatB));
        Assert.Single(_f.Bot.SentTo(ChatC));
        Assert.Empty(_f.Deliveries(ChatB));
    }

    [Fact]
    public async Task RealPopup_AccidentMessage_NamesTheThreeDistricts()
    {
        AddChannel(ChatA, c => c.PartIdList = ["bucha"]);

        await DispatchAsync(RealPopup);

        var text = Assert.Single(_f.Bot.SentTo(ChatA)).Text;
        Assert.Contains("Бучанський район", text);
        Assert.Contains("Обухівський район", text);
        Assert.Contains("Фастівський район", text);
        Assert.DoesNotContain("Броварський район", text);
        Assert.Contains("⚠️", text);
    }

    [Fact]
    public async Task RealPopup_ThenAllClear_EndsTheThreadsOfTheReachedChannelsOnly()
    {
        AddChannel(ChatA, c => c.PartIdList = ["bucha"]);
        AddChannel(ChatB, c => c.PartIdList = ["brovary"]);
        await DispatchAsync(RealPopup);

        var summary = await DispatchAsync("");

        Assert.Equal(new(0, 0, 1, 0), summary);
        Assert.Empty(_f.Bot.SentTo(ChatB));
        Assert.True(Assert.Single(_f.Deliveries(ChatA)).IsClosed);
    }

    // ---- Kyiv: banks stand for their districts ----

    [Fact]
    public async Task Kyiv_RightBankAlert_ReachesDistrictAndBankChannels_ButNotLeftBank()
    {
        var kyiv = _f.Kyiv;
        _f.AddChannel(ChatA, c => c.PartIdList = ["obolonskyi"]);
        _f.AddChannel(ChatB, c => c.PartIdList = ["right-bank"]);
        _f.AddChannel(ChatC, c => c.PartIdList = ["left-bank", "darnytskyi"]);

        var result = _f.Classifier.Classify("kyiv", DispatcherFixture.AccidentRightBankText);
        var summary = await _f.Dispatcher.DispatchAsync(kyiv, result, DispatcherFixture.AccidentRightBankText, CancellationToken.None);

        Assert.Equal(new(2, 0, 0, 0), summary);
        Assert.Single(_f.Bot.SentTo(ChatA));
        Assert.Single(_f.Bot.SentTo(ChatB));
        Assert.Empty(_f.Bot.SentTo(ChatC));
    }

    [Fact]
    public async Task Kyiv_SingleDistrictAlert_DoesNotReachTheWholeBankChannel()
    {
        _f.AddChannel(ChatA, c => c.PartIdList = ["right-bank"]);
        _f.AddChannel(ChatB, c => c.PartIdList = ["darnytskyi"]);

        const string text = "Через аварію знеструмлено споживачів у Дарницькому районі.";
        var result = _f.Classifier.Classify("kyiv", text);
        var summary = await _f.Dispatcher.DispatchAsync(_f.Kyiv, result, text, CancellationToken.None);

        Assert.Equal(new(1, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.SentTo(ChatA));
        Assert.Single(_f.Bot.SentTo(ChatB));
    }

    [Fact]
    public async Task GroupAlerts_AreNotAffectedByDistricts()
    {
        AddChannel(ChatA, c => { c.PartIdList = ["bucha"]; c.GroupId = "45.1"; });

        var summary = await _f.Dispatcher.DispatchGroupAsync(_oblast, "45.1", true, CancellationToken.None);

        Assert.Equal(new(1, 0, 0, 0), summary);
        Assert.Single(_f.Bot.Requests.OfType<SendMessageRequest>());
    }
}
