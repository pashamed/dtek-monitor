using System.Net;
using System.Text.Json;
using DtekBot.Api.Data;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class PlaceholderNamesTests
{
    [Fact]
    public void EveryPlaceholderOfEverySlot_HasAUkrainianName()
    {
        var keys = MessageSlots.All.SelectMany(s => s.Placeholders).Select(p => p.Name)
            .Concat(MessageTemplate.Placeholders.Select(p => p.Name))
            .Distinct()
            .ToList();

        Assert.NotEmpty(keys);
        Assert.All(keys, key => Assert.True(PlaceholderNames.Table.ContainsKey(key), $"No Ukrainian name for '{key}'"));
        Assert.Equal(PlaceholderNames.Table.Count, PlaceholderNames.Table.Values.Distinct().Count());
    }

    [Fact]
    public void EveryValueKeyTheBotRenders_HasAUkrainianName()
    {
        var alert = AlertText.Values(AlertKind.Emergency, "r", ["p"], "s", DateTimeOffset.UtcNow, "x").Keys;
        Assert.All(alert, key => Assert.True(PlaceholderNames.Table.ContainsKey(key), $"No Ukrainian name for '{key}'"));
    }

    [Fact]
    public void UkrainianNames_AreLowercaseCyrillicAndUnderscoreOnly()
    {
        Assert.All(PlaceholderNames.Table.Values, name => Assert.Matches("^[а-щьюяєіїґ_]+$", name));
    }

    [Theory]
    [InlineData("{time} {region}", "{час} {регіон}")]
    [InlineData("{час} {region}", "{час} {регіон}")]
    [InlineData("{#onDuration}було {onDuration}{/onDuration}", "{#світло_було}було {світло_було}{/світло_було}")]
    [InlineData("<a href=\"{site}\">час</a>", "<a href=\"{сайт}\">час</a>")]
    [InlineData("<a title=\"час time\">{time}</a>", "<a title=\"час time\">{час}</a>")]
    [InlineData("{{time}} {time}", "{{time}} {час}")]
    [InlineData("{{{time}}}", "{{{час}}}")]
    [InlineData("{unknown} {#nope}x{/nope}", "{unknown} {#nope}x{/nope}")]
    [InlineData("a { b", "a { b")]
    [InlineData("", "")]
    public void ToDisplay_TranslatesOnlyPlaceholderTokens(string input, string expected)
    {
        Assert.Equal(expected, PlaceholderNames.ToDisplay(input));
    }

    [Theory]
    [InlineData("{час} {регіон}", "{time} {region}")]
    [InlineData("{time} {регіон}", "{time} {region}")]
    [InlineData("{#світло_було}x{/світло_було}", "{#onDuration}x{/onDuration}")]
    [InlineData("<a href=\"{сайт}\">час</a>", "<a href=\"{site}\">час</a>")]
    [InlineData("час {{час}} {час}", "час {{час}} {time}")]
    [InlineData("{невідомо} {#щось}", "{невідомо} {#щось}")]
    public void ToCanonical_AcceptsUkrainianAndLatin(string input, string expected)
    {
        Assert.Equal(expected, PlaceholderNames.ToCanonical(input));
    }

    [Fact]
    public void Translation_IsReversibleAndIdempotent_ForEveryDefaultTemplate()
    {
        foreach (var slot in MessageSlots.All)
        {
            var display = PlaceholderNames.ToDisplay(slot.DefaultTemplate);
            Assert.Equal(slot.DefaultTemplate, PlaceholderNames.ToCanonical(display));
            Assert.Equal(display, PlaceholderNames.ToDisplay(display));
            Assert.Equal(slot.DefaultTemplate, PlaceholderNames.ToCanonical(slot.DefaultTemplate));
            Assert.Empty(MessageTemplate.Validate(PlaceholderNames.ToCanonical(display), slot.Id));
        }
    }

    [Fact]
    public void Translation_IsReversibleForEveryNameInEveryPosition()
    {
        var latin = string.Join(" ", PlaceholderNames.Table.Keys.Select(k => $"{{{k}}}{{#{k}}}x{{/{k}}}"));

        var display = PlaceholderNames.ToDisplay(latin);

        Assert.DoesNotContain(PlaceholderNames.Table.Keys, k => display.Contains("{" + k + "}"));
        Assert.Equal(latin, PlaceholderNames.ToCanonical(display));
    }

    [Fact]
    public void RenderingAMixedTemplate_GivesTheSameResultAsTheLatinOne()
    {
        var values = new Dictionary<string, string> { ["time"] = "14:24", ["region"] = "Київ", ["onDuration"] = "2год", ["until"] = "" };
        var mixed = "{час} {region}{#світло_було} було {onDuration}{/світло_було}{#очікуємо_до} до {until}{/очікуємо_до}";

        Assert.Equal("14:24 Київ було 2год", MessageTemplate.Render(PlaceholderNames.ToCanonical(mixed), values));
    }

    [Fact]
    public void ToDisplayList_KeepsTheLatinKey()
    {
        var shown = PlaceholderNames.ToDisplayList(MessageTemplate.Placeholders);

        Assert.Equal(MessageTemplate.Placeholders.Select(p => p.Name), shown.Select(p => p.Key));
        Assert.Equal(PlaceholderNames.ToDisplayList(shown).Select(p => p.Name), shown.Select(p => p.Name));
        Assert.Equal(PlaceholderNames.ToDisplayList(shown).Select(p => p.Key), shown.Select(p => p.Key));
    }
}

