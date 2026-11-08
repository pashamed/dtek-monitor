using System.Net;
using System.Net.Http.Json;
using DtekBot.Api.Data;
using DtekBot.Api.MiniApp;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class StructuredTemplateTests
{
    private const string Fields = "{regionId}|{region}|{city}|{group}|{status}|{statusCode}";
    private static ChatAddress Address(string region, string? city, string group, string? parts = null) => new()
    {
        RegionId = region, Name = city ?? "Адреса", City = city, Street = "Тестова", House = "1",
        PartIds = parts, Groups = [new() { GroupId = group }],
    };

    [Theory]
    [InlineData("emergency", AlertKind.Emergency)]
    [InlineData("regional", AlertKind.Accident)]
    [InlineData("ended", AlertKind.Ended)]
    [InlineData("emergency", AlertKind.Unknown)]
    public async Task DefaultAlertPreview_UsesSharedConciseCopyWithoutFooter(string slot, AlertKind kind)
    {
        using var f = new DispatcherFixture();
        var service = new TemplatePreviewService(f.Options, new FakeScheduleProvider(), f.Clock);
        var rendered = await service.RenderAsync(slot, null, f.Kyiv, [], null, null,
            ScheduleImage.None, kind, default);
        var values = AlertText.Values(kind, f.Kyiv.Name, [], f.Kyiv.SiteUrl, f.Clock.GetUtcNow(), null);

        Assert.Equal(MessageTemplate.Render(null, values), rendered.Html);
        Assert.Single(rendered.Html.Split('\n'));
        Assert.DoesNotContain("(за даними ДТЕК)", rendered.Html);
        Assert.DoesNotContain("Деталі:", rendered.Html);
        if (kind == AlertKind.Ended)
            Assert.Contains("Скасування не підтверджене", rendered.Html);
    }

    [Theory]
    [InlineData("emergency")]
    [InlineData("regional")]
    [InlineData("local")]
    [InlineData("ended")]
    [InlineData("schedule-heading")]
    [InlineData("before-off")]
    public void StructuredFields_ValidateWithoutPopupOrPreparedMessage(string slot)
    {
        var localized = "{код_регіону}|{регіон}|{місто}|{група}|{статус}|{код_статусу}";
        Assert.Equal(Fields, PlaceholderNames.ToCanonical(localized));
        Assert.Empty(MessageTemplate.Validate(Fields, slot));
        Assert.Empty(MessageTemplate.Validate(PlaceholderNames.ToCanonical(localized), slot));
        Assert.NotEmpty(MessageTemplate.Validate(" \n", slot));
        Assert.NotEmpty(MessageTemplate.Validate("{unknown}", slot));
    }

    [Fact]
    public async Task AlertDelivery_UsesOnlyMatchingDistrictCitiesAndGroups()
    {
        using var f = new DispatcherFixture();
        f.AddChannel(93001, c =>
        {
            c.TemplateMap = new Dictionary<string, string> { [MessageSlots.Regional] = Fields };
            c.Addresses = [
                Address("kyiv", "Київ", "45.1", "holosiivskyi"),
                Address("kyiv", "Київ", "45.2", "solomianskyi"),
                Address("kyiv", "Інший", "46.1", "darnytskyi"),
                Address("kyiv-oblast", "Бровари", "45.1"),
            ];
        });
        await f.DispatchAsync(DispatcherFixture.AccidentRightBankText);
        var message = Assert.Single(f.Bot.SentTo(93001)).Text;
        Assert.Contains("kyiv|м. Київ|Київ|45.1, 45.2|Аварія в енергосистемі|Accident", message);
        Assert.DoesNotContain("Бровари", message);
        Assert.DoesNotContain("Інший", message);
        Assert.DoesNotContain("46.1", message);
        Assert.DoesNotContain("{", message);
    }

    [Fact]
    public async Task LocalAndClearanceDelivery_DoNotNeedPopupText()
    {
        using var f = new DispatcherFixture();
        f.AddChannel(93002, c =>
        {
            c.TemplateMap = new Dictionary<string, string> { [MessageSlots.Local] = Fields, [MessageSlots.Ended] = Fields };
            c.Addresses = [Address("kyiv", "Київ", "45.1"), Address("kyiv", "Не стосується", "45.2")];
        });
        await f.Dispatcher.DispatchGroupAsync(f.Kyiv, "45.1", true, default);
        Assert.Contains("kyiv|м. Київ|Київ|45.1|Екстрені відключення|Emergency", Assert.Single(f.Bot.SentTo(93002)).Text);
        await f.Dispatcher.DispatchGroupAsync(f.Kyiv, "45.1", false, default);
        Assert.Contains(f.Bot.SentTo(93002), m => m.Text.Contains("|Запис про відключення знято|Ended"));
        Assert.DoesNotContain(f.Bot.SentTo(93002), m => m.Text.Contains("Не стосується"));
    }

    [Fact]
    public async Task PreviewAndSave_ExposeScopedFieldsWithoutSending()
    {
        using var api = new ApiFactory();
        using var client = api.ClientFor(93003);
        api.WithDb(db => db.Channels.Add(new()
        {
            ChatId = 93003, RegionId = "kyiv",
            Addresses = [Address("kyiv", "Київ", "45.1"), Address("kyiv", "КИЇВ", "45.2"), Address("kyiv-oblast", "Бровари", "45.1")],
        }));
        var chats = await client.GetFromJsonAsync<List<ChatSummaryDto>>("/api/chats", ApiJson.Options);
        var saved = Assert.Single(chats!).Settings;
        var template = "{регіон} · {місто} · {група}\n{статус} ({код_статусу})";
        var put = await client.PutAsJsonAsync("/api/chats/93003", saved with
        {
            Templates = new Dictionary<string, string> { [MessageSlots.Emergency] = template },
        }, ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var preview = await client.PostAsJsonAsync("/api/preview", new PreviewRequest(
            Slot: MessageSlots.Emergency, Template: template, Kind: AlertKind.Emergency, RegionId: "kyiv", PartId: null, Addresses: saved.Addresses), ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var result = await preview.Content.ReadFromJsonAsync<PreviewResponse>(ApiJson.Options);
        Assert.Empty(result!.Errors);
        Assert.Equal("м. Київ · Київ · 45.1, 45.2\nЕкстрені відключення (Emergency)", result.Html);
        Assert.Empty(api.Bot.Sent);
    }

    [Fact]
    public async Task Preview_CityIsEmptyWhenUnknown_NotAnInventedLocation()
    {
        using var f = new DispatcherFixture();
        var service = new TemplatePreviewService(f.Options, new FakeScheduleProvider(), f.Clock);
        var rendered = await service.RenderAsync(MessageSlots.Emergency, Fields, f.Kyiv, [], null, null,
            ScheduleImage.None, null, default);
        Assert.Equal("kyiv|м. Київ|||Екстрені відключення|Emergency", rendered.Html);
    }

    [Fact]
    public void ScheduleHeading_StatusDistinguishesAvailableAndUnavailableDays()
    {
        var channel = new Channel
        {
            RegionId = "kyiv", Addresses = [Address("kyiv", "Київ", "45.1")],
            TemplateMap = new Dictionary<string, string> { [MessageSlots.ScheduleHeading] = Fields },
        };
        var available = CombinedSchedule.Render(channel, RealSnapshot(), "м. Київ", Kyiv(15, 12), false);
        Assert.Contains("kyiv|м. Київ|Київ|45.1|Опублікований графік|ScheduleAvailable", available);
        var missing = CombinedSchedule.Render(channel, RealSnapshot(), "м. Київ", Kyiv(17, 12), false);
        Assert.Contains("kyiv|м. Київ|Київ|45.1|Графік недоступний|ScheduleUnavailable", missing);
        Assert.DoesNotContain("ScheduleAvailable", missing);
        channel.Addresses.Clear();
        var noGroups = CombinedSchedule.Render(channel, RealSnapshot(), "м. Київ", Kyiv(15, 12), false);
        Assert.DoesNotContain("ScheduleAvailable", noGroups);
        Assert.Contains("ScheduleUnavailable", noGroups);
    }

    [Fact]
    public async Task ReminderPreview_UsesPlannedStatusAndActualRegionContext()
    {
        using var f = new DispatcherFixture();
        var channel = new Channel { RegionId = "kyiv", Addresses = [Address("kyiv", "Київ", "45.1")] };
        var service = new TemplatePreviewService(f.Options, new FakeScheduleProvider(), f.Clock);
        var rendered = await service.RenderAsync(MessageSlots.BeforeOff, Fields, f.Kyiv, [], "45.1", null,
            ScheduleImage.None, null, default, channel: channel);
        Assert.Equal("kyiv|м. Київ|Київ|45.1|Планове відключення|PlannedOutage", rendered.Html);
    }

    [Theory]
    [InlineData("regional", "Аварія в енергосистемі", "Accident")]
    [InlineData("local", "Екстрені відключення", "Emergency")]
    [InlineData("before-off", "Планове відключення", "PlannedOutage")]
    public async Task Preview_ExcludesUnaffectedAddresses(string slot, string status, string code)
    {
        using var f = new DispatcherFixture();
        var channel = new Channel
        {
            RegionId = "kyiv",
            Addresses = [
                Address("kyiv", "Київ", "45.1", "holosiivskyi"),
                Address("kyiv", "Інший", "45.2", "darnytskyi"),
                Address("kyiv-oblast", "Бровари", "45.1"),
            ],
        };
        var service = new TemplatePreviewService(f.Options, new FakeScheduleProvider(), f.Clock);
        var rendered = await service.RenderAsync(slot, Fields, f.Kyiv, ["holosiivskyi"], "45.1", null,
            ScheduleImage.None, null, default, channel: channel);
        var attribution = slot == MessageSlots.Local ? "\n\n(за даними Yasno)" : "";
        Assert.Equal($"kyiv|м. Київ|Київ|45.1|{status}|{code}{attribution}", rendered.Html);
    }
}
