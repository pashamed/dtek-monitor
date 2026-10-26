using System.Net;
using System.Net.Http.Json;
using DtekBot.Api.Data;
using DtekBot.Api.Schedules;
using DtekBot.Api.Svitlobot;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class SvitlobotApiTests : IClassFixture<ApiFactory>
{
    private const string Key = "ab&c=d/e+f";
    private const string EncodedKey = "ab%26c%3Dd%2Fe%2Bf";
    private const string Week = "000000001100000000000000;000000000000000000000000;000000000000000000000000;000000000000000000000000;000000000000000000000000;000000000000000000000000;000000000000000000000023";
    private const string WeekMaybe = "000000001100000000000000;100000000000000000000002;000000000000000000000000;000000000000000000000000;000000000000000000000000;000000000000000000000000;000000000000000000000023";
    private static readonly string Quiet = string.Join(';', Enumerable.Repeat(new string('0', 24), 7));

    private readonly ApiFactory _factory;

    public SvitlobotApiTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Svitlobot.Reset();
        _factory.SvitlobotEncryptionKey = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
        // A week without published daily schedules, so these tests see the weekly preset alone.
        _factory.Clock.Now = Kyiv(22, 12);
        _factory.Schedules.Snapshots.Clear();
        _factory.Schedules.Snapshots["kyiv"] = RealSnapshot() with
        {
            WeeklyPresets = new Dictionary<string, string> { ["45.1"] = Week, ["2.1"] = Quiet, ["2.2"] = Quiet },
            WeeklyPresetsWithMaybe = new Dictionary<string, string> { ["45.1"] = WeekMaybe, ["2.1"] = Quiet, ["2.2"] = Week },
        };
    }

    private (long ChatId, HttpClient Client) NewUser(Action<Channel>? configure = null)
    {
        var userId = _factory.NextId();
        if (configure is not null)
        {
            _factory.WithDb(db =>
            {
                var channel = new Channel { ChatId = userId, RegionId = "kyiv", IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
                configure(channel);
                db.Channels.Add(channel);
            });
        }

        return (userId, _factory.ClientFor(userId));
    }

    private (long ChatId, HttpClient Client) UserWithKey(Action<Channel>? configure = null)
    {
        var protector = new SvitlobotKeyProtector(() => _factory.SvitlobotEncryptionKey);
        return NewUser(c =>
        {
            c.SvitlobotKey = protector.Protect(Key);
            configure?.Invoke(c);
        });
    }

    private string? StoredKey(long chatId) =>
        _factory.WithDb(db => db.Channels.AsNoTracking().Where(c => c.ChatId == chatId).Select(c => c.SvitlobotKey).SingleOrDefault());

    private static async Task AssertNoKeyAsync(HttpResponseMessage response, long? chatId = null, string? stored = null)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Key, body);
        Assert.DoesNotContain(EncodedKey, body);
        if (stored is not null)
        {
            Assert.DoesNotContain(stored, body);
        }
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response) => (await response.ReadAsync<ApiError>())!.Error;

    // ================= key =================

    [Fact]
    public async Task PutKey_ValidatesWithSvitlobot_StoresItEncrypted_AndReturnsTheState()
    {
        var (chatId, client) = NewUser();
        _factory.Svitlobot.Responder = _ => FakeSvitlobotHandler.Text("12;&;Графік;&&&;no data");

        var response = await client.PutJsonAsync($"/api/chats/{chatId}/svitlobot/key", new SetSvitlobotKeyRequest("  " + Key + " "));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = (await response.ReadAsync<SvitlobotStateDto>())!;
        Assert.True(state.HasKey);
        Assert.False(state.HasSchedule);
        Assert.Equal((12, "Графік"), (state.Templates.Single().Id, state.Templates.Single().Name));
        await AssertNoKeyAsync(response);

        var stored = StoredKey(chatId);
        Assert.False(string.IsNullOrEmpty(stored));
        Assert.DoesNotContain(Key, stored);
        Assert.Equal(Key, new SvitlobotKeyProtector(() => _factory.SvitlobotEncryptionKey).Unprotect(stored));
        Assert.Equal($"/website/getChannelTimetable?channel_key={EncodedKey}", Assert.Single(_factory.Svitlobot.Requests).PathAndQuery);
    }

    [Fact]
    public async Task PutKey_UnknownKey_Is400_AndNothingIsStored()
    {
        var (chatId, client) = NewUser();
        _factory.Svitlobot.Responder = _ => FakeSvitlobotHandler.Text($"Channel \"{Key}\" is not found! Insert correct key from chat bot!");

        var response = await client.PutJsonAsync($"/api/chats/{chatId}/svitlobot/key", new SetSvitlobotKeyRequest(Key));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("ключем", await ErrorAsync(response));
        await AssertNoKeyAsync(response);
        Assert.Null(StoredKey(chatId));
    }

    [Fact]
    public async Task PutKey_NetworkError_Is502()
    {
        var (chatId, client) = NewUser();
        _factory.Svitlobot.Responder = _ => throw new HttpRequestException("down");

        var response = await client.PutJsonAsync($"/api/chats/{chatId}/svitlobot/key", new SetSvitlobotKeyRequest(Key));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Null(StoredKey(chatId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("bad\nkey")]
    public async Task PutKey_BadInput_Is400_WithoutCallingSvitlobot(string? key)
    {
        var (chatId, client) = NewUser();

        var response = await client.PutJsonAsync($"/api/chats/{chatId}/svitlobot/key", new SetSvitlobotKeyRequest(key));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task PutKey_TooLong_Is400()
    {
        var (chatId, client) = NewUser();

        var response = await client.PutJsonAsync($"/api/chats/{chatId}/svitlobot/key", new SetSvitlobotKeyRequest(new string('a', 201)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task DeleteKey_ForgetsItLocally_WithoutCallingSvitlobot()
    {
        var (chatId, client) = UserWithKey();

        var response = await client.DeleteAsync($"/api/chats/{chatId}/svitlobot/key");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.ReadAsync<SvitlobotStateDto>())!.HasKey);
        Assert.Null(StoredKey(chatId));
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task Summary_ShowsOnlyAFlag_NeverTheKey()
    {
        var (chatId, client) = UserWithKey();
        var stored = StoredKey(chatId)!;

        var response = await client.GetAsync("/api/chats");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"hasSvitlobotKey\":true", body);
        Assert.DoesNotContain(Key, body);
        Assert.DoesNotContain(stored, body);
        Assert.DoesNotContain("\"svitlobotKey\"", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Summary_WithoutAKey_HasFlagFalse()
    {
        var (_, client) = NewUser(c => { });

        var chats = await (await client.GetAsync("/api/chats")).ReadAsync<List<ChatSummaryDto>>();

        Assert.False(chats!.Single(c => c.IsPrivate).Settings.HasSvitlobotKey);
    }

    [Fact]
    public void ResettingAChannel_DropsTheKey()
    {
        var (chatId, _) = UserWithKey();

        _factory.WithDb(db =>
        {
            var channel = db.Channels.Single(c => c.ChatId == chatId);
            ChannelReset.ResetAsync(db, channel, default).GetAwaiter().GetResult();
        });

        Assert.Null(StoredKey(chatId));
    }

    // ================= state =================

    [Fact]
    public async Task Get_WithoutAKey_IsEmpty_AndCallsNothing()
    {
        var (chatId, client) = NewUser();

        var response = await client.GetAsync($"/api/chats/{chatId}/svitlobot");

        var state = (await response.ReadAsync<SvitlobotStateDto>())!;
        Assert.False(state.HasKey);
        Assert.Empty(state.Templates);
        Assert.False(state.HasSchedule);
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task Get_WithAKey_FetchesLiveState_AndNeverEchoesTheKey()
    {
        var (chatId, client) = UserWithKey();
        _factory.Svitlobot.Responder = _ => FakeSvitlobotHandler.Text("1;&;Перший;&&&;2;&;Другий;&&&;[[0]]");

        var response = await client.GetAsync($"/api/chats/{chatId}/svitlobot");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = (await response.ReadAsync<SvitlobotStateDto>())!;
        Assert.True(state.HasKey);
        Assert.True(state.HasSchedule);
        Assert.Equal(["Перший", "Другий"], state.Templates.Select(t => t.Name));
        await AssertNoKeyAsync(response, stored: StoredKey(chatId));
        Assert.Equal($"/website/getChannelTimetable?channel_key={EncodedKey}", Assert.Single(_factory.Svitlobot.Requests).PathAndQuery);
    }

    [Fact]
    public async Task Get_SvitlobotUnreachable_Is502()
    {
        var (chatId, client) = UserWithKey();
        _factory.Svitlobot.Responder = _ => throw new HttpRequestException("down");

        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync($"/api/chats/{chatId}/svitlobot")).StatusCode);
    }

    [Fact]
    public async Task Get_AStoredKeyThatCannotBeDecrypted_Is400()
    {
        var (chatId, client) = NewUser(c => c.SvitlobotKey = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/chats/{chatId}/svitlobot")).StatusCode);
    }

    // ================= key check =================

    [Fact]
    public async Task KeyCheck_ShowsAMaskedValueAndAFingerprint_NeverTheKey_AndCallsNothing()
    {
        var (chatId, client) = UserWithKey();

        var response = await client.GetAsync($"/api/chats/{chatId}/svitlobot/key-check");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var check = (await response.ReadAsync<SvitlobotKeyCheckDto>())!;
        Assert.Equal("••••••••+f", check.Masked);
        Assert.Matches("^[0-9a-f]{6}$", check.Fingerprint);
        var protector = new SvitlobotKeyProtector(() => _factory.SvitlobotEncryptionKey);
        Assert.Equal(protector.Fingerprint(Key), check.Fingerprint);
        await AssertNoKeyAsync(response, stored: StoredKey(chatId));
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task KeyCheck_WithoutAKey_Is400()
    {
        var (chatId, client) = NewUser();

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"/api/chats/{chatId}/svitlobot/key-check")).StatusCode);
    }

    [Fact]
    public async Task KeyCheck_ForSomeoneElsesChat_Is403()
    {
        var (chatId, _) = UserWithKey();

        var response = await _factory.ClientFor(_factory.NextId()).GetAsync($"/api/chats/{chatId}/svitlobot/key-check");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("ABCDEFGHIJ", "••••••••IJ")]
    [InlineData("short", "••••••••")]
    public void Mask_HidesShortKeysCompletely_AndOtherwiseKeepsTwoCharacters(string key, string expected) =>
        Assert.Equal(expected, SvitlobotKeyProtector.Mask(key));

    // ================= template =================

    [Fact]
    public async Task Template_CallsSetChannelTimetable_WithTheId()
    {
        var (chatId, client) = UserWithKey();

        var response = await client.PostJsonAsync($"/api/chats/{chatId}/svitlobot/template", new SetSvitlobotTemplateRequest(12));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.ReadAsync<SvitlobotResultDto>())!.HasSchedule);
        Assert.Equal($"/website/setChannelTimetable?channel_key={EncodedKey}&timetable_id=12", Assert.Single(_factory.Svitlobot.Requests).PathAndQuery);
        await AssertNoKeyAsync(response);
    }

    [Fact]
    public async Task Template_WithoutAKey_OrWithABadId_Is400()
    {
        var (chatId, client) = NewUser();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostJsonAsync($"/api/chats/{chatId}/svitlobot/template", new SetSvitlobotTemplateRequest(12))).StatusCode);

        var (keyed, keyedClient) = UserWithKey();
        Assert.Equal(HttpStatusCode.BadRequest, (await keyedClient.PostJsonAsync($"/api/chats/{keyed}/svitlobot/template", new SetSvitlobotTemplateRequest(0))).StatusCode);
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task Template_SvitlobotRejects_Is502()
    {
        var (chatId, client) = UserWithKey();
        _factory.Svitlobot.Responder = _ => FakeSvitlobotHandler.Text("error");

        var response = await client.PostJsonAsync($"/api/chats/{chatId}/svitlobot/template", new SetSvitlobotTemplateRequest(3));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("помилку", await ErrorAsync(response));
    }

    // ================= push =================

    [Fact]
    public async Task Push_DefaultsToIncludingMaybe_ForAMissingBody_OrMissingField()
    {
        var (chatId, client) = UserWithKey(c => c.GroupId = "45.1");

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/chats/{chatId}/svitlobot/push", null)).StatusCode);
        _factory.Clock.Advance(SvitlobotRequestLimiter.Window);
        Assert.Equal(HttpStatusCode.OK, (await client.PostJsonAsync($"/api/chats/{chatId}/svitlobot/push", new { })).StatusCode);

        var sent = _factory.Svitlobot.Requests.Select(r => r.PathAndQuery).ToList();
        Assert.Equal(2, sent.Count);
        Assert.All(sent, url => Assert.Equal(
            $"/website/timetableEditEvent?channel_key={EncodedKey}&timetableData={Uri.EscapeDataString(WeekMaybe)}", url));
    }

    [Fact]
    public async Task Push_IncludeMaybeFalse_SendsOnlyFirmOutages()
    {
        var (chatId, client) = UserWithKey(c => c.GroupId = "45.1");

        var response = await client.PostJsonAsync($"/api/chats/{chatId}/svitlobot/push", new SvitlobotPushRequest(false));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            $"/website/timetableEditEvent?channel_key={EncodedKey}&timetableData={Uri.EscapeDataString(Week)}",
            Assert.Single(_factory.Svitlobot.Requests).PathAndQuery);
        await AssertNoKeyAsync(response);
    }

    [Fact]
    public async Task Push_OnlyMaybeOutages_AreRefusedWithoutThem_AndHintAtTheOption()
    {
        var (chatId, client) = UserWithKey(c => c.GroupId = "2.2");

        var response = await client.PostJsonAsync($"/api/chats/{chatId}/svitlobot/push", new SvitlobotPushRequest(false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("можливі", await ErrorAsync(response));
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task Push_MalformedBody_Is400()
    {
        var (chatId, client) = UserWithKey(c => c.GroupId = "45.1");
        using var body = new StringContent("{oops", System.Text.Encoding.UTF8, "application/json");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/chats/{chatId}/svitlobot/push", body)).StatusCode);
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task Push_SendsTheGroupsWeek_AsEncodedTimetableData()
    {
        var (chatId, client) = UserWithKey(c => c.GroupId = "45.1");

        var response = await client.PostAsync($"/api/chats/{chatId}/svitlobot/push", null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.ReadAsync<SvitlobotResultDto>())!.HasSchedule);
        Assert.Equal(
            $"/website/timetableEditEvent?channel_key={EncodedKey}&timetableData={Uri.EscapeDataString(WeekMaybe)}",
            Assert.Single(_factory.Svitlobot.Requests).PathAndQuery);
        await AssertNoKeyAsync(response);
    }

    [Fact]
    public async Task AutomaticScheduleSync_SendsCurrentWeekOnceAndResendsWhenItChanges()
    {
        _factory.WithDb(db =>
        {
            foreach (var channel in db.Channels)
            {
                channel.SvitlobotKey = null;
            }

            db.KeyValues.RemoveRange(db.KeyValues.Where(value => value.Key.StartsWith("svitlobot-schedule:")));
        });
        var (chatId, _) = UserWithKey(c => c.GroupId = "45.1");
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();

        var first = await service.TickAsync(CancellationToken.None);
        var second = await service.TickAsync(CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(0, second);
        Assert.Equal(
            $"/website/timetableEditEvent?channel_key={EncodedKey}&timetableData={Uri.EscapeDataString(WeekMaybe)}",
            Assert.Single(_factory.Svitlobot.Requests).PathAndQuery);

        _factory.Schedules.Snapshots["kyiv"] = _factory.Schedules.Snapshots["kyiv"]! with
        {
            WeeklyPresets = new Dictionary<string, string> { ["45.1"] = Week },
            WeeklyPresetsWithMaybe = new Dictionary<string, string> { ["45.1"] = Week },
        };
        _factory.Clock.Advance(SvitlobotRequestLimiter.Window);

        var changed = await service.TickAsync(CancellationToken.None);

        Assert.Equal(1, changed);
        Assert.Equal(2, _factory.Svitlobot.Requests.Count);
        Assert.Equal(chatId, _factory.WithDb(db => db.Channels.AsNoTracking()
            .Where(channel => channel.ChatId == chatId)
            .Select(channel => channel.ChatId)
            .Single()));
    }

    [Fact]
    public async Task AutomaticScheduleSync_ResendsAtTheConfiguredMondayTime_OnlyOncePerWeek()
    {
        ClearSyncState();
        UserWithKey(c => c.GroupId = "45.1");
        _factory.Clock.Now = new DateTimeOffset(2026, 10, 18, 23, 58, 0, TimeSpan.Zero); // Monday 02:58 in Kyiv.
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();

        Assert.Equal(1, await service.TickAsync(CancellationToken.None));
        Assert.Single(_factory.Svitlobot.Requests);

        _factory.Clock.Advance(TimeSpan.FromMinutes(1)); // 02:59: the old 02:05 refresh would have fired already.
        Assert.Equal(0, await service.TickAsync(CancellationToken.None));
        Assert.Single(_factory.Svitlobot.Requests);

        _factory.Clock.Advance(TimeSpan.FromMinutes(1)); // 03:00
        Assert.Equal(1, await service.TickAsync(CancellationToken.None));
        Assert.Equal(2, _factory.Svitlobot.Requests.Count);

        _factory.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await service.TickAsync(CancellationToken.None));
        Assert.Equal(2, _factory.Svitlobot.Requests.Count);
    }

    [Fact]
    public async Task AutomaticScheduleSync_RemovesItsOwnScheduleOnce_WhenTheWeekBecomesEmpty()
    {
        ClearSyncState();
        UserWithKey(c => c.GroupId = "45.1");
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();
        Assert.Equal(1, await service.TickAsync(CancellationToken.None));

        _factory.Schedules.Snapshots["kyiv"] = _factory.Schedules.Snapshots["kyiv"]! with
        {
            WeeklyPresetsWithMaybe = new Dictionary<string, string> { ["45.1"] = Quiet },
        };
        _factory.Clock.Advance(SvitlobotRequestLimiter.Window);
        Assert.Equal(1, await service.TickAsync(CancellationToken.None));
        _factory.Clock.Advance(SvitlobotRequestLimiter.Window);
        Assert.Equal(0, await service.TickAsync(CancellationToken.None));

        Assert.Equal(
            [$"/website/timetableEditEvent?channel_key={EncodedKey}&timetableData={Uri.EscapeDataString(WeekMaybe)}",
             $"/website/removeChannelTimetable?channel_key={EncodedKey}"],
            _factory.Svitlobot.Requests.Select(r => r.PathAndQuery));

        _factory.Schedules.Snapshots["kyiv"] = _factory.Schedules.Snapshots["kyiv"]! with
        {
            WeeklyPresetsWithMaybe = new Dictionary<string, string> { ["45.1"] = WeekMaybe },
        };
        Assert.Equal(1, await service.TickAsync(CancellationToken.None));
        Assert.Equal(3, _factory.Svitlobot.Requests.Count);
    }

    [Fact]
    public async Task AutomaticScheduleSync_DoesNotRepeatARejectedSchedule_UntilItChanges()
    {
        ClearSyncState();
        UserWithKey(c => c.GroupId = "45.1");
        _factory.Svitlobot.Responder = _ => FakeSvitlobotHandler.Text("invalid data");
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();

        Assert.Equal(0, await service.TickAsync(CancellationToken.None));
        _factory.Clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await service.TickAsync(CancellationToken.None));
        Assert.Single(_factory.Svitlobot.Requests);

        _factory.Svitlobot.Responder = _ => FakeSvitlobotHandler.Text("ok");
        _factory.Schedules.Snapshots["kyiv"] = _factory.Schedules.Snapshots["kyiv"]! with
        {
            WeeklyPresetsWithMaybe = new Dictionary<string, string> { ["45.1"] = Week },
        };
        Assert.Equal(1, await service.TickAsync(CancellationToken.None));
        Assert.Equal(2, _factory.Svitlobot.Requests.Count);
    }

    [Fact]
    public async Task AutomaticScheduleSync_BacksOffAfterNetworkErrors()
    {
        ClearSyncState();
        UserWithKey(c => c.GroupId = "45.1");
        _factory.Svitlobot.Responder = _ => FakeSvitlobotHandler.Text("", HttpStatusCode.BadGateway);
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();

        await service.TickAsync(CancellationToken.None); // fails; next attempt in 30 s
        _factory.Clock.Advance(TimeSpan.FromSeconds(29));
        await service.TickAsync(CancellationToken.None);
        Assert.Single(_factory.Svitlobot.Requests);

        _factory.Clock.Advance(TimeSpan.FromSeconds(31)); // 1 min after the first request
        await service.TickAsync(CancellationToken.None); // fails; next attempt in 1 min
        Assert.Equal(2, _factory.Svitlobot.Requests.Count);
        _factory.Clock.Advance(TimeSpan.FromSeconds(59));
        await service.TickAsync(CancellationToken.None);
        Assert.Equal(2, _factory.Svitlobot.Requests.Count);

        _factory.Svitlobot.Responder = _ => FakeSvitlobotHandler.Text("ok");
        _factory.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await service.TickAsync(CancellationToken.None));
        Assert.Equal(3, _factory.Svitlobot.Requests.Count);
    }

    [Fact]
    public async Task AutomaticScheduleSync_SkipsAWeekWithOnlyHalfHourOutages()
    {
        ClearSyncState();
        UserWithKey(c => c.GroupId = "45.1");
        _factory.Schedules.Snapshots["kyiv"] = _factory.Schedules.Snapshots["kyiv"]! with
        {
            WeeklyPresetsWithMaybe = new Dictionary<string, string>
            {
                ["45.1"] = string.Join(';', Enumerable.Repeat("200000000000000000000003", 7)),
            },
        };
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();

        Assert.Equal(0, await service.TickAsync(CancellationToken.None));
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    private void ClearSyncState() => _factory.WithDb(db =>
    {
        foreach (var channel in db.Channels)
        {
            channel.SvitlobotKey = null;
        }

        db.KeyValues.RemoveRange(db.KeyValues.Where(value => value.Key.StartsWith("svitlobot-")));
    });

    [Fact]
    public async Task AutomaticScheduleSync_UsesPublishedDays_AndKeepsEarlierDaysOfTheWeek()
    {
        ClearSyncState();
        UserWithKey(c => c.GroupId = "45.1");
        var preset = new Dictionary<string, string> { ["45.1"] = WeekMaybe };
        _factory.Clock.Now = Kyiv(15, 9); // Thursday, the fourth row.
        _factory.Schedules.Snapshots["kyiv"] = RealSnapshot() with
        {
            CoverageStart = Kyiv(15, 0),
            CoverageEnd = Kyiv(17, 0),
            Groups = new Dictionary<string, GroupSchedule>
            {
                ["45.1"] = new("45.1", [new(Kyiv(15, 12, 30), Kyiv(15, 20))], [Kyiv(15, 0)]),
            },
            WeeklyPresets = preset,
            WeeklyPresetsWithMaybe = preset,
        };
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();

        Assert.Equal(1, await service.TickAsync(CancellationToken.None));
        var rows = WeekMaybe.Split(';');
        rows[3] = "000000000000311111110000";
        Assert.EndsWith("timetableData=" + Uri.EscapeDataString(string.Join(';', rows)),
            _factory.Svitlobot.Requests.Last().PathAndQuery);

        // Friday: the feed now has only Friday, yet Thursday keeps its published schedule instead of the preset.
        _factory.Clock.Now = Kyiv(16, 9);
        _factory.Schedules.Snapshots["kyiv"] = _factory.Schedules.Snapshots["kyiv"]! with
        {
            CoverageStart = Kyiv(16, 0),
            Groups = new Dictionary<string, GroupSchedule>
            {
                ["45.1"] = new("45.1", [new(Kyiv(16, 8), Kyiv(16, 10))], [Kyiv(16, 0)]),
            },
        };

        Assert.Equal(1, await service.TickAsync(CancellationToken.None));
        rows[4] = "000000001100000000000000";
        Assert.EndsWith("timetableData=" + Uri.EscapeDataString(string.Join(';', rows)),
            _factory.Svitlobot.Requests.Last().PathAndQuery);
        Assert.Equal(2, _factory.Svitlobot.Requests.Count);
    }

    // ================= main group of a multi-address chat =================

    private static void TwoAddresses(Channel c)
    {
        c.GroupId = "45.1";
        c.Addresses =
        [
            new ChatAddress { RegionId = "kyiv", Name = "Дім", Groups = [new ChatAddressGroup { GroupId = "45.1" }] },
            new ChatAddress { RegionId = "kyiv", Name = "Офіс", Groups = [new ChatAddressGroup { GroupId = "2.2" }] },
        ];
    }

    private static string TimetableOf(string week) =>
        $"/website/timetableEditEvent?channel_key={EncodedKey}&timetableData={Uri.EscapeDataString(week)}";

    [Fact]
    public async Task MainGroup_DefaultsToTheFirstAddressGroup_AndAChosenGroupIsWhatPushSends()
    {
        var (chatId, client) = UserWithKey(TwoAddresses);

        var summary = (await (await client.GetAsync("/api/chats")).ReadAsync<List<ChatSummaryDto>>())!
            .Single(c => c.ChatId == chatId);
        Assert.Equal("45.1", summary.Settings.SvitlobotGroupId);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/chats/{chatId}/svitlobot/push", null)).StatusCode);
        Assert.Equal(TimetableOf(WeekMaybe), _factory.Svitlobot.Requests.Last().PathAndQuery);

        var set = await client.PutJsonAsync($"/api/chats/{chatId}/svitlobot/group", new SetSvitlobotGroupRequest("2.2"));

        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        Assert.Equal("2.2", (await set.ReadAsync<SvitlobotGroupDto>())!.GroupId);
        Assert.Single(_factory.Svitlobot.Requests);
        _factory.Clock.Advance(SvitlobotRequestLimiter.Window);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/chats/{chatId}/svitlobot/push", null)).StatusCode);
        Assert.Equal(TimetableOf(Week), _factory.Svitlobot.Requests.Last().PathAndQuery);
    }

    [Fact]
    public async Task MainGroup_UnknownGroupIs400_AndSomeoneElsesChatIs403()
    {
        var (chatId, client) = UserWithKey(TwoAddresses);

        var unknown = await client.PutJsonAsync($"/api/chats/{chatId}/svitlobot/group", new SetSvitlobotGroupRequest("9.9"));
        var foreign = await _factory.ClientFor(_factory.NextId())
            .PutJsonAsync($"/api/chats/{chatId}/svitlobot/group", new SetSvitlobotGroupRequest("2.2"));

        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        Assert.Null(_factory.WithDb(db => db.Channels.AsNoTracking().Single(c => c.ChatId == chatId).SvitlobotGroupId));
    }

    [Fact]
    public async Task AutomaticScheduleSync_FollowsTheMainGroup_AndRewritesWhenItChanges()
    {
        ClearSyncState();
        var (chatId, client) = UserWithKey(TwoAddresses);
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();

        Assert.Equal(1, await service.TickAsync(CancellationToken.None));
        Assert.Equal(TimetableOf(WeekMaybe), Assert.Single(_factory.Svitlobot.Requests).PathAndQuery);

        await client.PutJsonAsync($"/api/chats/{chatId}/svitlobot/group", new SetSvitlobotGroupRequest("2.2"));
        scope.ServiceProvider.GetRequiredService<BotDbContext>().ChangeTracker.Clear();
        _factory.Clock.Advance(SvitlobotRequestLimiter.Window);

        Assert.Equal(1, await service.TickAsync(CancellationToken.None));
        Assert.Equal(TimetableOf(Week), _factory.Svitlobot.Requests.Last().PathAndQuery);
        Assert.Equal(0, await service.TickAsync(CancellationToken.None));
    }

    [Fact]
    public void MainGroup_FallsBackToTheFirstAddressGroup_WhenTheChosenGroupIsGone()
    {
        var channel = new Channel { RegionId = "kyiv", SvitlobotGroupId = "2.2" };
        TwoAddresses(channel);
        channel.Addresses[0].Id = 1;
        channel.Addresses[1].Id = 2;
        Assert.Equal("2.2", channel.SvitlobotGroup());

        channel.Addresses.RemoveAt(1);

        Assert.Equal("45.1", channel.SvitlobotGroup());
        Assert.Equal("45.1", new Channel { GroupId = "45.1" }.SvitlobotGroup());
        Assert.Null(new Channel().SvitlobotGroup());
    }

    [Fact]
    public async Task AutomaticScheduleSync_DoesNotSendWhenWeeklyTimetableHasNoOutages()
    {
        _factory.WithDb(db =>
        {
            foreach (var channel in db.Channels)
            {
                channel.SvitlobotKey = null;
            }
        });
        UserWithKey(c => c.GroupId = "45.1");
        _factory.Schedules.Snapshots["kyiv"] = _factory.Schedules.Snapshots["kyiv"]! with
        {
            WeeklyPresetsWithMaybe = new Dictionary<string, string> { ["45.1"] = Quiet },
        };
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();

        Assert.Equal(0, await service.TickAsync(CancellationToken.None));
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task OneTelegramUser_CanManageSeparateSvitlobotKeysForMultipleLinkedChannels()
    {
        const long firstChatId = -10070001;
        const long secondChatId = -10070002;
        const string firstKey = "first-channel-key";
        const string secondKey = "second-channel-key";
        var userId = _factory.NextId();
        var protector = new SvitlobotKeyProtector(() => _factory.SvitlobotEncryptionKey);
        _factory.Bot.SetStatus(userId, Telegram.Bot.Types.Enums.ChatMemberStatus.Administrator);
        _factory.Svitlobot.Responder = _ => FakeSvitlobotHandler.Text("12;&;Графік;&&&;no data");
        _factory.WithDb(db =>
        {
            foreach (var channel in db.Channels)
            {
                channel.SvitlobotKey = null;
            }

            db.KeyValues.RemoveRange(db.KeyValues.Where(value =>
                value.Key.StartsWith("svitlobot-schedule:")
                || value.Key.StartsWith("svitlobot-weekly-refresh:")));
            db.Channels.AddRange(
                new Channel { ChatId = firstChatId, RegionId = "kyiv", GroupId = "45.1", CreatedAt = _factory.Clock.Now },
                new Channel { ChatId = secondChatId, RegionId = "kyiv", GroupId = "2.2", CreatedAt = _factory.Clock.Now });
            db.ChannelAdmins.AddRange(
                new ChannelAdmin { ChatId = firstChatId, UserId = userId, LinkedAt = _factory.Clock.Now },
                new ChannelAdmin { ChatId = secondChatId, UserId = userId, LinkedAt = _factory.Clock.Now });
        });
        using var client = _factory.ClientFor(userId);

        var firstConnected = await client.PutJsonAsync(
            $"/api/chats/{firstChatId}/svitlobot/key",
            new SetSvitlobotKeyRequest(firstKey));
        var secondConnected = await client.PutJsonAsync(
            $"/api/chats/{secondChatId}/svitlobot/key",
            new SetSvitlobotKeyRequest(secondKey));

        Assert.Equal(HttpStatusCode.OK, firstConnected.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondConnected.StatusCode);
        Assert.True((await firstConnected.ReadAsync<SvitlobotStateDto>())!.HasKey);
        Assert.True((await secondConnected.ReadAsync<SvitlobotStateDto>())!.HasKey);

        var chats = (await (await client.GetAsync("/api/chats")).ReadAsync<List<ChatSummaryDto>>())!;
        Assert.True(Assert.Single(chats, chat => chat.ChatId == firstChatId).Settings.HasSvitlobotKey);
        Assert.True(Assert.Single(chats, chat => chat.ChatId == secondChatId).Settings.HasSvitlobotKey);

        _factory.Svitlobot.Reset();
        _factory.Clock.Advance(SvitlobotRequestLimiter.Window);
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();
        Assert.Equal(2, await service.TickAsync(CancellationToken.None));

        Assert.Contains(_factory.Svitlobot.Requests, request =>
            request.PathAndQuery == $"/website/timetableEditEvent?channel_key={firstKey}&timetableData={Uri.EscapeDataString(WeekMaybe)}");
        Assert.Contains(_factory.Svitlobot.Requests, request =>
            request.PathAndQuery == $"/website/timetableEditEvent?channel_key={secondKey}&timetableData={Uri.EscapeDataString(Week)}");
    }

    [Fact]
    public async Task Push_WithoutAGroup_Is400()
    {
        var (chatId, client) = UserWithKey();

        var response = await client.PostAsync($"/api/chats/{chatId}/svitlobot/push", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("чергу", await ErrorAsync(response));
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task Push_WithoutAKey_Is400()
    {
        var (chatId, client) = NewUser(c => c.GroupId = "45.1");

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync($"/api/chats/{chatId}/svitlobot/push", null)).StatusCode);
    }

    [Fact]
    public async Task Push_RegionWithoutSchedules_Is400()
    {
        var (chatId, client) = UserWithKey(c => { c.GroupId = "45.1"; c.RegionId = "donetsk"; });

        var response = await client.PostAsync($"/api/chats/{chatId}/svitlobot/push", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task Push_GroupWithoutAWeeklyPreset_OrWithoutOutages_Is400()
    {
        var (missing, missingClient) = UserWithKey(c => c.GroupId = "9.9");
        var missingResponse = await missingClient.PostAsync($"/api/chats/{missing}/svitlobot/push", null);
        Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
        Assert.Contains("9.9", await ErrorAsync(missingResponse));

        var (quiet, quietClient) = UserWithKey(c => c.GroupId = "2.1");
        var quietResponse = await quietClient.PostAsync($"/api/chats/{quiet}/svitlobot/push", null);
        Assert.Equal(HttpStatusCode.BadRequest, quietResponse.StatusCode);
        Assert.Contains("немає запланованих", await ErrorAsync(quietResponse));
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    [Fact]
    public async Task Push_ScheduleSourceDown_Is503()
    {
        var (chatId, client) = UserWithKey(c => c.GroupId = "45.1");
        _factory.Schedules.Snapshots.Clear();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsync($"/api/chats/{chatId}/svitlobot/push", null)).StatusCode);
        Assert.Empty(_factory.Svitlobot.Requests);
    }

    // ================= schedule =================

    [Fact]
    public async Task DeleteSchedule_CallsRemoveChannelTimetable()
    {
        var (chatId, client) = UserWithKey();

        var response = await client.DeleteAsync($"/api/chats/{chatId}/svitlobot/schedule");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False((await response.ReadAsync<SvitlobotResultDto>())!.HasSchedule);
        Assert.Equal($"/website/removeChannelTimetable?channel_key={EncodedKey}", Assert.Single(_factory.Svitlobot.Requests).PathAndQuery);
        Assert.False(string.IsNullOrEmpty(StoredKey(chatId)));
    }

    // ================= access, limits, config =================

    [Fact]
    public async Task EveryEndpoint_Is403_ForANonManager_AndNeverCallsSvitlobot()
    {
        var (_, client) = NewUser();
        var otherChat = _factory.NextId();
        _factory.WithDb(db => db.Channels.Add(new Channel { ChatId = otherChat, RegionId = "kyiv", GroupId = "45.1", CreatedAt = DateTimeOffset.UtcNow, SvitlobotKey = "x" }));
        var url = $"/api/chats/{otherChat}/svitlobot";

        var responses = new[]
        {
            await client.PutJsonAsync(url + "/key", new SetSvitlobotKeyRequest(Key)),
            await client.DeleteAsync(url + "/key"),
            await client.GetAsync(url),
            await client.PostJsonAsync(url + "/template", new SetSvitlobotTemplateRequest(1)),
            await client.PostAsync(url + "/push", null),
            await client.DeleteAsync(url + "/schedule"),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Empty(_factory.Svitlobot.Requests);
        Assert.Equal("x", StoredKey(otherChat));
    }

    [Fact]
    public async Task WithoutAuthorization_Is401()
    {
        using var client = _factory.CreateClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/chats/1/svitlobot")).StatusCode);
    }

    [Fact]
    public async Task Requests_AreLimitedToOnePerMinute_PerChat_AndReadsAreCached()
    {
        var (chatId, client) = UserWithKey();
        var (otherChat, otherClient) = UserWithKey();

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/chats/{chatId}/svitlobot/schedule")).StatusCode);

        var limited = await client.PostJsonAsync($"/api/chats/{chatId}/svitlobot/template", new SetSvitlobotTemplateRequest(1));

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Contains("Забагато", await ErrorAsync(limited));
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync($"/api/chats/{chatId}/svitlobot")).StatusCode);
        Assert.Single(_factory.Svitlobot.Requests);
        Assert.Equal(HttpStatusCode.OK, (await otherClient.DeleteAsync($"/api/chats/{otherChat}/svitlobot/schedule")).StatusCode);

        _factory.Clock.Advance(SvitlobotRequestLimiter.Window);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/chats/{chatId}/svitlobot")).StatusCode);
        _factory.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/chats/{chatId}/svitlobot")).StatusCode);
        Assert.Equal(3, _factory.Svitlobot.Requests.Count);
    }

    [Fact]
    public async Task WithoutTheServerSetting_EverythingAnswers503()
    {
        _factory.SvitlobotEncryptionKey = null;
        var (chatId, client) = UserWithKey(c => c.SvitlobotKey = "x");
        var url = $"/api/chats/{chatId}/svitlobot";

        var responses = new[]
        {
            await client.PutJsonAsync(url + "/key", new SetSvitlobotKeyRequest(Key)),
            await client.DeleteAsync(url + "/key"),
            await client.GetAsync(url),
            await client.PostJsonAsync(url + "/template", new SetSvitlobotTemplateRequest(1)),
            await client.PostAsync(url + "/push", null),
            await client.DeleteAsync(url + "/schedule"),
        };

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.ServiceUnavailable, r.StatusCode));
        Assert.Equal("Світлобот не налаштований на сервері. Адміністратор має задати Svitlobot:KeyEncryptionKey (32 випадкові байти в base64) і перезапустити застосунок.", await ErrorAsync(responses[0]));
        Assert.Empty(_factory.Svitlobot.Requests);
    }
    [Fact]
    public async Task MultipleGroups_PushAndSyncUseOnlyTheMainGroup_AndRewriteOnlyWhenItChanges()
    {
        _factory.WithDb(db => { foreach (var channel in db.Channels) channel.SvitlobotKey = null; });
        var (chatId, client) = UserWithKey(c => {
            c.GroupId = "2.2";
            c.Addresses = [new ChatAddress { Name = "Дім", RegionId = "kyiv", Groups = [
                new ChatAddressGroup { GroupId = "45.1" }, new ChatAddressGroup { GroupId = "2.2" }] }];
        });
        (await client.PostAsync($"/api/chats/{chatId}/svitlobot/push", null)).EnsureSuccessStatusCode();
        Assert.Equal(TimetableOf(Week), Assert.Single(_factory.Svitlobot.Requests).PathAndQuery);
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        Assert.Equal(0, await service.TickAsync(default));

        // Another group joining does not change what Svitlobot follows.
        _factory.WithDb(d => d.Channels.Single(c => c.ChatId == chatId).Addresses.Single().Groups.Add(new ChatAddressGroup { GroupId = "45.2" }));
        db.ChangeTracker.Clear();
        _factory.Clock.Advance(SvitlobotRequestLimiter.Window);
        Assert.Equal(0, await service.TickAsync(default));

        // Losing the main group falls back to the address's first remaining group.
        _factory.WithDb(d => d.Channels.Single(c => c.ChatId == chatId).Addresses.Single().Groups.RemoveAll(g => g.GroupId == "2.2"));
        db.ChangeTracker.Clear();
        Assert.Equal(1, await service.TickAsync(default));
        Assert.Equal(TimetableOf(WeekMaybe), _factory.Svitlobot.Requests.Last().PathAndQuery);
    }

}
