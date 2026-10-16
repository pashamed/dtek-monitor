using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Grid;
using DtekBot.Api.Parsing;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Requests;

namespace DtekBot.Tests;

public sealed class FakeGridProvider : IGridStatusProvider
{
    public GridFeed? Feed { get; set; }

    public int Calls { get; private set; }

    public Task<GridFeed?> GetAsync(CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(Feed);
    }
}

// Both sources through the real application: the DTEK popup via POST /ingest, Ukrenergo via POST /ingest/grid/tick.
public sealed class GridAlertTests : IDisposable
{
    private const long All = 9001;
    private const long Darnytskyi = 9002;
    private const long Holosiivskyi = 9003;
    private const string Tag = "(за даними Укренерго)";
    private const string DtekTag = "(за даними ДТЕК)";

    private readonly ApiFactory _f = new();
    private readonly HttpClient _client;

    public GridAlertTests()
    {
        _client = _f.CreateClient();
        _client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
    }

    public void Dispose()
    {
        _client.Dispose();
        _f.Dispose();
    }

    // ---- helpers ----

    private void AddChannel(long chatId, string[]? districts = null, bool editInPlace = true, string regionId = "kyiv", string? template = null) =>
        _f.WithDb(db => db.Channels.Add(new Channel
        {
            ChatId = chatId,
            RegionId = regionId,
            CreatedAt = _f.Clock.Now,
            EditInPlace = editInPlace,
            PartIdList = districts ?? [],
            Template = template,
        }));

    private void SetGrid(string status, TimeSpan? age = null, DateTimeOffset? since = null)
    {
        var generated = _f.Clock.Now - (age ?? TimeSpan.Zero);
        var regions = new Dictionary<string, GridRegionStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var slug in new[] { "kyiv", "kyiv-oblast", "dnipropetrovsk", "odesa", "donetsk" })
        {
            var s = slug == "kyiv" ? status : GridStatuses.Normal;
            regions[slug] = new GridRegionStatus(slug, s, $"Статус {s}", slug == "kyiv" ? since ?? _f.Clock.Now.AddMinutes(-5) : null);
        }

