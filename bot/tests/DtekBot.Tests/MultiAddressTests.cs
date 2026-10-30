using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DtekBot.Api.Checks;
using DtekBot.Api.Data;
using DtekBot.Api.Data.Migrations;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Requests;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class MultiAddressTests : IDisposable
{
    private readonly DispatcherFixture f = new();
    public void Dispose() => f.Dispose();

    private static ChatAddress Address(string name, params string[] groups) => new()
    {
        Name = name, RegionId = "kyiv", Street = "Вулиця " + name, House = "1",
        Groups = groups.Select(id => new ChatAddressGroup { GroupId = id }).ToList(),
    };

    [Fact]
    public void Migration_BackfillsAddressAndGroupWithoutChangingScalarDataOrMessageState()
    {
        f.AddChannel(11, c => { c.GroupId = "3.2"; c.AddressCity = "Київ"; c.AddressStreet = "Вулиця";
            c.AddressHouse = "7"; c.AddressLabel = "Повна адреса"; c.PartId = "darnytskyi"; });
        f.AddChannel(12, c => c.GroupId = "4.1");
        f.AddChannel(13);
        f.Db.SchedulePostThreads.Add(new SchedulePostThread { ChatId = 11, RegionId = "kyiv", GroupId = "3.2", MessageId = 123 });
        f.Db.AddressChecks.Add(new AddressCheck { ChatId = 11, RegionId = "kyiv", City = "Київ", Street = "Вулиця", House = "7",
            RequestedAt = f.Clock.Now, BudgetCharged = false });
        f.Db.SaveChanges();
        var migration = new MultiAddressGroups();
        foreach (var operation in migration.UpOperations.OfType<SqlOperation>())
            f.Db.Database.ExecuteSqlRaw(operation.Sql);
        f.Db.ChangeTracker.Clear();
        var migrated = f.LoadChannel(11);
        var address = Assert.Single(migrated.Addresses);
        Assert.Equal("Київ", address.City); Assert.Equal("Вулиця", address.Street); Assert.Equal("7", address.House);
        Assert.Equal("Повна адреса", address.Label); Assert.Equal("darnytskyi", address.PartIds);
        Assert.Equal("3.2", Assert.Single(address.Groups).GroupId);
        Assert.Equal("Повна адреса", migrated.AddressLabel);
        Assert.Equal("4.1", Assert.Single(Assert.Single(f.LoadChannel(12).Addresses).Groups).GroupId);
        Assert.Empty(f.LoadChannel(13).Addresses);
        Assert.Equal(123, f.Db.SchedulePostThreads.Single().MessageId);
        Assert.Equal(address.Id, f.Db.AddressChecks.Single().AddressId);
        Assert.True(f.Db.AddressChecks.Single().BudgetCharged);
    }

    [Fact]
    public void Rendering_OneAddressOneGroupHasBothDaysWithoutGroupHeading()
    {
        var channel = new Channel { RegionId = "kyiv", GroupId = "45.1", Addresses = [Address("Дім", "45.1")] };
        var snapshot = RealSnapshot();
        var expected = "❗️ Графік відключень на сьогодні\n📅 Четвер 15.10.2026\n\n🔴 11:00 - 14:00 (~3 год)\n🔴 20:30 - 24:00 (~3,5 год)"
            + "\n\n❗️ Графік відключень на завтра\n📅 Пʼятниця 16.10.2026\n\n🔴 06:00 - 09:30 (~3,5 год)\n🔴 17:00 - 20:30 (~3,5 год)";
        Assert.Equal(expected, CombinedSchedule.Render(channel, snapshot, "м. Київ", Kyiv(15, 12), false));
    }

    [Fact]
    public void Rendering_CustomHeadingAndIntervalsApplyToBothDaysWithoutLegacyMetadata()
    {
        var channel = new Channel { RegionId = "kyiv", Addresses = [Address("Дім <1>", "45.1", "2.1")] };
        channel.TemplateMap = new Dictionary<string, string>
        {
            [MessageSlots.Schedule] = "{outages}\n{tomorrowDate}\n{total}\n{updated}",
            [MessageSlots.ScheduleHeading] = "⚡ {header}\n{weekday} · {date}",
            [MessageSlots.ScheduleLine] = "🕒 {start}  →  {end}",
        };
        var text = CombinedSchedule.Render(channel, RealSnapshot(), "м. Київ", Kyiv(15, 12), false);
        Assert.Equal(1, text.Split("Графік відключень на сьогодні").Length - 1);
        Assert.Equal(1, text.Split("Графік відключень на завтра").Length - 1);
        Assert.Equal(1, text.Split("Четвер · 15.10.2026").Length - 1);
        Assert.Equal(1, text.Split("Пʼятниця · 16.10.2026").Length - 1);
        Assert.Contains("🕒 11:00  →  14:00", text);
        Assert.Contains("🕒 06:00  →  09:30", text);
        Assert.Contains("<b>Черга 45.1</b> · Дім &lt;1&gt;", text);
        Assert.DoesNotContain(" ? ", text);
        Assert.DoesNotContain("~6,5 год", text);
        Assert.DoesNotContain("20:24", text);
    }

    [Fact]
    public void Rendering_UnpublishedTomorrowAndMissingGroupAreNotReportedAsNoOutages()
    {
        var channel = new Channel { RegionId = "kyiv", Addresses = [Address("Дім", "45.1", "missing")] };
        var snapshot = RealSnapshot() with { CoverageEnd = Kyiv(16, 0) };
        var text = CombinedSchedule.Render(channel, snapshot, "м. Київ", Kyiv(15, 12), false);
        Assert.Contains("<b>Черга missing</b> · Дім\nГрафік тимчасово недоступний.", text);
        Assert.EndsWith("Пʼятниця 16.10.2026\n\nГрафік ще не оприлюднено.", text);
        Assert.DoesNotContain("06:00", text);
    }

    [Fact]
    public void Rendering_GeographicPlaceholdersUseSavedValuesAndEscapeAddresses()
    {
        var channel = new Channel { RegionId = "kyiv", Addresses = [Address("Дім <1>", "45.1")] };
        channel.TemplateMap = new Dictionary<string, string>
        {
            [MessageSlots.ScheduleHeading] = "{region}{#part} · {part}{/part}{#address}\n{address}{/address}\n{header}",
        };
        var text = CombinedSchedule.Render(channel, RealSnapshot(), "м. Київ", Kyiv(15, 12), false, ["Оболонський район"]);
        Assert.Contains("м. Київ · Оболонський район\nВулиця Дім &lt;1&gt;, 1\nГрафік", text);
        channel.Addresses.Clear();
        channel.GroupId = "45.1";
        text = CombinedSchedule.Render(channel, RealSnapshot(), "м. Київ", Kyiv(15, 12), false);
        Assert.StartsWith("м. Київ\nГрафік", text);
    }

    [Fact]
    public void Rendering_TwoGroupsAndSharedAddressDeduplicatesAndEscapesLabels()
    {
        var channel = new Channel { RegionId = "kyiv", Addresses = [Address("Дім <1>", "45.1", "2.1"), Address("Офіс", "45.1")] };
        channel.Addresses[0].Groups[0].Label = "Під'їзд 1";
        Assert.Equal(new[] { "2.1", "45.1" }, channel.GroupIds());
        var text = CombinedSchedule.Render(channel, RealSnapshot(), "м. Київ", Kyiv(15, 12), false);
        Assert.Equal(2, text.Split("<b>Черга 45.1</b>").Length - 1);
        Assert.Contains("Дім &lt;1&gt;", text); Assert.Contains("Офіс", text); Assert.Contains("Під&#39;їзд 1", text);
        Assert.Contains("<b>Черга 2.1</b>", text); Assert.Contains("Графік відключень на завтра", text);
    }

    [Fact]
    public void WeeklyUnion_IsHalfHourAwareAndRequiresEveryGroup()
    {
        var snapshot = RealSnapshot() with { WeeklyPresets = new Dictionary<string, string> { ["3.2"] = "2301;0000", ["4.1"] = "3200;0100" } };
        Assert.Equal("1101;0100", WeeklyPreset.Union(snapshot, ["3.2", "4.1", "3.2"]));
        Assert.Null(WeeklyPreset.Union(snapshot, ["3.2", "missing"]));
    }

    [Fact]
    public async Task Reminders_SameStartAcrossGroupsOnlySendsOnce_EvenAfterMembershipChanges()
    {
        var snapshot = RealSnapshot();
        var outage = new OutageInterval(Kyiv(15, 11), Kyiv(15, 12));
        var provider = new FakeScheduleProvider();
        provider.Snapshots["kyiv"] = snapshot with { Groups = new Dictionary<string, GroupSchedule>
            { ["45.1"] = new("45.1", [outage]), ["2.1"] = new("2.1", [outage]) } };
        var channel = f.AddChannel(21, c => { c.GroupId = "45.1"; c.ScheduleReminder = true; c.ScheduleReminderMinutes = 15;
            c.Addresses = [Address("Дім", "45.1"), Address("Офіс", "2.1")]; });
        var clock = new TestClock(Kyiv(15, 10, 45));
        var notifier = new ScheduleNotifier(f.Db, f.Bot, provider, f.Options, clock, NullLogger<ScheduleNotifier>.Instance, f.CreateMirror(time: clock));
        await notifier.TickAsync(default); await notifier.TickAsync(default);
        var message = Assert.Single(f.Bot.Requests.OfType<SendMessageRequest>());
        Assert.Contains("45.1", message.Text); Assert.Contains("2.1", message.Text);
        Assert.Contains("Дім", message.Text); Assert.Contains("Офіс", message.Text);
        Assert.Contains("Дім · 45.1", message.Text); Assert.Contains("Офіс · 2.1", message.Text);
        Assert.DoesNotContain(" ? ", message.Text);
        Assert.Single(f.Db.ScheduleReminderDeliveries);
        channel.GroupId = "2.1";
        await notifier.TickAsync(default);
        Assert.Single(f.Bot.Requests.OfType<SendMessageRequest>());
    }

    [Theory]
    [InlineData(PostMode.Edit)]
    [InlineData(PostMode.New)]
    [InlineData(PostMode.ReplaceDelete)]
    public async Task CombinedPost_ModesTrackOneDigestAndSkipUnchanged(PostMode mode)
    {
        var provider = new FakeScheduleProvider(); provider.Snapshots["kyiv"] = RealSnapshot();
        var channel = f.AddChannel(22, c => { c.GroupId = "45.1"; c.SchedulePost = true; c.SchedulePostMode = mode;
            c.Addresses = [Address("Дім", "45.1"), Address("Офіс", "2.1")]; });
        var notifier = new ScheduleNotifier(f.Db, f.Bot, provider, f.Options, new TestClock(Kyiv(15, 12)), NullLogger<ScheduleNotifier>.Instance, f.CreateMirror());
        Assert.Equal(1, (await notifier.TickAsync(default)).Sent);
        Assert.Equal(0, (await notifier.TickAsync(default)).Sent);
        Assert.Single(f.Db.SchedulePostThreads);
        channel.Addresses[1].Name = "Новий офіс";
        await notifier.TickAsync(default);
        if (mode == PostMode.Edit) Assert.Single(f.Bot.Requests.OfType<EditMessageTextRequest>());
        else Assert.Equal(2, f.Bot.Requests.OfType<SendMessageRequest>().Count());
        Assert.Equal(mode == PostMode.ReplaceDelete ? 1 : 0, f.Bot.Requests.OfType<DeleteMessageRequest>().Count());
    }

    [Fact]
    public async Task Assessment_AllAddressesUsesOwnSchedulesAndOneCheckBudget()
    {
        var channel = f.AddChannel(31, c => { c.GroupId = "45.1";
            c.Addresses = [Address("Дім", "45.1"), Address("Офіс"), Address("Гараж")]; });
        var clock = new TestClock(Kyiv(15, 12)); var trigger = new FakeWorkflowTrigger();
        var provider = new FakeScheduleProvider(); provider.Snapshots["kyiv"] = RealSnapshot();
        var checks = new AddressCheckService(f.Db, f.Bot, trigger, f.Options, clock, NullLogger<AddressCheckService>.Instance, f.CreateMirror(time: clock));
        var assessment = new OutageAssessmentService(f.Db, checks, provider, f.Options, clock);
        var text = await assessment.AssessAsync(channel, default);
        Assert.Equal(3, text.Split('\n').Length);
        Assert.Contains("Дім: Відключення за графіком", text);
        Assert.Contains("Офіс: Перевіряю адресу", text); Assert.Contains("Гараж: Перевіряю адресу", text);
        Assert.Equal(2, f.Db.AddressChecks.Count());
        Assert.Single(f.Db.AddressChecks.Where(c => c.BudgetCharged));
        Assert.Single(f.Db.AddressChecks.Select(c => c.BatchId).Distinct()); Assert.Single(trigger.Events);
    }

    [Fact]
    public async Task GroupEmergency_ReachesNonPrimaryGroupAndNamesOnlyAssociatedAddresses()
    {
        f.AddChannel(41, c => { c.GroupId = "45.1"; c.Addresses = [Address("Дім", "45.1"), Address("Офіс", "2.1")]; });
        await f.Dispatcher.DispatchGroupAsync(f.Kyiv, "2.1", true, default);
        var message = Assert.Single(f.Bot.Requests.OfType<SendMessageRequest>());
        Assert.Contains("Офіс", message.Text); Assert.Contains("2.1", message.Text); Assert.DoesNotContain("Дім", message.Text);
    }
    [Fact]
    public async Task BatchResults_AreOneReplyAndMissingSourceDoesNotUseSensorPredictions()
    {
        var channel = f.AddChannel(32, c => c.Addresses = [Address("Дім"), Address("Офіс")]);
        var clock = new TestClock(Kyiv(15, 12));
        var checks = new AddressCheckService(f.Db, f.Bot, new FakeWorkflowTrigger(), f.Options, clock, NullLogger<AddressCheckService>.Instance, f.CreateMirror(time: clock));
        await checks.RequestBatchAsync(channel, channel.Addresses, default);
        var first = channel.Addresses[0]; var second = channel.Addresses[1];
        var result = new HouseResultEntry(true, "", "", "", "");
        await checks.ProcessResultsAsync(new AddressResultsRequest([new("kyiv", "", first.Street!, new() { ["1"] = result })]), default);
        Assert.Empty(f.Bot.Requests.OfType<SendMessageRequest>());
        await checks.ProcessResultsAsync(new AddressResultsRequest([new("kyiv", "", second.Street!, new() { ["1"] = result })]), default);
        var message = Assert.Single(f.Bot.Requests.OfType<SendMessageRequest>());
        Assert.Contains("Дім", message.Text); Assert.Contains("Офіс", message.Text);
        Assert.Equal(2, f.Db.AddressChecks.Count(c => c.Status == AddressCheckStatus.Done));
    }

    [Fact]
    public async Task ChannelTrigger_AssessesAllAndIgnoresSvitlobotReturnTimeAndDuration()
    {
        var channel = f.AddChannel(-999, c => { c.OutageTrigger = true;
            c.Addresses = [Address("Дім", "45.1"), Address("Офіс", "45.1")]; });
        var clock = new TestClock(Kyiv(15, 12)); var provider = new FakeScheduleProvider();
        provider.Snapshots["kyiv"] = RealSnapshot();
        var checks = new AddressCheckService(f.Db, f.Bot, new FakeWorkflowTrigger(), f.Options, clock, NullLogger<AddressCheckService>.Instance, f.CreateMirror(time: clock));
        var assessment = new OutageAssessmentService(f.Db, checks, provider, f.Options, clock);
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var handler = new DtekBot.Api.Bot.BotUpdateHandler(f.Bot, f.Db, assessment, config, clock,
            NullLogger<DtekBot.Api.Bot.BotUpdateHandler>.Instance, f.CreateMirror(time: clock));
        await handler.HandleAsync(new Telegram.Bot.Types.Update { ChannelPost = new Telegram.Bot.Types.Message {
            Id = 123, Chat = new Telegram.Bot.Types.Chat { Id = channel.ChatId, Type = Telegram.Bot.Types.Enums.ChatType.Channel },
            Text = "🔴 20:06 Світло зникло\nОчікуємо за графіком о 23:59\nСвітло було 999 год" } }, default);
        var reply = Assert.Single(f.Bot.Requests.OfType<SendMessageRequest>());
        Assert.Equal(2, reply.Text.Split('\n').Length);
        Assert.Contains("Дім: Відключення за графіком", reply.Text); Assert.Contains("Офіс", reply.Text);
        Assert.DoesNotContain("23:59", reply.Text); Assert.DoesNotContain("999", reply.Text);
        Assert.Empty(f.Db.AddressChecks);
    }

    [Fact]
    public void LongDigestSplittingPreservesTextAndNeverBreaksSurrogatePairs()
    {
        var text = string.Join("\n", Enumerable.Repeat("<b>Черга 3.2</b> &amp; 🏠 " + new string('ї', 180), 50));
        var parts = CombinedSchedule.Parts(text);
        Assert.True(parts.Count > 1);
        Assert.All(parts, part => Assert.True(WebUtility.HtmlDecode(part).Length <= 4096));
        Assert.DoesNotContain(parts, part => part.Length > 0 && char.IsHighSurrogate(part[^1]));
    }

    [Fact]
    public void SharedGroupAcrossTwoAddressesRendersOneSectionWithBothNames()
    {
        var channel = new Channel { RegionId = "kyiv", Addresses = [Address("Дім", "45.1"), Address("Офіс", "45.1")] };
        var text = CombinedSchedule.Render(channel, RealSnapshot(), "м. Київ", Kyiv(15, 12), false);
        Assert.Equal(2, text.Split("<b>Черга 45.1</b>").Length - 1);
        Assert.Contains("Дім, Офіс", text);
        Assert.Equal(1, text.Split("Графік відключень на сьогодні").Length - 1);
    }

    [Fact]
    public async Task DistrictAlertNamesOnlyAffectedAddressesIncludingInheritedScopes()
    {
        f.AddChannel(42, c => {
            c.PartIdList = ["darnytskyi"];
            c.Addresses = [Address("Дім"), Address("Офіс")];
            c.Addresses[0].PartIds = "obolonskyi";
        });
        await f.DispatchAsync(DispatcherFixture.AccidentRightBankText);
        var message = Assert.Single(f.Bot.Requests.OfType<SendMessageRequest>());
        Assert.Contains("Дім", message.Text); Assert.DoesNotContain("Офіс", message.Text);
    }

}

