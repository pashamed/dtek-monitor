using System.Text.Json;
using System.Net.Http.Json;
using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Grid;
using DtekBot.Api.Groups;
using DtekBot.Api.Parsing;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Telegram.Bot;
using Xunit.Abstractions;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class LiveTelegramFactAttribute : FactAttribute
{
    public LiveTelegramFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DTEK_LIVE_TELEGRAM") != "1")
            Skip = "Explicit opt-in required: DTEK_LIVE_TELEGRAM=1 and test bot credentials.";
    }
}

public sealed class LiveTelegramTests(ITestOutputHelper output)
{
    [LiveTelegramFact]
    public async Task BrowserSettings_SaveReloadValidateAndSendToRealTestChannel()
    {
        const long chatId = -1004471996729;
        Assert.Equal(chatId.ToString(), Environment.GetEnvironmentVariable("TELEGRAM_TEST_CHAT_ID"));
        var token = Environment.GetEnvironmentVariable("Bot__Token");
        Assert.False(string.IsNullOrWhiteSpace(token));
        using var transport = new TestChannelTransport(chatId);
        using var telegramHttp = new HttpClient(transport);
        var bot = new TelegramBotClient(token!, telegramHttp);
        var me = await bot.GetMe();
        Assert.Equal("emergencyCheckBot", me.Username, ignoreCase: true);
        using var api = new ApiFactory(bot);
        using var client = api.CreateClient();
        api.Schedules.Snapshots["kyiv"] = RealSnapshot();
        api.WithDb(db =>
        {
            db.Channels.Add(new Channel
            {
                ChatId = chatId, RegionId = "kyiv", GroupId = "45.1", PartIdList = ["darnytskyi"],
                SchedulePost = true, ScheduleReminder = true,
                Addresses = [new ChatAddress
                {
                    RegionId = "kyiv", Name = "Тест дім", City = "Київ", Street = "Тестова", House = "1",
                    Groups = [new() { GroupId = "45.1" }],
                }],
                TemplateMap = MessageSlots.All.ToDictionary(s => s.Id, s =>
                    s.Id == MessageSlots.ScheduleLine ? s.DefaultTemplate : "🧪 ТЕСТ UI\n" + s.DefaultTemplate),
            });
            db.ChannelAdmins.Add(new ChannelAdmin { ChatId = chatId, UserId = me.Id });
        });
        var root = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "src", "lite-editor.test.js")))
            root = root.Parent;
        Assert.NotNull(root);
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var proxy = builder.Build();
        proxy.UseStaticFiles(new Microsoft.AspNetCore.Builder.StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
                Path.Combine(root.FullName, "bot", "src", "DtekBot.Api", "wwwroot")),
        });
        proxy.Run(async context =>
        {
            using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method),
                context.Request.Path + context.Request.QueryString);
            if (context.Request.ContentLength > 0)
            {
                request.Content = new StreamContent(context.Request.Body);
                request.Content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            }
            if (context.Request.Headers.TryGetValue("Authorization", out var auth))
                request.Headers.TryAddWithoutValidation("Authorization", auth.ToString());
            using var response = await client.SendAsync(request);
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentType = response.Content.Headers.ContentType?.ToString();
            await response.Content.CopyToAsync(context.Response.Body);
        });
        await proxy.StartAsync();
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo("node")
            {
                WorkingDirectory = root.FullName, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add(Path.Combine(root.FullName, "src", "live-ui-check.js"));
            start.Environment["DTEK_UI_URL"] = proxy.Urls.Single();
            start.Environment["DTEK_UI_AUTH"] = ApiFactory.InitData(me.Id, ApiFactory.BotToken, api.Clock.Now);
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(6));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            output.WriteLine(await stdout);
            Assert.True(process.ExitCode == 0, await stderr);
            api.WithDb(db =>
            {
                var channel = db.Channels.Single(c => c.ChatId == chatId);
                Assert.True(channel.SchedulePost);
                Assert.Equal(PostMode.ReplaceDelete, channel.SchedulePostMode);
                Assert.True(channel.ScheduleReminder);
                Assert.Equal(30, channel.ScheduleReminderMinutes);
                Assert.True(channel.QuietHoursEnabled);
                Assert.Equal(22 * 60 + 15, channel.QuietHoursStartMinute);
                Assert.Equal(7 * 60 + 45, channel.QuietHoursEndMinute);
                Assert.True(channel.DeletePreviousAlert);
                Assert.Equal(EndStyle.Edit, channel.SlotEndStyle(MessageSlots.Ended));
                Assert.False(channel.IsPaused);
                Assert.Equal("Тест будинок UI", Assert.Single(channel.Addresses).Name);
            });
            Assert.True(transport.Writes >= 2);
            output.WriteLine("PASS browser UI → real settings/preview/send API → SQLite persistence and Telegram delivery.");
        }
        finally
        {
            await proxy.StopAsync();
            foreach (var id in transport.LiveIds.ToArray()) await bot.DeleteMessage(chatId, id);
            Assert.Empty(transport.LiveIds);
            output.WriteLine($"CLEANUP verified: {transport.Deletes} browser test-created Telegram messages deleted; zero tracked messages remain.");
        }
    }

    [LiveTelegramFact]
    public async Task DeliveredUpdates_ExplainEmergencyTransitionsAndPlannedScheduleDelta()
    {
        const long chatId = -1004471996729;
        Assert.Equal(chatId.ToString(), Environment.GetEnvironmentVariable("TELEGRAM_TEST_CHAT_ID"));
        var token = Environment.GetEnvironmentVariable("Bot__Token");
        Assert.False(string.IsNullOrWhiteSpace(token));
        using var transport = new TestChannelTransport(chatId);
        using var telegramHttp = new HttpClient(transport);
        var bot = new TelegramBotClient(token!, telegramHttp);
        Assert.Equal("emergencyCheckBot", (await bot.GetMe()).Username, ignoreCase: true);
        using var api = new ApiFactory(bot);
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        api.Clock.Now = Kyiv(15, 12);
        api.WithDb(db => db.Channels.Add(new Channel
        {
            ChatId = chatId, RegionId = "kyiv", GroupId = "45.1", PartIdList = ["darnytskyi"],
            TemplateMap = MessageSlots.All.ToDictionary(s => s.Id,
                s => s.Id == MessageSlots.ScheduleLine ? s.DefaultTemplate
                    : "🧪 ТЕСТ — не реальна подія\n" + s.DefaultTemplate
                        + (s.Id == MessageSlots.ScheduleHeading ? "\n{changes}" : "")),
        }));
        try
        {
            await Ingest(DispatcherFixture.EmergencyText);
            Assert.Contains("екстрені", transport.LastText, StringComparison.OrdinalIgnoreCase);
            await Ingest("");
            Assert.Contains("запис про екстрені відключення більше не відображається", transport.LastText);
            Assert.Contains("не підтверджує, що електропостачання відновлено", transport.LastText);
            Assert.DoesNotContain("діють стабілізаційні графіки", transport.LastText);
            await Ingest(DispatcherFixture.EmergencyText);
            await Ingest(DispatcherFixture.StabilizationText);
            Assert.Contains("екстрені відключення завершено", transport.LastText);
            Assert.Contains("діють стабілізаційні графіки", transport.LastText);
            Assert.Contains("не означає, що електроенергія вже є", transport.LastText);
            output.WriteLine("PASS live Telegram emergency disappearance vs affirmative stabilization; neither claims actual restored power.");

            api.WithDb(db =>
            {
                var channel = db.Channels.Single();
                channel.SchedulePost = true;
                channel.SchedulePostMode = PostMode.Edit;
                channel.SchedulePostImage = ScheduleImage.None;
                // The default heading has no change summary; opt in to check the planned delta.
                channel.TemplateMap = new Dictionary<string, string>
                {
                    [MessageSlots.ScheduleHeading] = "❗️ {header}\n📅 {weekday} {date}{#changes}\n\n{changes}{/changes}",
                };
            });
            var original = RealSnapshot(api.Clock.Now);
            api.Schedules.Snapshots["kyiv"] = original;
            Assert.Equal(1, (await Tick()).Sent);
            var messageId = transport.LastMessageId;
            api.Schedules.Snapshots["kyiv"] = original with
            {
                Version = "e2e-planned-delta",
                Groups = new Dictionary<string, GroupSchedule>(original.Groups)
                {
                    ["45.1"] = original.Groups["45.1"] with
                    {
                        Outages = original.Groups["45.1"].Outages.Select(o =>
                            o.Start == Kyiv(15, 11) ? o with { End = Kyiv(15, 14, 30) } : o).ToArray(),
                    },
                },
            };
            api.Clock.Now = api.Clock.Now.AddMinutes(5);
            var changed = await Tick();
            Assert.Equal(0, changed.Failed);
            Assert.Equal(1, changed.Edited);
            Assert.Equal(messageId, transport.LastMessageId);
            Assert.Contains("Зміни планового графіка", transport.LastText);
            Assert.Contains("додано планове відключення 14:00–14:30", transport.LastText);
            var before = transport.Writes;
            await Tick();
            Assert.Equal(before, transport.Writes);
            output.WriteLine("PASS live Telegram schedule edit contains concrete planned delta 14:00–14:30; unchanged repeat suppressed.");
        }
        finally
        {
            foreach (var id in transport.LiveIds.ToArray()) await bot.DeleteMessage(chatId, id);
            Assert.Empty(transport.LiveIds);
            output.WriteLine($"CLEANUP verified: {transport.Deletes} test-created Telegram messages deleted; zero tracked messages remain.");
        }

        async Task Ingest(string text)
        {
            api.Clock.Now = api.Clock.Now.AddSeconds(1);
            var response = await client.PostAsJsonAsync("/ingest",
                new IngestRequest("kyiv", text, api.Clock.Now), ApiJson.Options);
            response.EnsureSuccessStatusCode();
        }
        async Task<ScheduleTickSummary> Tick()
        {
            var response = await client.PostAsync("/ingest/schedules/tick", null);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<ScheduleTickSummary>(ApiJson.Options))!;
        }
    }

    [LiveTelegramFact]
    public async Task ReceivedEvents_RespectGeographyClosureSettingsAndCronScheduleChanges()
    {
        const long chatId = -1004471996729;
        Assert.Equal(chatId.ToString(), Environment.GetEnvironmentVariable("TELEGRAM_TEST_CHAT_ID"));
        var token = Environment.GetEnvironmentVariable("Bot__Token");
        Assert.False(string.IsNullOrWhiteSpace(token));
        using var transport = new TestChannelTransport(chatId);
        using var http = new HttpClient(transport);
        var bot = new TelegramBotClient(token!, http);
        using var api = new ApiFactory(bot);
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        var me = await bot.GetMe();
        using var settingsClient = api.ClientFor(me.Id);
        api.WithDb(db => db.ChannelAdmins.Add(new ChannelAdmin { ChatId = chatId, UserId = me.Id }));
        try
        {
            foreach (var regionId in new[] { "kyiv", "kyiv-oblast", "dnipropetrovsk" })
            foreach (var style in Enum.GetValues<EndStyle>())
            {
                var part = regionId == "kyiv" ? "darnytskyi" : regionId == "kyiv-oblast" ? "boryspil" : "nikopol";
                var otherRegion = regionId == "kyiv" ? "kyiv-oblast" : "kyiv";
                api.WithDb(db =>
                {
                    var channel = db.Channels.SingleOrDefault(c => c.ChatId == chatId);
                    if (channel is null) { channel = new Channel { ChatId = chatId }; db.Channels.Add(channel); }
                    channel.RegionId = regionId;
                    channel.PartIdList = [part];
                    channel.GroupId = "45.1";
                    channel.QuietHoursEnabled = true;
                    channel.QuietHoursStartMinute = 0;
                    channel.QuietHoursEndMinute = 1439;
                    channel.TemplateMap = MessageSlots.All.ToDictionary(s => s.Id,
                        s => s.Id is MessageSlots.ScheduleLine ? s.DefaultTemplate
                            : "🧪 ТЕСТ — не реальна подія\n" + s.DefaultTemplate);
                });
                var chats = await settingsClient.GetFromJsonAsync<List<ChatSummaryDto>>("/api/chats", ApiJson.Options);
                var savedSettings = Assert.Single(chats!, c => c.ChatId == chatId).Settings;
                var save = await settingsClient.PutAsJsonAsync($"/api/chats/{chatId}", savedSettings with
                {
                    SlotSettings = new Dictionary<string, SlotSettingDto> { [MessageSlots.Ended] = new(true, true, style) },
                }, ApiJson.Options);
                save.EnsureSuccessStatusCode();
                var before = transport.Writes;
                await Ingest(otherRegion, DispatcherFixture.EmergencyText);
                Assert.Equal(before, transport.Writes);
                await Ingest(otherRegion, DispatcherFixture.StabilizationText);
                Assert.Equal(before, transport.Writes);
                await Ingest(regionId, DispatcherFixture.EmergencyText);
                Assert.Equal(before + 1, transport.Writes);
                var originalId = transport.LastMessageId;
                before = transport.Writes;
                await Ingest(regionId, DispatcherFixture.EmergencyText);
                Assert.Equal(before, transport.Writes);
                await Ingest(regionId, DispatcherFixture.StabilizationText);
                AssertClosure(style, originalId);

                before = transport.Writes;
                var otherGroupRegion = regionId == "kyiv" ? "dnipropetrovsk" : "kyiv";
                await Groups(otherGroupRegion, "45.1", GroupStatuses.Emergency);
                Assert.Equal(before, transport.Writes);
                await Groups(otherGroupRegion, "45.1", "");
                if (regionId == "kyiv")
                {
                    await Groups(regionId, "2.1", GroupStatuses.Emergency);
                    Assert.Equal(before, transport.Writes);
                    await Groups(regionId, "45.1", GroupStatuses.Emergency);
                    Assert.Equal(before + 1, transport.Writes);
                    originalId = transport.LastMessageId;
                    before = transport.Writes;
                    await Groups(regionId, "45.1", GroupStatuses.Emergency);
                    Assert.Equal(before, transport.Writes);
                    await Groups(regionId, "45.1", "");
                    AssertClosure(style, originalId);
                }
                else
                {
                    var unsupported = await client.PostAsJsonAsync("/ingest/groups",
                        new GroupsIngestRequest(regionId, new() { ["45.1"] = new(GroupStatuses.Emergency, "") }), ApiJson.Options);
                    Assert.Equal(System.Net.HttpStatusCode.BadRequest, unsupported.StatusCode);
                    Assert.Equal(before, transport.Writes);
                    output.WriteLine($"UNAVAILABLE: {regionId} has no configured local group-status source; ingest is explicitly rejected.");
                }
                before = transport.Writes;
                await Grid(otherRegion, GridStatuses.EmergencyOutages);
                Assert.Equal(before, transport.Writes);
                await Grid(otherRegion, GridStatuses.Normal);
                await Grid(regionId, GridStatuses.EmergencyOutages);
                Assert.Equal(before + 1, transport.Writes);
                originalId = transport.LastMessageId;
                before = transport.Writes;
                await Grid(regionId, GridStatuses.EmergencyOutages);
                await Ingest(regionId, DispatcherFixture.StabilizationText);
                Assert.Equal(before, transport.Writes);
                await Grid(regionId, GridStatuses.Normal);
                AssertClosure(style, originalId);
                output.WriteLine($"PASS geographic isolation, emergency/stabilization and grid HTTP ingest, saved clearance mode; {regionId}, ended={style}; local source available={regionId == "kyiv"}.");
                await Cleanup();
            }

            foreach (var mode in Enum.GetValues<PostMode>())
            {
                api.WithDb(db =>
                {
                    db.SchedulePostThreads.RemoveRange(db.SchedulePostThreads);
                    var channel = db.Channels.Single(c => c.ChatId == chatId);
                    channel.RegionId = "kyiv";
                    channel.GroupId = "45.1";
                    channel.SchedulePost = true;
                    channel.SchedulePostMode = mode;
                    channel.SchedulePostImage = ScheduleImage.None;
                });
                api.Schedules.Snapshots["kyiv"] = RealSnapshot();
                var first = await Tick();
                Assert.Equal(1, first.Sent);
                var originalId = transport.LastMessageId;
                var before = transport.Writes;
                Assert.Equal(0, (await Tick()).Sent);
                Assert.Equal(before, transport.Writes);
                var snapshot = RealSnapshot();
                var group = snapshot.Groups["45.1"];
                api.Schedules.Snapshots["kyiv"] = snapshot with
                {
                    Version = "synthetic-cron-change",
                    Groups = new Dictionary<string, GroupSchedule>(snapshot.Groups)
                    {
                        ["45.1"] = group with
                        {
                            Outages = group.Outages.Select((o, i) => i == 0 ? o with { End = o.End.AddMinutes(30) } : o).ToArray(),
                        },
                    },
                };
                api.Clock.Now = api.Clock.Now.AddMinutes(5);
                var deletes = transport.Deletes;
                var changed = await Tick();
                Assert.Equal(0, changed.Failed);
                Assert.Equal(1, changed.Sent + changed.Edited);
                Assert.Contains("14:30", transport.LastText);
                if (mode == PostMode.Edit) Assert.Equal(originalId, transport.LastMessageId);
                else Assert.NotEqual(originalId, transport.LastMessageId);
                Assert.Equal(deletes + (mode == PostMode.ReplaceDelete ? 1 : 0), transport.Deletes);
                before = transport.Writes;
                await Tick();
                Assert.Equal(before, transport.Writes);
                output.WriteLine($"PASS actual cron HTTP endpoint → new/changed schedule → Telegram; mode={mode}; repeat suppressed.");
                await Cleanup();
            }
        }
        finally { await Cleanup(); }

        async Task Ingest(string regionId, string text)
        {
            api.Clock.Now = api.Clock.Now.AddSeconds(1);
            var response = await client.PostAsJsonAsync("/ingest",
                new IngestRequest(regionId, text, api.Clock.Now), ApiJson.Options);
            response.EnsureSuccessStatusCode();
        }
        async Task<ScheduleTickSummary> Tick()
        {
            var response = await client.PostAsync("/ingest/schedules/tick", null);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<ScheduleTickSummary>(ApiJson.Options))!;
        }
        async Task Groups(string regionId, string groupId, string status)
        {
            var response = await client.PostAsJsonAsync("/ingest/groups",
                new GroupsIngestRequest(regionId, new() { [groupId] = new(status, "") }), ApiJson.Options);
            if (regionId != "kyiv")
            {
                Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
                return;
            }
            response.EnsureSuccessStatusCode();
        }
        async Task Grid(string regionId, string status)
        {
            api.Grid.Feed = new GridFeed(api.Clock.Now,
                new Dictionary<string, GridRegionStatus> { [regionId] = new(regionId, status, "ТЕСТ Укренерго", api.Clock.Now) });
            var response = await client.PostAsync("/ingest/grid/tick", null);
            response.EnsureSuccessStatusCode();
        }
        void AssertClosure(EndStyle style, int originalId)
        {
            Assert.Equal(style == EndStyle.Edit ? "editMessageText" : "sendMessage", transport.LastWriteMethod);
            if (style == EndStyle.Edit) Assert.Equal(originalId, transport.LastMessageId);
            else Assert.NotEqual(originalId, transport.LastMessageId);
            Assert.Equal(style == EndStyle.Reply ? originalId : (int?)null, transport.LastReplyId);
        }
        async Task Cleanup()
        {
            foreach (var id in transport.LiveIds.ToArray()) await bot.DeleteMessage(chatId, id);
            Assert.Empty(transport.LiveIds);
        }
    }

    [LiveTelegramFact]
    public async Task NotificationFlows_SendEditReplaceDeduplicateAndCleanUpInTestChannel()
    {
        const long chatId = -1004471996729;
        var token = Environment.GetEnvironmentVariable("Bot__Token");
        Assert.False(string.IsNullOrWhiteSpace(token), "Missing test bot token.");
        Assert.Equal(chatId.ToString(), Environment.GetEnvironmentVariable("TELEGRAM_TEST_CHAT_ID"));
        using var transport = new TestChannelTransport(chatId);
        using var http = new HttpClient(transport);
        var bot = new TelegramBotClient(token!, http);
        using var fixture = new DispatcherFixture();
        var channel = fixture.AddChannel(chatId, c =>
        {
            c.GroupId = "45.1";
            c.EditInPlace = true;
            c.QuietHoursEnabled = true;
            c.QuietHoursStartMinute = 0;
            c.QuietHoursEndMinute = 1439;
            c.TemplateMap = MessageSlots.All.ToDictionary(s => s.Id,
                s => s.Id == MessageSlots.ScheduleLine ? s.DefaultTemplate
                    : "🧪 <b>ТЕСТ — не реальна подія</b>\n" + s.DefaultTemplate);
        });
        var dispatcher = new AlertDispatcher(bot, fixture.Db, fixture.Clock, NullLogger<AlertDispatcher>.Instance);
        var coordinator = new RegionAlertCoordinator(fixture.Db, dispatcher, fixture.Classifier, fixture.Options,
            NullLogger<RegionAlertCoordinator>.Instance);
        var grid = new GridState { RegionId = "kyiv", Status = GridStatuses.EmergencyOutages };
        fixture.Db.GridStates.Add(grid);
        await fixture.Db.SaveChangesAsync();
        try
        {
            Assert.Equal(1, (await coordinator.RecomputeAsync("kyiv", default))!.Sent);
            Assert.True(transport.LastSendSilent);
            output.WriteLine("PASS: Ukrenergo fallback alert delivered silently.");
            var count = transport.Writes;
            await coordinator.RecomputeAsync("kyiv", default);
            Assert.Equal(count, transport.Writes);
            output.WriteLine("PASS: repeated source observation sends nothing.");

            var classification = fixture.Classifier.Classify("kyiv", DispatcherFixture.AccidentRightBankText);
            var state = new RegionState
            {
                RegionId = "kyiv",
                Kind = classification.Kind,
                Hash = classification.Hash,
                NormalizedText = classification.NormalizedText,
                PopupText = DispatcherFixture.AccidentRightBankText,
                PartIdList = classification.PartIds,
            };
            fixture.Db.RegionStates.Add(state);
            await fixture.Db.SaveChangesAsync();
            Assert.Equal(1, (await coordinator.RecomputeAsync("kyiv", default))!.Edited);
            output.WriteLine("PASS: informative DTEK district alert edits the Ukrenergo message.");
            fixture.Db.RegionStates.Remove(state);
            grid.Status = GridStatuses.Normal;
            await fixture.Db.SaveChangesAsync();
            Assert.Equal(1, (await coordinator.RecomputeAsync("kyiv", default))!.Closed);
            output.WriteLine("PASS: sources clearing closes the alert with a reply.");

            channel.PartIdList = ["darnytskyi"];
            fixture.Db.RegionStates.Add(state);
            await fixture.Db.SaveChangesAsync();
            count = transport.Writes;
            await coordinator.RecomputeAsync("kyiv", default);
            Assert.Equal(count, transport.Writes);
            grid.Status = GridStatuses.EmergencyOutages;
            await fixture.Db.SaveChangesAsync();
            Assert.Equal(1, (await coordinator.RecomputeAsync("kyiv", default))!.Sent);
            Assert.Contains("Укренерго", transport.LastText);
            count = transport.Writes;
            await coordinator.RecomputeAsync("kyiv", default);
            Assert.Equal(count, transport.Writes);
            fixture.Db.RegionStates.Remove(state);
            await fixture.Db.SaveChangesAsync();
            await coordinator.RecomputeAsync("kyiv", default);
            Assert.Equal(count, transport.Writes);
            grid.Status = GridStatuses.Normal;
            await fixture.Db.SaveChangesAsync();
            Assert.Equal(1, (await coordinator.RecomputeAsync("kyiv", default))!.Closed);
            channel.PartIdList = [];
            output.WriteLine("PASS: DTEK-first district alert followed by regional Ukrenergo reaches outside subscriber; no premature clearance.");

            channel.EditInPlace = false;
            channel.DeletePreviousAlert = true;
            await fixture.Db.SaveChangesAsync();
            await dispatcher.DispatchAsync(fixture.Kyiv,
                fixture.Classifier.Classify("kyiv", DispatcherFixture.EmergencyText), DispatcherFixture.EmergencyText, default);
            var deletes = transport.Deletes;
            var changed = DispatcherFixture.EmergencyText + " Оновлення: тест.";
            Assert.Equal(1, (await dispatcher.DispatchAsync(fixture.Kyiv,
                fixture.Classifier.Classify("kyiv", changed), changed, default)).Sent);
            Assert.Equal(deletes + 1, transport.Deletes);
            output.WriteLine("PASS: replacement emergency alert sends first, then deletes the previous post.");
            await dispatcher.DispatchAsync(fixture.Kyiv, fixture.Classifier.Classify("kyiv", null), null, default);
            Assert.Equal(1, (await dispatcher.DispatchGroupAsync(fixture.Kyiv, "45.1", true, default)).Sent);
            Assert.Equal(1, (await dispatcher.DispatchGroupAsync(fixture.Kyiv, "45.1", false, default)).Closed);
            output.WriteLine("PASS: local group emergency and clearance delivered.");

            channel.SchedulePost = true;
            channel.SchedulePostMode = PostMode.Edit;
            channel.SchedulePostImage = ScheduleImage.None;
            channel.Addresses.Add(new ChatAddress
            {
                RegionId = "kyiv",
                Name = "Тестовий дім",
                City = "Київ",
                Street = "Тестова",
                House = "1",
                Groups = [new() { GroupId = "45.1" }, new() { GroupId = "2.1" }],
            });
            await fixture.Db.SaveChangesAsync();
            var provider = new FakeScheduleProvider();
            provider.Snapshots["kyiv"] = RealSnapshot();
            var notifier = new ScheduleNotifier(fixture.Db, bot, provider, fixture.Options, fixture.Clock,
                NullLogger<ScheduleNotifier>.Instance);
            Assert.True((await notifier.SendScheduleNowAsync(channel, default)).Sent);
            Assert.Equal(1, transport.LastText.Split("Графік відключень на сьогодні").Length - 1);
            Assert.Equal(1, transport.LastText.Split("Графік відключень на завтра").Length - 1);
            Assert.Contains("Черга 2.1", transport.LastText);
            Assert.Contains("Черга 45.1", transport.LastText);
            output.WriteLine("PASS: multi-group Today/Tomorrow post accepted by Telegram.");
            channel.TemplateMap = channel.TemplateMap.ToDictionary(p => p.Key, p =>
                p.Key == MessageSlots.ScheduleLine ? "⚡ {start}  →  {end}" : p.Value);
            Assert.True((await notifier.SendScheduleNowAsync(channel, default)).Sent);
            Assert.Contains("⚡ 11:00  →  14:00", transport.LastText);
            output.WriteLine("PASS: interval customization edits the schedule.");
            count = transport.Writes;
            await notifier.TickAsync(default);
            Assert.Equal(count, transport.Writes);
            output.WriteLine("PASS: unchanged schedule is not reposted.");
            channel.SchedulePostMode = PostMode.ReplaceDelete;
            deletes = transport.Deletes;
            Assert.True((await notifier.SendScheduleNowAsync(channel, default)).Sent);
            Assert.True(transport.Deletes > deletes);
            output.WriteLine("PASS: schedule replace/delete mode.");
            channel.SchedulePost = false;
            channel.ScheduleReminder = true;
            channel.ScheduleReminderMinutes = 15;
            fixture.Clock.Now = Kyiv(15, 10, 45);
            await fixture.Db.SaveChangesAsync();
            var reminder = await notifier.TickAsync(default);
            Assert.Equal(0, reminder.Failed);
            Assert.True(reminder.Sent > 0);
            Assert.True(transport.LastSendSilent);
            count = transport.Writes;
            await notifier.TickAsync(default);
            Assert.Equal(count, transport.Writes);
            output.WriteLine("PASS: reminder lead time, quiet hours and reminder deduplication.");

            channel.IsPaused = true;
            await fixture.Db.SaveChangesAsync();
            count = transport.Writes;
            await notifier.TickAsync(default);
            await dispatcher.DispatchGroupAsync(fixture.Kyiv, "45.1", true, default);
            Assert.Equal(count, transport.Writes);
            output.WriteLine("PASS: paused channel receives no automatic notifications.");

            var photo = await notifier.SendNewAsync(chatId, "🧪 <b>Тест картинки</b>", "https://invalid.example.test/image.png", default, true);
            Assert.False(photo.IsPhoto);
            output.WriteLine("PASS: rejected schedule picture falls back to text.");

        }
        finally
        {
            foreach (var id in transport.LiveIds.ToArray())
                await bot.DeleteMessage(chatId, id);
            Assert.Empty(transport.LiveIds);
            output.WriteLine("PASS: all test messages cleaned up; production database and webhook untouched.");
        }

    }

    [LiveTelegramFact]
    public async Task MiniAppApi_ReadSavePreviewSendAndUnlinkInTestChannel()
    {
        const long chatId = -1004471996729;
        Assert.Equal(chatId.ToString(), Environment.GetEnvironmentVariable("TELEGRAM_TEST_CHAT_ID"));
        var token = Environment.GetEnvironmentVariable("Bot__Token");
        Assert.False(string.IsNullOrWhiteSpace(token));
        using var transport = new TestChannelTransport(chatId);
        using var http = new HttpClient(transport);
        var bot = new TelegramBotClient(token!, http);
        try
        {
            await VerifyMiniAppApiAsync(bot, transport, chatId);
        }
        finally
        {
            foreach (var id in transport.LiveIds.ToArray()) await bot.DeleteMessage(chatId, id);
            Assert.Empty(transport.LiveIds);
            output.WriteLine("PASS: API test messages cleaned up.");
        }
    }

    private async Task VerifyMiniAppApiAsync(ITelegramBotClient bot, TestChannelTransport transport, long chatId)
    {
        var me = await bot.GetMe();
        using var api = new ApiFactory(bot);
        using var client = api.ClientFor(me.Id);
        api.Schedules.Snapshots["kyiv"] = RealSnapshot();
        api.WithDb(db =>
        {
            db.Channels.Add(new Channel
            {
                ChatId = chatId,
                RegionId = "kyiv",
                GroupId = "45.1",
                TemplateMap = MessageSlots.All.ToDictionary(s => s.Id, s =>
                    s.Id == MessageSlots.ScheduleLine ? s.DefaultTemplate : "🧪 <b>Тест API</b>\n" + s.DefaultTemplate),
            });
            db.ChannelAdmins.Add(new ChannelAdmin { ChatId = chatId, UserId = me.Id });
        });
        (await client.GetAsync("/api/bootstrap")).EnsureSuccessStatusCode();
        var chats = await client.GetFromJsonAsync<List<ChatSummaryDto>>("/api/chats", ApiJson.Options);
        var settings = Assert.Single(chats!, c => c.ChatId == chatId).Settings;
        (await client.PutAsJsonAsync($"/api/chats/{chatId}", settings with
        {
            QuietHoursEnabled = true,
            QuietHoursStartMinute = 0,
            QuietHoursEndMinute = 1439,
            DeletePreviousAlert = true,
        }, ApiJson.Options)).EnsureSuccessStatusCode();
        output.WriteLine("PASS: Mini App authentication, real Telegram admin check, settings read/save.");
        var addresses = new[]
        {
                new ChatAddressDto(0, "kyiv", "Тест дім", "Київ", "Тестова", "1", [new("45.1"), new("2.1")]),
                new ChatAddressDto(0, "kyiv", "Тест офіс", "Київ", "Тестова", "2", [new("45.1")]),
            };
        (await client.PutAsJsonAsync($"/api/chats/{chatId}/addresses",
            new SaveAddressesRequest(addresses), ApiJson.Options)).EnsureSuccessStatusCode();
        var previewResponse = await client.PostAsJsonAsync("/api/preview",
            new PreviewRequest(null, AlertKind.Emergency, "kyiv", null, Slot: MessageSlots.Schedule, Addresses: addresses),
            ApiJson.Options);
        previewResponse.EnsureSuccessStatusCode();
        var preview = await previewResponse.Content.ReadFromJsonAsync<PreviewResponse>(ApiJson.Options);
        Assert.Empty(preview!.Errors);
        Assert.Contains("Тест дім", preview.Html);
        Assert.Contains("Тест офіс", preview.Html);
        foreach (var slot in new[] { MessageSlots.Emergency, MessageSlots.Regional, MessageSlots.Local, MessageSlots.Ended, MessageSlots.Schedule })
        {
            (await client.PostAsJsonAsync($"/api/chats/{chatId}/templates/{slot}/send",
                new SendTestRequest(null), ApiJson.Options)).EnsureSuccessStatusCode();
            api.Clock.Now = api.Clock.Now.AddSeconds(11);
        }
        output.WriteLine("PASS: multi-address save/preview and all emergency/local/schedule template test-send API routes.");
        (await client.GetAsync($"/api/chats/{chatId}/trigger-check?pattern=Світло%20зникло")).EnsureSuccessStatusCode();
        (await client.GetAsync($"/api/chats/{chatId}/status")).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync($"/api/chats/{chatId}/schedules/send", new { }, ApiJson.Options)).EnsureSuccessStatusCode();
        Assert.Contains("Графік відключень на завтра", transport.LastText);
        output.WriteLine("PASS: channel status, custom trigger validation and send-schedule-now API.");
        (await client.DeleteAsync($"/api/chats/{chatId}")).EnsureSuccessStatusCode();
        api.WithDb(db => Assert.Empty(db.ChannelAdmins.Where(a => a.ChatId == chatId)));
        output.WriteLine("PASS: unlink/reset affects only the isolated test database.");
    }

    [LiveTelegramFact]
    public async Task PublicSources_ParseLiveFeedsAndDeliverScheduleImage()
    {
        const long chatId = -1004471996729;
        Assert.Equal(chatId.ToString(), Environment.GetEnvironmentVariable("TELEGRAM_TEST_CHAT_ID"));
        var token = Environment.GetEnvironmentVariable("Bot__Token");
        Assert.False(string.IsNullOrWhiteSpace(token));
        using var fixture = new DispatcherFixture();
        using var sources = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var factory = new SourceClients(sources);
        var schedules = new ScheduleProvider(factory, fixture.Options, TimeProvider.System, NullLogger<ScheduleProvider>.Instance);
        var grid = new GridStatusProvider(factory, fixture.Options, TimeProvider.System, NullLogger<GridStatusProvider>.Instance);
        var scheduleTasks = fixture.Settings.Regions.Where(r => r.HasSchedules).ToDictionary(
            r => r.Id, r => schedules.RefreshAsync(r.Id, default));
        var gridTask = grid.RefreshAsync(default);
        await Task.WhenAll(scheduleTasks.Values.Cast<Task>().Append(gridTask));
        foreach (var (regionId, task) in scheduleTasks)
        {
            var regional = await task;
            if (regional is null)
            {
                var region = fixture.Settings.Regions.Single(r => r.Id == regionId);
                using var raw = JsonDocument.Parse(await sources.GetStringAsync(
                    $"{fixture.Settings.Schedules.BaseUrl.TrimEnd('/')}/data/{region.OutageDataFile}.json"));
                var data = raw.RootElement.GetProperty("fact").GetProperty("data");
                Assert.Equal(JsonValueKind.Array, data.ValueKind);
                Assert.Equal(0, data.GetArrayLength());
                output.WriteLine($"UNAVAILABLE: Baskerville {regionId} publishes fact.data=[]; no current schedule to deliver. Provider correctly returns no snapshot.");
                continue;
            }
            Assert.NotEmpty(regional.GroupIds);
            output.WriteLine($"PASS: live Baskerville {regionId}; {regional.GroupIds.Count()} groups, coverage {regional.CoverageStart:u}–{regional.CoverageEnd:u}.");
        }
        var snapshot = (await scheduleTasks["kyiv"])!;
        var feed = await gridTask;
        Assert.NotNull(snapshot);
        Assert.NotEmpty(snapshot.GroupIds);
        Assert.NotNull(feed);
        Assert.NotEmpty(feed.Regions);
        foreach (var region in fixture.Settings.Regions)
        {
            Assert.True(feed.Regions.TryGetValue(region.GridStatusSlug, out var status), $"Missing current grid region {region.Id}");
            output.WriteLine($"PASS: live Ukrenergo {region.Id}: {status!.Status}.");
        }
        output.WriteLine($"PASS: live Baskerville feed parsed; {snapshot.GroupIds.Count()} groups, coverage {snapshot.CoverageStart:u}–{snapshot.CoverageEnd:u}.");
        output.WriteLine($"PASS: live Ukrenergo feed parsed; {feed.Regions.Count} regions, generated {feed.GeneratedAt:u}.");
        using var transport = new TestChannelTransport(chatId);
        using var telegramHttp = new HttpClient(transport);
        var bot = new TelegramBotClient(token!, telegramHttp);
        var notifier = new ScheduleNotifier(fixture.Db, bot, schedules, fixture.Options, TimeProvider.System,
            NullLogger<ScheduleNotifier>.Instance);
        try
        {
            var group = snapshot.GroupIds.First();
            var image = ScheduleText.ImageUrl(fixture.Settings.Schedules, snapshot.DataFile, group, snapshot.Version);
            var result = await notifier.SendNewAsync(chatId,
                "🧪 <b>Тест отримання картинки з джерела</b>\nНе повідомлення про подію за вашою адресою.",
                image, default, true);
            Assert.True(result.IsPhoto, "Telegram did not accept the actual source schedule image.");
            output.WriteLine("PASS: Telegram accepted actual Baskerville schedule photo.");
        }
        finally
        {
            foreach (var id in transport.LiveIds.ToArray()) await bot.DeleteMessage(chatId, id);
            Assert.Empty(transport.LiveIds);
            output.WriteLine("PASS: source image test cleaned up.");
        }
    }

    private sealed class SourceClients(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    [LiveTelegramFact]
    public async Task EveryDistrict_DeterministicAndRealLlmInputs_IngestToLiveTelegram()
    {
        const long chatId = -1004471996729;
        Assert.Equal(chatId.ToString(), Environment.GetEnvironmentVariable("TELEGRAM_TEST_CHAT_ID"));
        var token = Environment.GetEnvironmentVariable("Bot__Token");
        Assert.False(string.IsNullOrWhiteSpace(token));
        var inputPath = Environment.GetEnvironmentVariable("DTEK_ROUTING_INPUTS");
        Assert.False(string.IsNullOrWhiteSpace(inputPath), "Run live-routing-inputs.js first.");
        using var inputs = JsonDocument.Parse(await File.ReadAllTextAsync(inputPath!));
        Assert.Empty(inputs.RootElement.GetProperty("failures").EnumerateArray());
        using var transport = new TestChannelTransport(chatId);
        using var http = new HttpClient(transport);
        var bot = new TelegramBotClient(token!, http);
        using var api = new ApiFactory(bot);
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        using var fixture = new DispatcherFixture();
        var cases = inputs.RootElement.GetProperty("cases").EnumerateArray().Where(c => !c.GetProperty("live").GetBoolean()).ToArray();
        Assert.Equal(fixture.Settings.Regions.Sum(r => r.Parts.Count), cases.Length);
        try
        {
            foreach (var item in cases)
            {
                var regionId = item.GetProperty("regionId").GetString()!;
                var partId = item.GetProperty("partId").GetString()!;
                var text = item.GetProperty("popupText").GetString()!;
                var structured = item.GetProperty("structured").Deserialize<StructuredPopup>(ApiJson.Options)!;
                var region = fixture.Settings.Regions.Single(r => r.Id == regionId);
                var part = region.Parts.Single(p => p.Id == partId);
                Assert.Contains(partId, structured.Districts!);
                api.WithDb(db =>
                {
                    var channel = db.Channels.SingleOrDefault(c => c.ChatId == chatId);
                    if (channel is null) { channel = new Channel { ChatId = chatId }; db.Channels.Add(channel); }
                    channel.RegionId = regionId;
                    channel.PartIdList = [partId];
                    channel.IsActive = true;
                    channel.EditInPlace = true;
                    channel.QuietHoursEnabled = true;
                    channel.QuietHoursStartMinute = 0;
                    channel.QuietHoursEndMinute = 1439;
                    channel.TemplateMap = new Dictionary<string, string>
                    {
                        [MessageSlots.Emergency] = "🧪 ТЕСТ {region} · {part}\n{message}\n{popup}",
                        [MessageSlots.Regional] = "🧪 ТЕСТ {region} · {part}\n{message}\n{popup}",
                        [MessageSlots.Ended] = "🧪 ТЕСТ ЗНЯТТЯ {region} · {part}\n{message}",
                    };
                });
                var before = transport.Writes;
                await Ingest(text + "\nДетермінована перевірка.", null);
                Assert.True(transport.Writes > before, $"No deterministic delivery for {regionId}/{partId}");
                Assert.Contains(part.Name, transport.LastText);
                Assert.Contains(region.Name, transport.LastText);
                before = transport.Writes;
                await Ingest(text, structured);
                Assert.True(transport.Writes > before, $"No LLM-backed update for {regionId}/{partId}");
                Assert.Contains(part.Name, transport.LastText);
                before = transport.Writes;
                await Ingest(text, structured);
                Assert.Equal(before, transport.Writes);
                api.WithDb(db =>
                {
                    var saved = db.RegionStates.Single(s => s.RegionId == regionId);
                    Assert.Contains(partId, saved.PartIdList!);
                    Assert.Single(db.Deliveries, d => d.ChatId == chatId && d.RegionId == regionId && !d.IsClosed);
                });
                await Ingest(DispatcherFixture.StabilizationText, null);
                Assert.True(transport.Writes > before);
                Assert.Contains("ЗНЯТТЯ", transport.LastText);
                foreach (var id in transport.LiveIds.ToArray()) await bot.DeleteMessage(chatId, id);
                output.WriteLine($"PASS ingest → subscription/template → Telegram → stabilization clearance: {regionId}/{partId}; deterministic + actual LLM answer.");

                async Task Ingest(string popup, StructuredPopup? scope)
                {
                    api.Clock.Now = api.Clock.Now.AddSeconds(1);
                    var response = await client.PostAsJsonAsync("/ingest",
                        new IngestRequest(regionId, popup, api.Clock.Now, scope), ApiJson.Options);
                    response.EnsureSuccessStatusCode();
                }
            }
            foreach (var source in inputs.RootElement.GetProperty("sources").EnumerateArray())
            {
                Assert.True(source.GetProperty("ok").GetBoolean());
                output.WriteLine($"LIVE WEBSITE {source.GetProperty("regionId")}: verified={source.GetProperty("ok")}");
            }
            foreach (var item in inputs.RootElement.GetProperty("cases").EnumerateArray().Where(c => c.GetProperty("live").GetBoolean()))
            {
                var regionId = item.GetProperty("regionId").GetString()!;
                api.WithDb(db =>
                {
                    var channel = db.Channels.Single(c => c.ChatId == chatId);
                    channel.RegionId = regionId;
                    channel.PartIdList = [];
                });
                var before = transport.Writes;
                api.Clock.Now = api.Clock.Now.AddSeconds(1);
                var popup = item.GetProperty("popupText").GetString()!;
                var scope = item.GetProperty("structured").Deserialize<StructuredPopup>(ApiJson.Options);
                var expected = fixture.Classifier.Classify(regionId, popup, scope);
                var response = await client.PostAsJsonAsync("/ingest",
                    new IngestRequest(regionId, popup, api.Clock.Now, scope), ApiJson.Options);
                response.EnsureSuccessStatusCode();
                if (expected.IsAlert) Assert.True(transport.Writes > before);
                else Assert.Equal(before, transport.Writes);
                api.WithDb(db => Assert.Equal(expected.Kind, db.RegionStates.Single(s => s.RegionId == regionId).Kind));
                output.WriteLine($"PASS current real website + real LLM scope → ingest → {expected.Kind}: {regionId}");
            }
        }
        finally
        {
            foreach (var id in transport.LiveIds.ToArray()) await bot.DeleteMessage(chatId, id);
            Assert.Empty(transport.LiveIds);
        }
    }

    private sealed class TestChannelTransport(long chatId) : DelegatingHandler(new HttpClientHandler())
    {
        public HashSet<int> LiveIds { get; } = [];
        public int Writes { get; private set; }
        public int Deletes { get; private set; }
        public bool LastSendSilent { get; private set; }
        public string LastText { get; private set; } = "";
        public string LastWriteMethod { get; private set; } = "";
        public int LastMessageId { get; private set; }
        public int? LastReplyId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var method = request.RequestUri!.Segments.Last();
            var payload = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(ct);
            using var data = JsonDocument.Parse(payload);
            if (method != "getMe")
                Assert.Equal(chatId.ToString(), data.RootElement.GetProperty("chat_id").ToString());
            Assert.Contains(method, new[] { "sendMessage", "editMessageText", "deleteMessage", "sendPhoto", "getMe", "getChatMember" });
            await Task.Delay(TimeSpan.FromSeconds(3.2), ct);
            HttpResponseMessage response;
            try { response = await base.SendAsync(request, ct); }
            catch (HttpRequestException) { throw new InvalidOperationException("Telegram test transport failed; URL withheld."); }
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            using var result = JsonDocument.Parse(bytes);
            if (result.RootElement.GetProperty("ok").GetBoolean())
            {
                var value = result.RootElement.GetProperty("result");
                if (method == "deleteMessage")
                {
                    Deletes++;
                    LiveIds.Remove(data.RootElement.GetProperty("message_id").GetInt32());
                }
                else if (method is "sendMessage" or "editMessageText" or "sendPhoto")
                {
                    LiveIds.Add(value.GetProperty("message_id").GetInt32());
                    Writes++;
                    LastWriteMethod = method;
                    LastMessageId = value.GetProperty("message_id").GetInt32();
                    LastReplyId = data.RootElement.TryGetProperty("reply_parameters", out var reply)
                        ? reply.GetProperty("message_id").GetInt32() : null;
                    LastText = value.TryGetProperty("text", out var text) ? text.GetString()! : "";
                    if (method == "sendMessage")
                        LastSendSilent = data.RootElement.TryGetProperty("disable_notification", out var silent) && silent.GetBoolean();
                }
            }
            response.Content = new ByteArrayContent(bytes);
            response.Content.Headers.ContentType = new("application/json");
            return response;
        }
    }
}