        _f.Grid.Feed = new GridFeed(generated, regions);
    }

    private async Task<JsonElement> TickAsync()
    {
        var response = await _client.PostAsync("/ingest/grid/tick", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> IngestAsync(string? text, params string[] districts)
    {
        var response = await _client.PostAsJsonAsync("/ingest", new
        {
            regionId = "kyiv",
            popupText = text,
            structured = districts.Length == 0 ? null : new { scope = "districts", districts },
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(body).RootElement;
    }

    private List<SendMessageRequest> Sent(long chat) => _f.Bot.SentTo(chat).ToList();

    private List<EditMessageTextRequest> Edits(long chat) => _f.Bot.Edits.Where(e => e.ChatId.Identifier == chat).ToList();

    private List<Delivery> Deliveries(long chat) =>
        _f.WithDb(db => db.Deliveries.AsNoTracking().Where(d => d.ChatId == chat).OrderBy(d => d.Id).ToList());

    // ---- status mapping ----

    [Theory]
    [InlineData("emergency_outages", PopupKind.Emergency)]
    [InlineData("grid_damage", PopupKind.Accident)]
    [InlineData("normal", PopupKind.None)]
    [InlineData("shortage", PopupKind.None)]
    [InlineData("outage_schedules", PopupKind.None)]
    [InlineData("something_new", PopupKind.None)]
    public void Status_MapsToKind(string status, PopupKind expected) =>
        Assert.Equal(expected, GridStatuses.ToKind(status));

    [Theory]
    [InlineData("normal")]
    [InlineData("shortage")]
    [InlineData("outage_schedules")]
    public async Task NonEmergencyStatuses_PostNothing(string status)
    {
        AddChannel(All);
        SetGrid(status);

        var result = await TickAsync();

        Assert.Equal("ok", result.GetProperty("feed").GetString());
        Assert.Empty(_f.Bot.Sent);
    }

    // ---- start by Ukrenergo ----

    [Fact]
    public async Task UkrenergoEmergency_PostsAtOnce_ToEverySubscriber_IncludingDistrictOnes()
    {
        AddChannel(All, template: "{message}\n{popup}");
        AddChannel(Darnytskyi, ["darnytskyi"], template: "{message}\n{popup}");
        AddChannel(Holosiivskyi, ["holosiivskyi"], template: "{message}\n{popup}");
        AddChannel(9100, regionId: "odesa");
        SetGrid(GridStatuses.EmergencyOutages, since: new DateTimeOffset(2026, 10, 15, 8, 30, 0, TimeSpan.Zero));

        await TickAsync();

        foreach (var chat in new[] { All, Darnytskyi, Holosiivskyi })
        {
            var message = Assert.Single(Sent(chat));
            Assert.Contains(Tag, message.Text);
            Assert.Contains("введені екстрені відключення", message.Text);
            Assert.Contains("Укренерго: Статус emergency_outages (з 15.10 11:30)", message.Text);
            Assert.Equal("grid:emergency_outages", Assert.Single(Deliveries(chat)).ContentHash);
        }

        Assert.Empty(Sent(9100));
    }

    [Fact]
    public async Task UkrenergoGridDamage_PostsAnAccident()
    {
        AddChannel(All);
        SetGrid(GridStatuses.GridDamage);

        await TickAsync();

        var message = Assert.Single(Sent(All));
        Assert.Contains("аварія в енергосистемі", message.Text);
        Assert.Contains(Tag, message.Text);
    }

    [Fact]
    public async Task RepeatedTicks_DoNotPostAgain()
    {
        AddChannel(All);
        SetGrid(GridStatuses.EmergencyOutages);

        await TickAsync();
        await TickAsync();
        await TickAsync();

        Assert.Single(Sent(All));
        Assert.Empty(Edits(All));
    }

    // ---- unknown feed state ----

    [Fact]
    public async Task StaleFeed_IsIgnored_AndOpensNothing()
    {
        AddChannel(All);
        SetGrid(GridStatuses.EmergencyOutages, age: TimeSpan.FromMinutes(46));

        var result = await TickAsync();

        Assert.Equal("stale", result.GetProperty("feed").GetString());
        Assert.Empty(_f.Bot.Sent);
        Assert.Empty(_f.WithDb(db => db.GridStates.ToList()));
    }

    [Fact]
    public async Task FeedJustInsideTheLimit_IsUsed()
    {
        AddChannel(All);
        SetGrid(GridStatuses.EmergencyOutages, age: TimeSpan.FromMinutes(44));

        await TickAsync();

        Assert.Single(Sent(All));
    }

    [Fact]
    public async Task StaleFeed_NeverClosesAnOpenAlert()
    {
        AddChannel(All);
        SetGrid(GridStatuses.EmergencyOutages);
        await TickAsync();

        SetGrid(GridStatuses.Normal, age: TimeSpan.FromMinutes(120));
        await TickAsync();

        Assert.Single(Sent(All));
        Assert.False(Assert.Single(Deliveries(All)).IsClosed);
        Assert.Equal(GridStatuses.EmergencyOutages, _f.WithDb(db => db.GridStates.Single(g => g.RegionId == "kyiv").Status));
    }

    [Fact]
    public async Task UnavailableFeed_LeavesTheStateAlone()
    {
        AddChannel(All);
        SetGrid(GridStatuses.EmergencyOutages);
        await TickAsync();

        _f.Grid.Feed = null;
        var result = await TickAsync();

        Assert.Equal("unavailable", result.GetProperty("feed").GetString());
        Assert.Single(Sent(All));
        Assert.False(Assert.Single(Deliveries(All)).IsClosed);
        Assert.Equal(GridStatuses.EmergencyOutages, _f.WithDb(db => db.GridStates.Single(g => g.RegionId == "kyiv").Status));
    }

    // ---- DTEK arrives later ----

    [Fact]
    public async Task DtekPopupLater_EditsTheSameMessage_InPlace()
    {
        AddChannel(All);
        SetGrid(GridStatuses.EmergencyOutages);
        await TickAsync();
        var original = Deliveries(All).Single().TelegramMessageId;

        await IngestAsync(DispatcherFixture.EmergencyText);

        Assert.Single(Sent(All));
        var edit = Assert.Single(Edits(All));
        Assert.Equal(original, edit.MessageId);
        Assert.Contains(DtekTag, edit.Text);
        Assert.DoesNotContain(Tag, edit.Text);
        Assert.False(Assert.Single(Deliveries(All)).IsClosed);
    }

    [Fact]
    public async Task DtekPopupLater_PostsANewMessage_WhenTheChatDoesNotEditInPlace()
    {
        AddChannel(All, editInPlace: false);
        SetGrid(GridStatuses.EmergencyOutages);
        await TickAsync();

        await IngestAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(2, Sent(All).Count);
        Assert.Empty(Edits(All));
        Assert.Contains(DtekTag, Sent(All)[1].Text);
    }

    [Fact]
    public async Task DtekPopupFirst_IsPostedAsBefore_AndNotTagged_WhenNoUkrenergoStateIsKnown()
    {
        AddChannel(All);

        var result = await IngestAsync(DispatcherFixture.EmergencyText);

        Assert.True(result.GetProperty("changed").GetBoolean());
        Assert.Equal("Emergency", result.GetProperty("kind").GetString());
        Assert.Equal(1, result.GetProperty("dispatch").GetProperty("sent").GetInt32());
        var message = Assert.Single(Sent(All));
        Assert.DoesNotContain("за даними", message.Text);
    }

    [Fact]
    public async Task DtekPopupFirst_IsNamedAsDtek_OnceTheUkrenergoStateIsKnown()
    {
        AddChannel(All);
        SetGrid(GridStatuses.Normal);
        await TickAsync();

        await IngestAsync(DispatcherFixture.EmergencyText);

        Assert.Contains(DtekTag, Assert.Single(Sent(All)).Text);
    }

    [Fact]
    public async Task Ingest_SameTextTwice_IsNotDispatchedTwice()
    {
        AddChannel(All);
        await IngestAsync(DispatcherFixture.EmergencyText);

        var second = await IngestAsync(DispatcherFixture.EmergencyText);

        Assert.False(second.TryGetProperty("changed", out var changed) && changed.GetBoolean());
        Assert.Single(Sent(All));
    }

    // ---- narrowing ----

    [Fact]
    public async Task DtekNarrowsToDistricts_KeepsOutsideChatsThreadOpen_WhileTheRegionEmergencyIsOn()
    {
        AddChannel(All);
        AddChannel(Darnytskyi, ["darnytskyi"]);
        AddChannel(Holosiivskyi, ["holosiivskyi"]);
        SetGrid(GridStatuses.EmergencyOutages);
        await TickAsync();

        await IngestAsync(DispatcherFixture.EmergencyText, "darnytskyi");

        // Matched chats are edited; the chat outside the districts gets nothing and keeps its open thread,
        // so the region-wide alert is not sent to it a second time later.
        Assert.Single(Edits(All));
        Assert.Single(Edits(Darnytskyi));
        Assert.Empty(Edits(Holosiivskyi));
        Assert.Single(Sent(Holosiivskyi));
        Assert.False(Assert.Single(Deliveries(Holosiivskyi)).IsClosed);

        await IngestAsync("");

        Assert.Single(Sent(Holosiivskyi));
    }

    [Fact]
    public async Task DtekNarrowsToDistricts_WithoutUkrenergo_StillAnnouncesTheEndToOutsideChats()
    {
        AddChannel(Holosiivskyi, ["holosiivskyi"]);
        await IngestAsync(DispatcherFixture.EmergencyText);

        await IngestAsync(DispatcherFixture.EmergencyText + " Район.", "darnytskyi");

        var sent = Sent(Holosiivskyi);
        Assert.Equal(2, sent.Count);
        Assert.NotNull(sent[1].ReplyParameters);
    }

    // ---- ending ----

    [Fact]
    public async Task Ended_IsSentOnlyWhenBothSourcesAreClear()
    {
        AddChannel(All);
        SetGrid(GridStatuses.EmergencyOutages);
        await TickAsync();
        await IngestAsync(DispatcherFixture.EmergencyText);
        var messageId = Deliveries(All).Single().TelegramMessageId;

        // Ukrenergo clears while DTEK still shows the popup: the alert stays on.
        SetGrid(GridStatuses.Normal);
        await TickAsync();
        Assert.Single(Sent(All));
        Assert.False(Assert.Single(Deliveries(All)).IsClosed);

        // DTEK clears too: now it ends, as a reply to the alert.
        await IngestAsync(null);

        var sent = Sent(All);
        Assert.Equal(2, sent.Count);
        Assert.Equal(messageId, sent[1].ReplyParameters?.MessageId);
        Assert.True(Assert.Single(Deliveries(All)).IsClosed);
    }

    [Fact]
    public async Task DtekClears_WhileUkrenergoStillSaysEmergency_KeepsTheAlertWithUkrenergoWording()
    {
        AddChannel(All);
        SetGrid(GridStatuses.Normal);
        await TickAsync();
        await IngestAsync(DispatcherFixture.EmergencyText);
        SetGrid(GridStatuses.EmergencyOutages);
        await TickAsync();
        Assert.Empty(Edits(All));

        await IngestAsync(null);

        Assert.Single(Sent(All));
        var edit = Assert.Single(Edits(All));
        Assert.Contains(Tag, edit.Text);
        Assert.False(Assert.Single(Deliveries(All)).IsClosed);

        // ... and ends only when Ukrenergo clears as well.
        SetGrid(GridStatuses.Normal);
        await TickAsync();

        Assert.Equal(2, Sent(All).Count);
        Assert.True(Assert.Single(Deliveries(All)).IsClosed);
    }

    [Fact]
    public async Task UkrenergoClears_WhileDtekPopupIsOn_StaysOnDtek()
    {
        AddChannel(All);
        SetGrid(GridStatuses.EmergencyOutages);
        await TickAsync();
        await IngestAsync(DispatcherFixture.EmergencyText);
        var edits = Edits(All).Count;

        SetGrid(GridStatuses.Normal);
        await TickAsync();

        Assert.Equal(edits, Edits(All).Count);
        Assert.Single(Sent(All));
        Assert.False(Assert.Single(Deliveries(All)).IsClosed);
    }

    [Fact]
    public async Task UkrenergoAlone_EndsWhenItClears()
    {
        AddChannel(All);
        SetGrid(GridStatuses.EmergencyOutages);
        await TickAsync();

        SetGrid(GridStatuses.Normal);
        await TickAsync();

        var sent = Sent(All);
        Assert.Equal(2, sent.Count);
        Assert.NotNull(sent[1].ReplyParameters);
        Assert.True(Assert.Single(Deliveries(All)).IsClosed);
    }

    [Fact]
    public async Task StabilizationPopup_DoesNotCloseAUkrenergoAlert_AndIsNotAnAlertItself()
    {
        AddChannel(All);
        SetGrid(GridStatuses.EmergencyOutages);
        await TickAsync();

        await IngestAsync(DispatcherFixture.StabilizationText);

        Assert.Single(Sent(All));
        Assert.Empty(Edits(All));
        Assert.False(Assert.Single(Deliveries(All)).IsClosed);
    }

    // ---- endpoint ----

    [Theory]
    [InlineData(null)]
    [InlineData("wrong")]
    public async Task Tick_RejectsABadOrMissingSecret(string? secret)
    {
        SetGrid(GridStatuses.EmergencyOutages);
        using var client = _f.CreateClient();
        if (secret is not null)
        {
            client.DefaultRequestHeaders.Add("X-Ingest-Secret", secret);
        }

        var response = await client.PostAsync("/ingest/grid/tick", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0, _f.Grid.Calls);
    }

    [Fact]
    public async Task Ingest_KeepsItsContract()
    {
        using var anonymous = _f.CreateClient();
        var denied = await anonymous.PostAsJsonAsync("/ingest", new { regionId = "kyiv", popupText = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var unknown = await _client.PostAsJsonAsync("/ingest", new { regionId = "mars", popupText = "x" });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public async Task Tick_ReportsTheRegionsItRead()
    {
        SetGrid(GridStatuses.EmergencyOutages);

        var result = await TickAsync();

        var kyiv = result.GetProperty("regions").EnumerateArray().Single(r => r.GetProperty("region_id").GetString() == "kyiv");
        Assert.Equal("emergency_outages", kyiv.GetProperty("status").GetString());
        Assert.Equal("Emergency", kyiv.GetProperty("kind").GetString());
        Assert.True(kyiv.GetProperty("changed").GetBoolean());
        Assert.Equal(5, result.GetProperty("regions").GetArrayLength());
    }
}