public sealed class MultiAddressApiTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory factory;
    public MultiAddressApiTests(ApiFactory factory) { this.factory = factory; factory.Schedules.Snapshots["kyiv"] = RealSnapshot(); }
    private (long Id, HttpClient Client) Chat()
    {
        var id = factory.NextId(); var client = factory.ClientFor(id);
        factory.WithDb(db => db.Channels.Add(new Channel { ChatId = id, RegionId = "kyiv", CreatedAt = factory.Clock.Now }));
        return (id, client);
    }
    private static ChatAddressDto Address(string name, string region = "kyiv") => new(0, region, name, null, "Вулиця", "7", [new("45.1")]);

    [Fact]
    public async Task Save_EnforcesCapAndSingleRegion_AndOldSettingsSavePreservesAddresses()
    {
        var (id, client) = Chat();
        var url = $"/api/chats/{id}/addresses";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new SaveAddressesRequest(
            [Address("1"), Address("2"), Address("3"), Address("4")]), ApiJson.Options)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, new SaveAddressesRequest(
            [Address("1", "kyiv-oblast")]), ApiJson.Options)).StatusCode);
        (await client.PutAsJsonAsync(url, new SaveAddressesRequest([Address("Дім"), Address("Офіс")]), ApiJson.Options)).EnsureSuccessStatusCode();
        var old = new ChatSettingsDto("kyiv", null, true, false, AlertFilter.All, true, true, null);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/chats/{id}", old with { RegionId = "odesa" }, ApiJson.Options)).StatusCode);
        (await client.PutAsJsonAsync($"/api/chats/{id}", old, ApiJson.Options)).EnsureSuccessStatusCode();
        factory.WithDb(db => {
            var channel = db.Channels.Single(c => c.ChatId == id);
            Assert.Equal(2, channel.Addresses.Count); Assert.Equal("45.1", Assert.Single(channel.GroupIds()));
        });
    }

    [Fact]
    public async Task PreviewUsesSameCombinedRendererAndEscapesNames()
    {
        var (id, client) = Chat();
        var request = new PreviewRequest(null, AlertKind.Emergency, "kyiv", null, Slot: MessageSlots.Schedule,
            Addresses: [Address("Дім <script>"), Address("Офіс")]);
        var response = await client.PostAsJsonAsync("/api/preview", request, ApiJson.Options);
        response.EnsureSuccessStatusCode();
        var preview = await response.Content.ReadFromJsonAsync<PreviewResponse>(ApiJson.Options);
        Assert.Contains("Дім &lt;script&gt;", preview!.Html); Assert.Contains("Офіс", preview.Html);
        Assert.Null(preview.ImageUrl);
    }

    [Fact]
    public async Task CombinedPreviewAndTestSendUseTheSameSavedHeaderAndIntervals()
    {
        var (id, client) = Chat();
        var addresses = new[] { Address("Дім") with { Groups = [new("45.1"), new("2.1")] } };
        (await client.PutAsJsonAsync($"/api/chats/{id}/addresses",
            new SaveAddressesRequest(addresses), ApiJson.Options)).EnsureSuccessStatusCode();
        const string heading = "⚡ {header}\n{weekday} {date}";
        const string line = "🕒 {start}  →  {end}";
        factory.WithDb(db => db.Channels.Single(c => c.ChatId == id).TemplateMap = new Dictionary<string, string>
        {
            [MessageSlots.ScheduleHeading] = heading,
            [MessageSlots.ScheduleLine] = line,
        });
        var response = await client.PostAsJsonAsync("/api/preview",
            new PreviewRequest(null, AlertKind.Emergency, "kyiv", null, Slot: MessageSlots.Schedule,
                Addresses: addresses, ScheduleLineTemplate: line, ScheduleHeadingTemplate: heading), ApiJson.Options);
        response.EnsureSuccessStatusCode();
        var preview = (await response.Content.ReadFromJsonAsync<PreviewResponse>(ApiJson.Options))!;
        Assert.Empty(preview.Errors);
        var sent = await client.PostAsJsonAsync($"/api/chats/{id}/templates/schedule/send", new SendTestRequest(null), ApiJson.Options);
        sent.EnsureSuccessStatusCode();
        var message = Assert.Single(factory.Bot.Requests.OfType<SendMessageRequest>(),
            r => r.ChatId.Identifier == id && r.Text.StartsWith("🧪", StringComparison.Ordinal));
        Assert.Equal("🧪 <b>Тест</b>\n\n" + preview.Html, message.Text);
        Assert.Contains("🕒 06:00  →  09:30", message.Text);
        Assert.Equal(1, message.Text.Split("Графік відключень на завтра").Length - 1);
    }
    [Fact]
    public async Task GroupIngestDispatchesNonPrimaryMembershipAndRemovalClosesOnlyItsThread()
    {
        var (id, client) = Chat();
        factory.Bot.Requests.Clear();
        var address = Address("Дім") with { Groups = [new("45.1"), new("2.1")] };
        var response = await client.PutAsJsonAsync($"/api/chats/{id}/addresses", new SaveAddressesRequest([address]), ApiJson.Options);
        response.EnsureSuccessStatusCode();
        client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        var ingest = await client.PostAsJsonAsync("/ingest/groups", new DtekBot.Api.Groups.GroupsIngestRequest("kyiv",
            new() { ["2.1"] = new("EmergencyShutdowns", "") }), ApiJson.Options);
        ingest.EnsureSuccessStatusCode();
        Assert.Contains(factory.Bot.Requests.OfType<SendMessageRequest>(), r => r.ChatId.Identifier == id && r.Text.Contains("2.1"));
        var saved = (await response.Content.ReadFromJsonAsync<ChatSummaryDto>(ApiJson.Options))!.Settings.Addresses![0];
        var removed = saved with { Groups = [new("45.1")] };
        (await client.PutAsJsonAsync($"/api/chats/{id}/addresses", new SaveAddressesRequest([removed]), ApiJson.Options)).EnsureSuccessStatusCode();
        factory.WithDb(db => Assert.True(db.Deliveries.Single(d => d.ChatId == id && d.RegionId == "kyiv:group:2.1").IsClosed));
    }

}
