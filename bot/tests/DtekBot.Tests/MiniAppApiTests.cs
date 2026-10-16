using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DtekBot.Api.Data;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types.Enums;
using static DtekBot.Tests.ScheduleFixtures;
using ChatFullInfo = Telegram.Bot.Types.ChatFullInfo;

namespace DtekBot.Tests;

public sealed class MiniAppApiTests : IClassFixture<ApiFactory>
{
    private const string GridBase = "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/images/kyiv/";

    private readonly ApiFactory _factory;

    public MiniAppApiTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Bot.Requests.Clear();
        _factory.Bot.Failure = null;
        _factory.Clock.Now = Kyiv(15, 12);
        _factory.Schedules.Snapshots.Clear();
        _factory.Schedules.Snapshots["kyiv"] = RealSnapshot();
        _factory.Schedules.Calls.Clear();
    }

    // ---- helpers ----

    private static ChatSettingsDto Settings(
        string? regionId = "kyiv",
        bool isActive = true,
        string? template = null,
        IReadOnlyDictionary<string, string>? templates = null,
        bool scheduleNotify = false,
        bool schedulePost = false,
        PostMode eventMode = PostMode.Edit,
        PostMode postMode = PostMode.New,
        ScheduleImage image = ScheduleImage.None,
        ScheduleImage postImage = ScheduleImage.Grid,
        bool outageTrigger = false,
        IReadOnlyList<string>? partIds = null,
        string? partId = null,
        IReadOnlyDictionary<string, SlotSettingDto>? slotSettings = null) =>
        new(
            regionId,
            partId,
            isActive,
            false,
            AlertFilter.All,
            true,
            true,
            template,
            null,
            null,
            outageTrigger,
            scheduleNotify,
            image,
            partIds,
            schedulePost,
            postMode,
            postImage,
            eventMode,
            templates,
            null,
            slotSettings);

    private static Dictionary<string, string> Map(params (string Slot, string Text)[] items) =>
        items.ToDictionary(i => i.Slot, i => i.Text);

    private (long UserId, HttpClient Client) NewUser()
    {
        var userId = _factory.NextId();
        return (userId, _factory.ClientFor(userId));
    }

    private Channel? LoadChannel(long chatId) =>
        _factory.WithDb(db => db.Channels.AsNoTracking().SingleOrDefault(c => c.ChatId == chatId));

    private void SeedChannel(long chatId, Action<Channel>? configure = null)
    {
        _factory.WithDb(db =>
        {
            var channel = new Channel { ChatId = chatId, RegionId = "kyiv", IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            configure?.Invoke(channel);
            db.Channels.Add(channel);
        });
    }

    // ================= authorization =================

    [Fact]
    public async Task WithoutAuthorization_Is401()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/bootstrap");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Відкрийте застосунок із Telegram.", (await response.ReadAsync<ApiError>())!.Error);
    }

    [Fact]
    public async Task WithInitDataSignedByAnotherToken_Is401()
    {
        using var client = _factory.ClientFor(1, token: "654321:OTHER");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/bootstrap")).StatusCode);
    }

    [Fact]
    public async Task WithExpiredInitData_Is401()
    {
        using var client = _factory.ClientFor(1, authDate: _factory.Clock.Now.AddHours(-25));

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/chats")).StatusCode);
    }

    [Fact]
    public async Task WithValidInitData_IsAllowed()
    {
        var (_, client) = NewUser();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/bootstrap")).StatusCode);
    }

    // ================= bootstrap =================

    [Fact]
    public async Task Bootstrap_ListsAllTemplateSlots_WithTheirDefaultsAndPlaceholders()
    {
        var (userId, client) = NewUser();

        var bootstrap = (await (await client.GetAsync("/api/bootstrap")).ReadAsync<BootstrapDto>())!;

        Assert.Equal(userId, bootstrap.UserId);
        Assert.Equal(MessageTemplate.MaxLength, bootstrap.MaxTemplateLength);
        Assert.Equal(
            ["emergency", "ended", "regional", "local", "schedule", "off", "on"],
            bootstrap.TemplateSlots!.Select(s => s.Id));
        Assert.Equal(MessageSlots.All.Count, bootstrap.TemplateSlots!.Count);
        foreach (var (expected, actual) in MessageSlots.All.Zip(bootstrap.TemplateSlots!))
        {
            Assert.Equal(PlaceholderNames.ToDisplay(expected.DefaultTemplate), actual.DefaultTemplate);
            Assert.Equal(expected.Group, actual.Group);
            Assert.Equal(expected.Title, actual.Title);
            Assert.Equal(expected.Placeholders.Select(p => PlaceholderNames.Table[p.Name]), actual.Placeholders.Select(p => p.Name));
            Assert.Equal(expected.Placeholders.Select(p => p.Name), actual.Placeholders.Select(p => p.Key));
            Assert.Equal(expected.Placeholders.Select(p => p.Description), actual.Placeholders.Select(p => p.Description));
        }

        Assert.Equal(PlaceholderNames.ToDisplay(MessageTemplate.Default), bootstrap.DefaultTemplate);
        Assert.Equal(MessageTemplate.Placeholders.Select(p => PlaceholderNames.Table[p.Name]), bootstrap.Placeholders.Select(p => p.Name));
        Assert.Equal(MessageTemplate.Placeholders.Select(p => p.Name), bootstrap.Placeholders.Select(p => p.Key));
    }

    [Fact]
    public async Task Bootstrap_ListsTheRegions_WithDistrictsAndCapabilities()
    {
        var (_, client) = NewUser();

        var bootstrap = (await (await client.GetAsync("/api/bootstrap")).ReadAsync<BootstrapDto>())!;

        var kyiv = bootstrap.Regions.Single(r => r.Id == "kyiv");
        Assert.True(kyiv.HasGroups);
        Assert.True(kyiv.HasSchedules);
        Assert.Equal(10, kyiv.Parts.Count);
        Assert.DoesNotContain(kyiv.Parts, p => p.Id is "right-bank" or "left-bank");
        var donetsk = bootstrap.Regions.Single(r => r.Id == "donetsk");
        Assert.False(donetsk.HasGroups);
        Assert.False(donetsk.HasSchedules);
    }

    // ================= GET /api/chats =================

    [Fact]
    public async Task Chats_ForANewUser_HasOnlyThePrivateChatPlaceholder()
    {
        var (userId, client) = NewUser();

        var chats = (await (await client.GetAsync("/api/chats")).ReadAsync<List<ChatSummaryDto>>())!;

        var chat = Assert.Single(chats);
        Assert.Equal(userId, chat.ChatId);
        Assert.True(chat.IsPrivate);
        Assert.Equal("Цей чат із ботом", chat.Title);
        Assert.False(chat.Settings.IsActive);
        Assert.Null(chat.Settings.RegionId);
    }

    [Fact]
    public async Task Chats_MergesTheLegacyTemplateIntoTheAlertSlots_AndReturnsNoLegacyTemplate()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, c =>
        {
            c.Template = "LEG {message}";
            c.TemplateMap = Map(("regional", "own {message}"), ("off", "OFF {time}"), ("bogus", "x"));
        });

        var chat = Assert.Single((await (await client.GetAsync("/api/chats")).ReadAsync<List<ChatSummaryDto>>())!);

        Assert.Null(chat.Settings.Template);
        var templates = chat.Settings.Templates!;
        Assert.Equal("LEG {текст}", templates["emergency"]);
        Assert.Equal("LEG {текст}", templates["ended"]);
        Assert.Equal("LEG {текст}", templates["local"]);
        Assert.Equal("own {текст}", templates["regional"]);
        Assert.Equal("OFF {час}", templates["off"]);
        Assert.False(templates.ContainsKey("schedule"));
        Assert.False(templates.ContainsKey("on"));
        Assert.False(templates.ContainsKey("bogus"));
    }

    [Fact]
    public async Task Chats_WithoutAnyTemplates_HasAnEmptyTemplateMap()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId);

        var chat = Assert.Single((await (await client.GetAsync("/api/chats")).ReadAsync<List<ChatSummaryDto>>())!);

        Assert.Empty(chat.Settings.Templates!);
    }

    [Fact]
    public async Task Chats_ReturnsTheNewSettingsFields()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, c =>
        {
            c.GroupId = "45.1";
            c.ScheduleNotify = true;
            c.SchedulePost = true;
            c.ScheduleEventMode = PostMode.ReplaceDelete;
            c.SchedulePostMode = PostMode.Edit;
            c.SchedulePostImage = ScheduleImage.Card;
            c.ScheduleImage = ScheduleImage.Grid;
            c.OutageTrigger = true;
            c.PartIdList = ["obolonskyi", "darnytskyi"];
        });

        var settings = Assert.Single((await (await client.GetAsync("/api/chats")).ReadAsync<List<ChatSummaryDto>>())!).Settings;

        Assert.Equal("45.1", settings.GroupId);
        Assert.True(settings.ScheduleNotify);
        Assert.True(settings.SchedulePost);
        Assert.Equal(PostMode.ReplaceDelete, settings.ScheduleEventMode);
        Assert.Equal(PostMode.Edit, settings.SchedulePostMode);
        Assert.Equal(ScheduleImage.Card, settings.SchedulePostImage);
        Assert.Equal(ScheduleImage.Grid, settings.ScheduleImage);
        Assert.True(settings.OutageTrigger);
        Assert.Equal(["obolonskyi", "darnytskyi"], settings.PartIds);
    }

    [Fact]
    public async Task Chats_ReplacesRetiredKyivBankIds_WithTheirDistricts_AndSavesThem()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, c =>
        {
            c.PartIds = "left-bank,Right-Bank,pecherskyi";
        });

        var settings = Assert.Single((await (await client.GetAsync("/api/chats")).ReadAsync<List<ChatSummaryDto>>())!).Settings;

        string[] expected =
        [
            "darnytskyi", "desnianskyi", "dniprovskyi",
            "holosiivskyi", "obolonskyi", "pecherskyi", "podilskyi", "sviatoshynskyi", "solomianskyi", "shevchenkivskyi",
        ];
        Assert.Equal(expected, settings.PartIds);
        Assert.Equal(expected, LoadChannel(userId)!.PartIdList);
    }

    // ================= PUT /api/chats/{id}: per-alert settings =================

    [Fact]
    public async Task Chats_WithoutSlotSettings_ReturnsTheLegacyDefaultsForAllFourAlertSlots()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, c => { c.NotifyOnEnd = false; c.EditInPlace = false; });

        var map = Assert.Single((await (await client.GetAsync("/api/chats")).ReadAsync<List<ChatSummaryDto>>())!).Settings.SlotSettings!;

        Assert.Equal(["emergency", "ended", "local", "regional"], map.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new SlotSettingDto(true, false), map["emergency"]);
        Assert.Equal(new SlotSettingDto(true, false), map["local"]);
        Assert.Equal(new SlotSettingDto(false, false), map["ended"]);
    }

    [Fact]
    public async Task Put_SlotSettings_RoundTrip_AndMirrorIntoLegacyColumns()
    {
        var (userId, client) = NewUser();
        var slots = new Dictionary<string, SlotSettingDto>
        {
            ["emergency"] = new(true, false),
            ["regional"] = new(false, true),
            ["local"] = new(true, true),
            ["ended"] = new(false, false),
        };

        var response = await client.PutJsonAsync($"/api/chats/{userId}", Settings(slotSettings: slots));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var returned = (await response.ReadAsync<ChatSummaryDto>())!.Settings.SlotSettings!;
        Assert.Equal(slots, returned.ToDictionary(p => p.Key, p => p.Value));
        var channel = LoadChannel(userId)!;
        Assert.False(channel.NotifyOnEnd);
        Assert.False(channel.EditInPlace);
        Assert.False(channel.SlotEnabled("regional"));
        var fetched = Assert.Single((await (await client.GetAsync("/api/chats")).ReadAsync<List<ChatSummaryDto>>())!).Settings.SlotSettings!;
        Assert.Equal(slots, fetched.ToDictionary(p => p.Key, p => p.Value));
    }

    [Fact]
    public async Task Put_SlotSettings_WithANonAlertSlot_IsRejected()
    {
        var (userId, client) = NewUser();

        var response = await client.PutJsonAsync(
            $"/api/chats/{userId}",
            Settings(slotSettings: new Dictionary<string, SlotSettingDto> { ["schedule"] = new(true, true) }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(LoadChannel(userId));
    }

    // ================= PUT /api/chats/{id}: templates =================

    [Fact]
    public async Task Put_ValidTemplates_ArePersisted_PerSlot()
    {
        var (userId, client) = NewUser();
        var templates = Map(("emergency", "{emoji} X {message}"), ("off", "OFF {time}"), ("schedule", "{header}\n{outages}"));

        var response = await client.PutJsonAsync($"/api/chats/{userId}", Settings(templates: templates));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = (await response.ReadAsync<ChatSummaryDto>())!;
        Assert.Equal(templates.ToDictionary(p => p.Key, p => PlaceholderNames.ToDisplay(p.Value)), summary.Settings.Templates!.ToDictionary(p => p.Key, p => p.Value));
        var channel = LoadChannel(userId)!;
        Assert.Equal(templates, channel.TemplateMap.ToDictionary(p => p.Key, p => p.Value));
        Assert.Null(channel.Template);
        Assert.Null(summary.Settings.Template);
    }

    [Fact]
    public async Task Put_SendingTemplates_ClearsTheLegacyTemplate()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, c => c.Template = "LEG {message}");

        var response = await client.PutJsonAsync($"/api/chats/{userId}", Settings(templates: Map(("regional", "R {message}"))));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var channel = LoadChannel(userId)!;
        Assert.Null(channel.Template);
        Assert.Equal(["regional"], channel.TemplateMap.Keys);
    }

    [Fact]
    public async Task Put_EmptyTemplateMap_ClearsEverything()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, c => { c.Template = "LEG {message}"; c.TemplateMap = Map(("off", "OFF {time}")); });

        await client.PutJsonAsync($"/api/chats/{userId}", Settings(templates: new Dictionary<string, string>()));

        var channel = LoadChannel(userId)!;
        Assert.Null(channel.Template);
        Assert.Null(channel.Templates);
        Assert.Empty(channel.TemplateMap);
    }

    [Fact]
    public async Task Put_DefaultAndBlankTemplates_AreDropped()
    {
        var (userId, client) = NewUser();
        var emergencyDefault = MessageSlots.Find("emergency")!.DefaultTemplate;
        var offDefault = MessageSlots.Find("off")!.DefaultTemplate;
        var templates = Map(("emergency", emergencyDefault), ("off", offDefault), ("regional", ""), ("on", "   "), ("local", "L {message}"));

        var response = await client.PutJsonAsync($"/api/chats/{userId}", Settings(templates: templates));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["local"], LoadChannel(userId)!.TemplateMap.Keys);
    }

    [Fact]
    public async Task Put_InvalidTemplate_Is400_NamingTheSlot_AndSavesNothing()
    {
        var (userId, client) = NewUser();

        var response = await client.PutJsonAsync($"/api/chats/{userId}", Settings(templates: Map(("off", "{emoji} x"))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.ReadAsync<ApiError>())!;
        Assert.Equal("Шаблон містить помилки.", error.Error);
        Assert.Contains("Світло вимкнено за графіком: Невідомий параметр {значок}.", error.Details!);
        Assert.Null(LoadChannel(userId));
    }

    [Fact]
    public async Task Put_InvalidTemplatesInSeveralSlots_ListsEveryError()
    {
        var (userId, client) = NewUser();

        var response = await client.PutJsonAsync(
            $"/api/chats/{userId}",
            Settings(templates: Map(("emergency", "no content here"), ("schedule", "just text"), ("on", "<script>x</script>"))));

        var error = (await response.ReadAsync<ApiError>())!;
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error.Details!, d => d.StartsWith("Екстрені відключення: Додайте {текст} або {текст_сайту}"));
        Assert.Contains(error.Details!, d => d.StartsWith("Графік відключень: Додайте {відключення}"));
        Assert.Contains(error.Details!, d => d.StartsWith("Світло з'явилося за графіком: Тег <script>"));
    }

    [Fact]
    public async Task Put_SlotSpecificPlaceholders_AreChecked_PerSlot()
    {
        var (userId, client) = NewUser();

        var wrongSlot = await client.PutJsonAsync($"/api/chats/{userId}", Settings(templates: Map(("emergency", "{message} {group}"))));
        var rightSlot = await client.PutJsonAsync($"/api/chats/{userId}", Settings(templates: Map(("local", "{message} {group} {#address}{address}{/address}"))));

        Assert.Equal(HttpStatusCode.BadRequest, wrongSlot.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rightSlot.StatusCode);
    }

    [Fact]
    public async Task Put_UnknownSlot_IsRejected()
    {
        var (userId, client) = NewUser();

        var response = await client.PutJsonAsync($"/api/chats/{userId}", Settings(templates: Map(("bogus", "{message}"))));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Невідомий тип повідомлення «bogus».", (await response.ReadAsync<ApiError>())!.Details!);
        Assert.Null(LoadChannel(userId));
    }

    [Fact]
    public async Task Put_LegacySingleTemplate_IsValidatedAsTheEmergencySlot()
    {
        var (userId, client) = NewUser();

        var bad = await client.PutJsonAsync($"/api/chats/{userId}", Settings(template: "{nope} {message}"));
        var good = await client.PutJsonAsync($"/api/chats/{userId}", Settings(template: "<b>{message}</b>"));

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Contains("Екстрені відключення: Невідомий параметр {nope}.", (await bad.ReadAsync<ApiError>())!.Details!);
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        Assert.Equal("<b>{message}</b>", LoadChannel(userId)!.Template);
    }

    [Fact]
    public async Task Put_LegacyDefaultTemplate_IsStoredAsNothing()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, c => c.Template = "old {message}");

        await client.PutJsonAsync($"/api/chats/{userId}", Settings(template: MessageTemplate.Default));

        Assert.Null(LoadChannel(userId)!.Template);
    }

    [Fact]
    public async Task Put_TemplateAtTheLengthLimit_IsAccepted_AndOverItIsRejected()
    {
        var (userId, client) = NewUser();
        var ok = "{message}" + new string('x', MessageTemplate.MaxLength - "{message}".Length);

        var accepted = await client.PutJsonAsync($"/api/chats/{userId}", Settings(templates: Map(("emergency", ok))));
        var rejected = await client.PutJsonAsync($"/api/chats/{userId}", Settings(templates: Map(("emergency", ok + "y"))));

        Assert.Equal(HttpStatusCode.OK, accepted.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    // ================= PUT /api/chats/{id}: settings =================

    [Fact]
    public async Task Put_SchedulePost_AndEventModes_ArePersisted_WhenAGroupAndAScheduleRegionExist()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, c => c.GroupId = "45.1");

        var response = await client.PutJsonAsync(
            $"/api/chats/{userId}",
            Settings(
                scheduleNotify: true,
                schedulePost: true,
                eventMode: PostMode.ReplaceDelete,
                postMode: PostMode.Edit,
                image: ScheduleImage.Card,
                postImage: ScheduleImage.None,
                outageTrigger: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var channel = LoadChannel(userId)!;
        Assert.True(channel.ScheduleNotify);
        Assert.True(channel.SchedulePost);
        Assert.Equal(PostMode.ReplaceDelete, channel.ScheduleEventMode);
        Assert.Equal(PostMode.Edit, channel.SchedulePostMode);
        Assert.Equal(ScheduleImage.Card, channel.ScheduleImage);
        Assert.Equal(ScheduleImage.None, channel.SchedulePostImage);
        Assert.True(channel.OutageTrigger);

        var summary = (await response.ReadAsync<ChatSummaryDto>())!.Settings;
        Assert.True(summary.SchedulePost);
        Assert.Equal(PostMode.ReplaceDelete, summary.ScheduleEventMode);
        Assert.Equal(PostMode.Edit, summary.SchedulePostMode);
        Assert.Equal(ScheduleImage.None, summary.SchedulePostImage);
    }

    [Fact]
    public async Task Put_ScheduleSettings_WithoutAGroup_AreSwitchedOff()
    {
        var (userId, client) = NewUser();

        var response = await client.PutJsonAsync($"/api/chats/{userId}", Settings(scheduleNotify: true, schedulePost: true));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var channel = LoadChannel(userId)!;
        Assert.False(channel.ScheduleNotify);
        Assert.False(channel.SchedulePost);
    }

    [Fact]
    public async Task Put_ScheduleSettings_ForARegionWithoutSchedules_AreSwitchedOff()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, c => { c.RegionId = "donetsk"; c.GroupId = "45.1"; });

        await client.PutJsonAsync($"/api/chats/{userId}", Settings(regionId: "donetsk", scheduleNotify: true, schedulePost: true));

        var channel = LoadChannel(userId)!;
        Assert.False(channel.ScheduleNotify);
        Assert.False(channel.SchedulePost);
        Assert.Equal("donetsk", channel.RegionId);
    }

    [Fact]
    public async Task Put_ChangingTheRegion_ClearsTheGroupAndTheScheduleSwitches()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, c =>
        {
            c.GroupId = "45.1";
            c.AddressLabel = "вул. А";
            c.AddressStreet = "вул. А";
            c.AddressHouse = "1";
            c.ScheduleNotify = true;
            c.SchedulePost = true;
        });

        await client.PutJsonAsync($"/api/chats/{userId}", Settings(regionId: "odesa", scheduleNotify: true, schedulePost: true));

        var channel = LoadChannel(userId)!;
        Assert.Equal("odesa", channel.RegionId);
        Assert.Null(channel.GroupId);
        Assert.Null(channel.AddressLabel);
        Assert.Null(channel.AddressStreet);
        Assert.Null(channel.AddressHouse);
        Assert.False(channel.ScheduleNotify);
        Assert.False(channel.SchedulePost);
    }

    [Fact]
    public async Task Put_SwitchingTheSubscriptionOff_ClearsTheAddressAndEverythingThatDependsOnIt()
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, c =>
        {
            c.GroupId = "45.1";
            c.AddressStreet = "вул. А";
            c.OutageTrigger = true;
            c.ScheduleNotify = true;
            c.SchedulePost = true;
        });

        await client.PutJsonAsync($"/api/chats/{userId}", Settings(isActive: false, scheduleNotify: true, schedulePost: true, outageTrigger: true));

        var channel = LoadChannel(userId)!;
        Assert.False(channel.IsActive);
        Assert.Null(channel.GroupId);
        Assert.Null(channel.AddressStreet);
        Assert.False(channel.OutageTrigger);
        Assert.False(channel.ScheduleNotify);
        Assert.False(channel.SchedulePost);
    }

    [Fact]
    public async Task Put_ActiveWithoutARegion_Is400()
    {
        var (userId, client) = NewUser();

        var response = await client.PutJsonAsync($"/api/chats/{userId}", Settings(regionId: null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Оберіть регіон.", (await response.ReadAsync<ApiError>())!.Error);
    }

    [Fact]
    public async Task Put_Districts_ArePersisted_AndUnknownOnesAreRejected()
    {
        var (userId, client) = NewUser();

        var ok = await client.PutJsonAsync($"/api/chats/{userId}", Settings(partIds: ["obolonskyi", "DARNYTSKYI", "obolonskyi"]));
        Assert.Equal(["obolonskyi", "darnytskyi"], LoadChannel(userId)!.PartIdList);

        var bad = await client.PutJsonAsync($"/api/chats/{userId}", Settings(partIds: ["atlantis"]));
        var retired = await client.PutJsonAsync($"/api/chats/{userId}", Settings(partIds: ["right-bank"]));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, retired.StatusCode);
        Assert.Equal("Невідомий район.", (await bad.ReadAsync<ApiError>())!.Error);
    }

    [Fact]
    public async Task Put_ForAChatTheUserCannotManage_Is403()
    {
        var (_, client) = NewUser();
        var otherChat = _factory.NextId();

        var response = await client.PutJsonAsync($"/api/chats/{otherChat}", Settings());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Null(LoadChannel(otherChat));
    }

    [Fact]
    public async Task Put_ForALinkedChannel_NeedsTheUserToStillBeAnAdmin()
    {
        var (userId, client) = NewUser();
        var channelId = -_factory.NextId();
        SeedChannel(channelId, c => c.Title = "Мій канал");
        _factory.WithDb(db => db.ChannelAdmins.Add(new ChannelAdmin { ChatId = channelId, UserId = userId, LinkedAt = DateTimeOffset.UtcNow }));

        var denied = await client.PutJsonAsync($"/api/chats/{channelId}", Settings());
        _factory.Bot.SetStatus(userId, ChatMemberStatus.Administrator);
        var allowed = await client.PutJsonAsync($"/api/chats/{channelId}", Settings());

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Put_NullBody_Is400()
    {
        var (userId, client) = NewUser();

        var response = await client.PutAsync($"/api/chats/{userId}", new StringContent("null", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ================= DELETE /api/chats + POST /api/chats/link =================

    private void SeedLinkedChannel(long userId, long channelId, Action<Channel>? configure = null)
    {
        SeedChannel(channelId, configure);
        _factory.WithDb(db => db.ChannelAdmins.Add(new ChannelAdmin { ChatId = channelId, UserId = userId, LinkedAt = DateTimeOffset.UtcNow }));
    }

    private async Task<IReadOnlyList<ChatSummaryDto>> ListAsync(HttpClient client) =>
        (await (await client.GetAsync("/api/chats")).ReadAsync<List<ChatSummaryDto>>())!;

    [Fact]
    public async Task Delete_LastAdmin_RemovesTheChannelFromTheList_AlsoForAFreshClient_AndResetsIt()
    {
        var (userId, client) = NewUser();
        var channelId = -_factory.NextId();
        SeedLinkedChannel(userId, channelId, c => { ChannelAssert.Customize(c); c.Title = "Мій канал"; });
        Assert.Contains(await ListAsync(client), s => s.ChatId == channelId);

        var response = await client.DeleteAsync($"/api/chats/{channelId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(await ListAsync(client), s => s.ChatId == channelId);
        using var fresh = _factory.ClientFor(userId);
        Assert.DoesNotContain(await ListAsync(fresh), s => s.ChatId == channelId);
        ChannelAssert.IsCleanUnsubscribed(LoadChannel(channelId)!);
    }

    [Fact]
    public async Task Delete_ThenTheBotReAddedAsAdmin_ShowsACleanUnsubscribedChannel_ViaLink()
    {
        var (userId, client) = NewUser();
        var channelId = -_factory.NextId();
        SeedLinkedChannel(userId, channelId, c => { ChannelAssert.Customize(c); c.Title = "Мій канал"; });
        _factory.Bot.Chats[channelId] = new ChatFullInfo { Id = channelId, Type = ChatType.Channel, Title = "Мій канал" };
        _factory.Bot.SetStatus(userId, ChatMemberStatus.Administrator);
        await client.DeleteAsync($"/api/chats/{channelId}");

        var response = await client.PostJsonAsync("/api/chats/link", new LinkChatRequest(channelId.ToString()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var summary = (await response.ReadAsync<ChatSummaryDto>())!;
        Assert.False(summary.Settings.IsActive);
        var listed = Assert.Single(await ListAsync(client), s => s.ChatId == channelId);
        Assert.False(listed.Settings.IsActive);
        ChannelAssert.IsCleanUnsubscribed(LoadChannel(channelId)!);
    }

    [Fact]
    public async Task Link_ToAChannelAnotherAdminManages_KeepsItsSettings()
    {
        var (userId, client) = NewUser();
        var owner = _factory.NextId();
        var channelId = -_factory.NextId();
        SeedLinkedChannel(owner, channelId, ChannelAssert.Customize);
        _factory.Bot.Chats[channelId] = new ChatFullInfo { Id = channelId, Type = ChatType.Channel, Title = "Мій канал" };
        _factory.Bot.SetStatus(userId, ChatMemberStatus.Administrator);

        var response = await client.PostJsonAsync("/api/chats/link", new LinkChatRequest(channelId.ToString()));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var channel = LoadChannel(channelId)!;
        Assert.True(channel.IsActive);
        Assert.Equal("45.1", channel.GroupId);
    }

    [Fact]
    public async Task Delete_WhenAnotherAdminRemains_OnlyUnlinksTheCaller()
    {
        var (userId, client) = NewUser();
        var other = _factory.NextId();
        var channelId = -_factory.NextId();
        SeedLinkedChannel(userId, channelId, ChannelAssert.Customize);
        _factory.WithDb(db => db.ChannelAdmins.Add(new ChannelAdmin { ChatId = channelId, UserId = other, LinkedAt = DateTimeOffset.UtcNow }));

        await client.DeleteAsync($"/api/chats/{channelId}");

        Assert.DoesNotContain(await ListAsync(client), s => s.ChatId == channelId);
        Assert.True(LoadChannel(channelId)!.IsActive);
    }

    // ================= POST /api/preview =================

    private static PreviewRequest Preview(
        string? slot = null,
        string? template = null,
        string? regionId = "kyiv",
        string? groupId = null,
        ScheduleImage? image = null,
        IReadOnlyList<string>? partIds = null,
        AlertKind kind = AlertKind.Emergency) =>
        new(template, kind, regionId, null, partIds, slot, groupId, image);

    private async Task<PreviewResponse> PreviewAsync(PreviewRequest request)
    {
        var (_, client) = NewUser();
        var response = await client.PostJsonAsync("/api/preview", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.ReadAsync<PreviewResponse>())!;
    }

    [Fact]
    public async Task Preview_OffSlot_RendersTheSampleWording()
    {
        var result = await PreviewAsync(Preview("off"));

        Assert.Equal("🔴 14:24 Світло вимкнено за графіком\n🕓 Воно було 17год 23хв\n🗓 Очікуємо за графіком о 18:00", result.Html);
        Assert.Empty(result.Errors);
        Assert.Null(result.ImageUrl);
    }

    [Fact]
    public async Task Preview_OnSlot_RendersTheSampleWording()
    {
        var result = await PreviewAsync(Preview("on"));

        Assert.Equal(
            "🟢 18:07 Світло з'явилося за графіком\n🕓 Його не було 3год 42хв\n🗓 Наступне відключення за графіком о 22:00",
            result.Html);
    }

    [Fact]
    public async Task Preview_OffSlot_WithAGroup_FillsInTheRealRegionAndGroup()
    {
        var result = await PreviewAsync(Preview("off", "{region}|{group}|{time}", groupId: "1.1"));

        Assert.Equal("м. Київ|1.1|14:24", result.Html);
    }

    [Fact]
    public async Task Preview_ScheduleSlot_WithAGroup_UsesTheRealSchedule_AndReturnsTheGridUrl()
    {
        var result = await PreviewAsync(Preview("schedule", groupId: "45.1"));

        Assert.Equal(
            "❗️ Оновлений графік відключень на сьогодні\n📅 Четвер 15.10.2026\n\n🔴 11:00 - 14:00 (~3 год)\n🔴 20:30 - 24:00 (~3,5 год)",
            result.Html);
        Assert.Equal(GridBase + "gpv-45-1-emergency.png?v=1991457b4408", result.ImageUrl);
    }

    [Theory]
    [InlineData(ScheduleImage.Card, "gpv-45-1-summary.png?v=1991457b4408")]
    [InlineData(ScheduleImage.Grid, "gpv-45-1-emergency.png?v=1991457b4408")]
    [InlineData(ScheduleImage.None, null)]
    public async Task Preview_ScheduleSlot_ImageKind_PicksThePicture(ScheduleImage kind, string? file)
    {
        var result = await PreviewAsync(Preview("schedule", groupId: "45.1", image: kind));

        Assert.Equal(file is null ? null : GridBase + file, result.ImageUrl);
    }

    [Fact]
    public async Task Preview_ScheduleSlot_WithoutAGroup_UsesSampleTextAndASampleGroupPicture()
    {
        var result = await PreviewAsync(Preview("schedule"));

        Assert.Equal(
            "❗️ Оновлений графік відключень на сьогодні\n📅 Четвер 15.10.2026\n\n🔴 14:00 - 18:00 (~4 год)",
            result.Html);
        Assert.Equal(GridBase + "gpv-1-1-emergency.png?v=1991457b4408", result.ImageUrl);
    }

    [Fact]
    public async Task Preview_ScheduleSlot_WithAnUnknownGroup_FallsBackToTheSample()
    {
        var result = await PreviewAsync(Preview("schedule", groupId: "99.9"));

        Assert.Contains("🔴 14:00 - 18:00 (~4 год)", result.Html);
        Assert.NotNull(result.ImageUrl);
    }

    [Fact]
    public async Task Preview_ScheduleSlot_ForARegionWithoutSchedules_HasNoPicture()
    {
        var result = await PreviewAsync(Preview("schedule", regionId: "donetsk", groupId: "45.1"));

        Assert.Null(result.ImageUrl);
        Assert.Contains("❗️ Оновлений графік", result.Html);
    }

    [Fact]
    public async Task Preview_ScheduleSlot_UsesTheCustomTemplate()
    {
        var result = await PreviewAsync(Preview("schedule", "{weekday} {date}|{total}|{group}\n{outages}", groupId: "45.1"));

        Assert.Equal(
            "Четвер 15.10.2026|~6,5 год|45.1\n🔴 11:00 - 14:00 (~3 год)\n🔴 20:30 - 24:00 (~3,5 год)",
            result.Html);
    }

    [Theory]
    [InlineData("emergency", "{outages}", "Невідомий параметр {відключення}.")]
    [InlineData("emergency", "{відключення}", "Невідомий параметр {відключення}.")]
    [InlineData("off", "{emoji}", "Невідомий параметр {значок}.")]
    [InlineData("schedule", "no content", "Додайте {відключення}")]
    [InlineData("local", "{#address}x", "не закрито")]
    public async Task Preview_InvalidTemplate_ReturnsErrors_AndNoHtml(string slot, string template, string expected)
    {
        var result = await PreviewAsync(Preview(slot, template));

        Assert.Equal("", result.Html);
        Assert.Contains(result.Errors, e => e.Contains(expected));
        Assert.Null(result.ImageUrl);
    }

    [Fact]
    public async Task Preview_UnknownSlot_Is400()
    {
        var (_, client) = NewUser();

        var response = await client.PostJsonAsync("/api/preview", Preview("bogus"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Невідомий тип повідомлення.", (await response.ReadAsync<ApiError>())!.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Preview_BlankTemplate_RendersTheSlotDefault(string? template)
    {
        var result = await PreviewAsync(Preview("off", template));

        Assert.StartsWith("🔴 14:24 Світло вимкнено за графіком", result.Html);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("emergency", "🚨")]
    [InlineData("regional", "⚠")]
    [InlineData("ended", "ℹ️")]
    [InlineData("local", "🚨 Локальне відключення")]
    public async Task Preview_AlertSlots_UseTheirOwnSampleKind(string slot, string expected)
    {
        var result = await PreviewAsync(Preview(slot, groupId: "45.1"));

        Assert.StartsWith(expected, result.Html);
    }

    [Fact]
    public async Task Preview_LocalSlot_ShowsTheGroupAndASampleAddress()
    {
        var result = await PreviewAsync(Preview("local", groupId: "45.1"));

        Assert.Contains("черга 45.1", result.Html);
        Assert.Contains("📍 вул. Крушельницької, 13", result.Html);
        Assert.Contains("Деталі: https://www.dtek-kem.com.ua/ua/shutdowns", result.Html);
    }

    [Fact]
    public async Task Preview_EmergencySlot_NamesTheChosenDistricts()
    {
        var result = await PreviewAsync(Preview("emergency", "{where}|{part}|{message}", partIds: ["obolonskyi"]));

        Assert.StartsWith("м. Київ (Оболонський район)|Оболонський район|м. Київ (Оболонський район): введені екстрені відключення", result.Html);
    }

    [Fact]
    public async Task Preview_WithoutASlot_IsTheLegacyCallAndUsesTheKind()
    {
        var accident = await PreviewAsync(Preview(null, kind: AlertKind.Accident));
        var ended = await PreviewAsync(Preview(null, kind: AlertKind.Ended));

        Assert.StartsWith("⚠", accident.Html);
        Assert.StartsWith("ℹ️", ended.Html);
    }

    [Fact]
    public async Task Preview_UnknownRegion_FallsBackToTheFirstRegion()
    {
        var result = await PreviewAsync(Preview("emergency", "{region}|{message}", regionId: "atlantis"));

        Assert.StartsWith("м. Київ|", result.Html);
    }

    [Fact]
    public async Task Preview_EscapesValues_ButKeepsTheTemplateMarkup()
    {
        var result = await PreviewAsync(Preview("emergency", "<b>{region}</b> {message}"));

        Assert.StartsWith("<b>м. Київ</b> ", result.Html);
    }

    // ================= POST /api/chats/{id}/templates/{slot}/send =================

    private async Task<(long ChatId, HttpClient Client)> UserWithChannelAsync(Action<Channel>? configure = null)
    {
        var (userId, client) = NewUser();
        SeedChannel(userId, configure);
        await Task.CompletedTask;
        return (userId, client);
    }

    private static SendTestRequest Test(string? template = null, ScheduleImage? image = null) => new(template, image);

    [Fact]
    public async Task Send_Success_PostsATestMessage_IntoTheChat()
    {
        var (chatId, client) = await UserWithChannelAsync();

        var response = await client.PostJsonAsync($"/api/chats/{chatId}/templates/emergency/send", Test());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("ok").GetBoolean());
        var message = Assert.Single(_factory.Bot.SentTo(chatId));
        Assert.StartsWith("🧪 <b>Тест</b>\n\n🚨 ", message.Text);
        Assert.Contains("Деталі: https://www.dtek-kem.com.ua/ua/shutdowns", message.Text);
        Assert.Equal(ParseMode.Html, message.ParseMode);
    }

    [Fact]
    public async Task Send_UsesTheTemplateFromTheRequest_WhenGiven()
    {
        var (chatId, client) = await UserWithChannelAsync(c => c.TemplateMap = Map(("emergency", "CHANNEL {message}")));

        await client.PostJsonAsync($"/api/chats/{chatId}/templates/emergency/send", Test("REQ {region} {message}"));

        Assert.StartsWith("🧪 <b>Тест</b>\n\nREQ м. Київ ", Assert.Single(_factory.Bot.SentTo(chatId)).Text);
    }

    [Fact]
    public async Task Send_UsesTheChannelsOwnTemplate_WhenTheRequestHasNone()
    {
        var (chatId, client) = await UserWithChannelAsync(c => c.TemplateMap = Map(("regional", "CHANNEL {region} {message}")));

        await client.PostJsonAsync($"/api/chats/{chatId}/templates/regional/send", Test());

        Assert.StartsWith("🧪 <b>Тест</b>\n\nCHANNEL м. Київ ", Assert.Single(_factory.Bot.SentTo(chatId)).Text);
    }

    [Fact]
    public async Task Send_UsesTheLegacyTemplate_ForAlertSlots_ButNotForTheOthers()
    {
        var (chatId, client) = await UserWithChannelAsync(c => c.Template = "LEGACY {region} {message}");

        await client.PostJsonAsync($"/api/chats/{chatId}/templates/ended/send", Test());
        await client.PostJsonAsync($"/api/chats/{chatId}/templates/off/send", Test());

        var texts = _factory.Bot.SentTo(chatId).Select(s => s.Text).ToList();
        Assert.StartsWith("🧪 <b>Тест</b>\n\nLEGACY м. Київ ", texts[0]);
        Assert.StartsWith("🧪 <b>Тест</b>\n\n🔴 14:24 Світло вимкнено", texts[1]);
    }

    [Fact]
    public async Task Send_ScheduleSlot_PostsAPhoto_WithTheChannelsPostPicture()
    {
        var (chatId, client) = await UserWithChannelAsync(c => { c.GroupId = "45.1"; c.SchedulePostImage = ScheduleImage.Card; });

        var response = await client.PostJsonAsync($"/api/chats/{chatId}/templates/schedule/send", Test());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var photo = Assert.Single(_factory.Bot.Photos, p => p.ChatId.Identifier == chatId);
        Assert.Equal(GridBase + "gpv-45-1-summary.png?v=1991457b4408", ((Telegram.Bot.Types.InputFileUrl)photo.Photo).Url.ToString());
        Assert.StartsWith("🧪 <b>Тест</b>\n\n❗️ Оновлений графік відключень на сьогодні", photo.Caption);
        Assert.Contains("🔴 11:00 - 14:00 (~3 год)", photo.Caption);
    }

    [Fact]
    public async Task Send_ScheduleSlot_ImageKindNone_PostsText()
    {
        var (chatId, client) = await UserWithChannelAsync(c => c.GroupId = "45.1");

        await client.PostJsonAsync($"/api/chats/{chatId}/templates/schedule/send", Test(image: ScheduleImage.None));

        Assert.DoesNotContain(_factory.Bot.Photos, p => p.ChatId.Identifier == chatId);
        Assert.StartsWith("🧪 <b>Тест</b>\n\n❗️", Assert.Single(_factory.Bot.SentTo(chatId)).Text);
    }

    [Fact]
    public async Task Send_NonScheduleSlots_AreTextOnly_ByDefault()
    {
        var (chatId, client) = await UserWithChannelAsync(c => c.GroupId = "45.1");

        await client.PostJsonAsync($"/api/chats/{chatId}/templates/on/send", Test());

        Assert.DoesNotContain(_factory.Bot.Photos, p => p.ChatId.Identifier == chatId);
        Assert.Single(_factory.Bot.SentTo(chatId));
    }

    [Fact]
    public async Task Send_ForAChatTheUserCannotManage_Is403_AndSendsNothing()
    {
        var (_, client) = NewUser();
        var other = _factory.NextId();
        SeedChannel(other);

        var response = await client.PostJsonAsync($"/api/chats/{other}/templates/emergency/send", Test());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_factory.Bot.SentTo(other));
    }

    [Fact]
    public async Task Send_UnknownSlot_Is400()
    {
        var (chatId, client) = await UserWithChannelAsync();

        var response = await client.PostJsonAsync($"/api/chats/{chatId}/templates/bogus/send", Test());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Невідомий тип повідомлення.", (await response.ReadAsync<ApiError>())!.Error);
        Assert.Empty(_factory.Bot.SentTo(chatId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Send_WithoutAChannelOrRegion_Is400(bool rowExists)
    {
        var (userId, client) = NewUser();
        if (rowExists)
        {
            SeedChannel(userId, c => c.RegionId = "");
        }

        var response = await client.PostJsonAsync($"/api/chats/{userId}/templates/emergency/send", Test());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("Спершу оберіть регіон", (await response.ReadAsync<ApiError>())!.Error);
        Assert.Empty(_factory.Bot.SentTo(userId));
    }

    [Fact]
    public async Task Send_InvalidTemplate_Is400_WithDetails()
    {
        var (chatId, client) = await UserWithChannelAsync();

        var response = await client.PostJsonAsync($"/api/chats/{chatId}/templates/off/send", Test("{emoji}"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (await response.ReadAsync<ApiError>())!;
        Assert.Equal("Шаблон містить помилки.", error.Error);
        Assert.Contains(error.Details!, d => d.Contains("{значок}"));
        Assert.Empty(_factory.Bot.SentTo(chatId));
    }

    [Fact]
    public async Task Send_InvalidChannelTemplate_IsAlsoRejected()
    {
        var (chatId, client) = await UserWithChannelAsync(c => c.TemplateMap = Map(("on", "{outages}")));

        var response = await client.PostJsonAsync($"/api/chats/{chatId}/templates/on/send", Test());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Send_IsLimitedToFivePerMinute_PerChat()
    {
        var (chatId, client) = await UserWithChannelAsync();

        for (var i = 0; i < 5; i++)
        {
            var ok = await client.PostJsonAsync($"/api/chats/{chatId}/templates/emergency/send", Test());
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var limited = await client.PostJsonAsync($"/api/chats/{chatId}/templates/emergency/send", Test());

        Assert.Equal((HttpStatusCode)429, limited.StatusCode);
        Assert.Equal(5, _factory.Bot.SentTo(chatId).Count());

        // A minute later the window has moved on.
        _factory.Clock.Advance(TimeSpan.FromSeconds(61));
        var again = await client.PostJsonAsync($"/api/chats/{chatId}/templates/emergency/send", Test());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    [Fact]
    public async Task Send_Limit_IsPerChat_AndRejectedRequestsDoNotCountTowardsIt()
    {
        var (chatId, client) = await UserWithChannelAsync();
        var (otherId, otherClient) = await UserWithChannelAsync();

        for (var i = 0; i < 6; i++)
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync($"/api/chats/{chatId}/templates/off/send", Test("{emoji}"))).StatusCode);
        }

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync($"/api/chats/{chatId}/templates/off/send", Test())).StatusCode);
        }

        Assert.Equal((HttpStatusCode)429, (await client.PostJsonAsync($"/api/chats/{chatId}/templates/off/send", Test())).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await otherClient.PostJsonAsync($"/api/chats/{otherId}/templates/off/send", Test())).StatusCode);
    }

    [Fact]
    public async Task Send_TelegramForbidden_Is400_WithAHint()
    {
        var (chatId, client) = await UserWithChannelAsync();
        _factory.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        var response = await client.PostJsonAsync($"/api/chats/{chatId}/templates/emergency/send", Test());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.StartsWith("Бот не може писати в цей чат", (await response.ReadAsync<ApiError>())!.Error);
    }

    [Fact]
    public async Task Send_OtherTelegramRejection_IsReportedWithItsMessage()
    {
        var (chatId, client) = await UserWithChannelAsync();
        _factory.Bot.Failure = r => r is SendMessageRequest ? new ApiRequestException("Bad Request: message is too long", 400) : null;

        var response = await client.PostJsonAsync($"/api/chats/{chatId}/templates/emergency/send", Test());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Telegram не прийняв повідомлення: Bad Request: message is too long", (await response.ReadAsync<ApiError>())!.Error);
    }

    [Fact]
    public async Task Send_ForALinkedChannel_NeedsAdminRights_AndPostsIntoTheChannel()
    {
        var (userId, client) = NewUser();
        var channelId = -_factory.NextId();
        SeedChannel(channelId, c => c.Title = "Канал");
        _factory.WithDb(db => db.ChannelAdmins.Add(new ChannelAdmin { ChatId = channelId, UserId = userId, LinkedAt = DateTimeOffset.UtcNow }));
        _factory.Bot.SetStatus(userId, ChatMemberStatus.Administrator);

        var response = await client.PostJsonAsync($"/api/chats/{channelId}/templates/emergency/send", Test());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Single(_factory.Bot.SentTo(channelId));
    }

    [Fact]
    public async Task Send_RenderedTemplatesNeverMatchThePowerOutTrigger()
    {
        var (chatId, client) = await UserWithChannelAsync(c => c.GroupId = "45.1");
        var regex = new System.Text.RegularExpressions.Regex(@"світло\s+зникло", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var slot in MessageSlots.All.Select(s => s.Id))
        {
            _factory.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync($"/api/chats/{chatId}/templates/{slot}/send", Test(image: ScheduleImage.None))).StatusCode);
        }

        Assert.All(_factory.Bot.SentTo(chatId), s => Assert.DoesNotMatch(regex, s.Text));
        Assert.Equal(MessageSlots.All.Count, _factory.Bot.SentTo(chatId).Count());
    }
}