public sealed class PlaceholderApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PlaceholderApiTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Bot.Requests.Clear();
        _factory.Bot.Failure = null;
        _factory.Clock.Now = Kyiv(15, 12);
        _factory.Schedules.Snapshots.Clear();
        _factory.Schedules.Snapshots["kyiv"] = RealSnapshot();
        _factory.Schedules.Calls.Clear();
    }

    private static ChatSettingsDto Settings(IReadOnlyDictionary<string, string>? templates, string? template = null) =>
        new("kyiv", null, true, false, AlertFilter.All, true, true, template, Templates: templates);

    private static Dictionary<string, string> Map(params (string Slot, string Text)[] items) =>
        items.ToDictionary(i => i.Slot, i => i.Text);

    private (long UserId, HttpClient Client) NewUser()
    {
        var userId = _factory.NextId();
        return (userId, _factory.ClientFor(userId));
    }

    private Channel? Load(long chatId) =>
        _factory.WithDb(db => db.Channels.AsNoTracking().SingleOrDefault(c => c.ChatId == chatId));

    [Fact]
    public async Task Put_UkrainianMixedAndConditionalTemplates_AreStoredInLatin_AndReturnedInUkrainian()
    {
        var (userId, client) = NewUser();
        var templates = Map(
            ("emergency", "{значок} {message}"),
            ("off", "{час} {#світло_було}було {світло_було}{/світло_було}"),
            ("on", "{time} {регіон}"));

        var response = await client.PutJsonAsync($"/api/chats/{userId}", Settings(templates));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = (await response.ReadAsync<ChatSummaryDto>())!;
        Assert.Equal("{значок} {текст}", summary.Settings.Templates!["emergency"]);
        Assert.Equal("{час} {#світло_було}було {світло_було}{/світло_було}", summary.Settings.Templates["off"]);
        Assert.Equal("{час} {регіон}", summary.Settings.Templates["on"]);

        var channel = Load(userId)!;
        Assert.Equal("{emoji} {message}", channel.TemplateFor("emergency"));
        Assert.Equal("{time} {#onDuration}було {onDuration}{/onDuration}", channel.TemplateFor("off"));
        Assert.Equal("{time} {region}", channel.TemplateFor("on"));
        Assert.DoesNotContain(JsonDocument.Parse(channel.Templates!).RootElement.EnumerateObject().Select(p => p.Value.GetString()!), t => t.Contains("час") || t.Contains("значок"));
    }

    [Fact]
    public async Task Put_SavingTheReturnedUkrainianTemplatesAgain_ChangesNothing()
    {
        var (userId, client) = NewUser();
        var first = (await (await client.PutJsonAsync($"/api/chats/{userId}", Settings(Map(("off", "{час}|{регіон}"))))).ReadAsync<ChatSummaryDto>())!;
        var storedFirst = Load(userId)!.Templates;

        var second = (await (await client.PutJsonAsync($"/api/chats/{userId}", Settings(first.Settings.Templates))).ReadAsync<ChatSummaryDto>())!;

        Assert.Equal(first.Settings.Templates, second.Settings.Templates);
        Assert.Equal(storedFirst, Load(userId)!.Templates);
    }

    [Fact]
    public async Task Put_TheUkrainianDefault_IsNotStored()
    {
        var (userId, client) = NewUser();
        var slot = MessageSlots.Find("local")!;

        var response = await client.PutJsonAsync(
            $"/api/chats/{userId}",
            Settings(Map(("local", PlaceholderNames.ToDisplay(slot.DefaultTemplate)), ("off", "{час}"))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["off"], Load(userId)!.TemplateMap.Keys);
    }

    [Fact]
    public async Task Put_LegacySingleTemplate_InUkrainian_IsStoredInLatin()
    {
        var (userId, client) = NewUser();

        var response = await client.PutJsonAsync($"/api/chats/{userId}", Settings(null, "{значок} {текст}!"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{emoji} {message}!", Load(userId)!.Template);
    }

    [Fact]
    public async Task Put_HtmlLinkWithAUkrainianPlaceholder_IsAllowed_AndAttributeWordsStayUntouched()
    {
        var (userId, client) = NewUser();

        var response = await client.PutJsonAsync(
            $"/api/chats/{userId}",
            Settings(Map(("emergency", "<a href=\"{сайт}\">час</a> {текст}"))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("<a href=\"{site}\">час</a> {message}", Load(userId)!.TemplateFor("emergency"));
    }

    [Fact]
    public async Task Put_Errors_NameUkrainianPlaceholders()
    {
        var (userId, client) = NewUser();

        var response = await client.PutJsonAsync(
            $"/api/chats/{userId}",
            Settings(Map(("off", "{група} {#світло_було}x"), ("schedule", "{time}"), ("local", "{outages}{message}"))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var details = (await response.ReadAsync<ApiError>())!.Details!;
        Assert.Contains("Світло вимкнено за графіком: Невідомий параметр {група}.", details);
        Assert.Contains("Світло вимкнено за графіком: Блок {#світло_було} не закрито: додайте {/світло_було}.", details);
        Assert.Contains(details, d => d.StartsWith("Графік відключень: Додайте {відключення} або {ранок}"));
        Assert.Contains("Локальне відключення: Невідомий параметр {відключення}.", details);
        Assert.DoesNotContain(details, d => d.Contains("{outages}") || d.Contains("{time}") || d.Contains("{onDuration}"));
        Assert.Null(Load(userId));
    }

    [Fact]
    public async Task Put_LatinNamesThatAreNotInTheSlot_AreReportedInUkrainian()
    {
        var (userId, client) = NewUser();

        var response = await client.PutJsonAsync($"/api/chats/{userId}", Settings(Map(("emergency", "{group} {message}"))));

        var details = (await response.ReadAsync<ApiError>())!.Details!;
        Assert.Contains("Екстрені відключення: Невідомий параметр {черга}.", details);
    }

    [Fact]
    public async Task Preview_AcceptsUkrainianLatinAndMixedTemplates()
    {
        var (_, client) = NewUser();

        foreach (var template in new[] { "{регіон}|{черга}|{час}", "{region}|{group}|{time}", "{регіон}|{group}|{час}" })
        {
            var response = await client.PostJsonAsync("/api/preview", new PreviewRequest(template, AlertKind.Emergency, "kyiv", null, null, "off", "1.1"));
            var result = (await response.ReadAsync<PreviewResponse>())!;
            Assert.Equal("м. Київ|1.1|14:24", result.Html);
        }
    }

    [Fact]
    public async Task Preview_ConditionalBlockInUkrainian_Works()
    {
        var (_, client) = NewUser();

        var response = await client.PostJsonAsync(
            "/api/preview",
            new PreviewRequest("{час}{#світло_було} було {світло_було}{/світло_було}", AlertKind.Emergency, "kyiv", null, null, "off", "1.1"));

        var result = (await response.ReadAsync<PreviewResponse>())!;
        Assert.Empty(result.Errors);
        Assert.Equal("14:24 було 17год 23хв", result.Html);
    }

    [Fact]
    public async Task Preview_Errors_UseUkrainianNames()
    {
        var (_, client) = NewUser();

        var response = await client.PostJsonAsync("/api/preview", new PreviewRequest("{emoji} {time}", AlertKind.Emergency, "kyiv", null, null, "off"));

        var result = (await response.ReadAsync<PreviewResponse>())!;
        Assert.Equal(["Невідомий параметр {значок}."], result.Errors);
    }

    [Fact]
    public async Task Send_AcceptsAUkrainianTemplate()
    {
        var (userId, client) = NewUser();
        await client.PutJsonAsync($"/api/chats/{userId}", Settings(null));

        var response = await client.PostJsonAsync($"/api/chats/{userId}/templates/off/send", new SendTestRequest("{регіон}|{час}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = Assert.Single(_factory.Bot.SentTo(userId));
        Assert.EndsWith("м. Київ|14:24", sent.Text);
    }

    [Fact]
    public async Task Send_StoredUkrainianTemplateIsRenderedFromItsLatinForm()
    {
        var (userId, client) = NewUser();
        await client.PutJsonAsync($"/api/chats/{userId}", Settings(Map(("off", "{регіон}!{час}"))));

        var response = await client.PostJsonAsync($"/api/chats/{userId}/templates/off/send", new SendTestRequest(null));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.EndsWith("м. Київ!14:24", Assert.Single(_factory.Bot.SentTo(userId)).Text);
    }

    [Fact]
    public async Task Send_ErrorsUseUkrainianNames()
    {
        var (userId, client) = NewUser();
        await client.PutJsonAsync($"/api/chats/{userId}", Settings(null));

        var response = await client.PostJsonAsync($"/api/chats/{userId}/templates/off/send", new SendTestRequest("{emoji}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Невідомий параметр {значок}.", (await response.ReadAsync<ApiError>())!.Details!);
    }
}
