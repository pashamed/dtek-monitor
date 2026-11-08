using System.Net.Http.Json;
using DtekBot.Api.Data;
using DtekBot.Api.MiniApp;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class ScheduleTimelineTests
{
    [Fact]
    public void Timeline_MatchesRequestedIntervalsAndTotal()
    {
        var start = ScheduleText.KyivDayStart(new DateTime(2026, 11, 3));
        var end = ScheduleText.KyivDayStart(new DateTime(2026, 11, 4));
        var group = new GroupSchedule("1", [
            new(start, start.AddHours(3)),
            new(start.AddHours(9.5), start.AddHours(17)),
            new(start.AddHours(20), end),
        ]);
        Assert.Equal([
            "❌ 00:00–03:00 (3 год)",
            "🟢 03:00–09:30 (6 год 30 хв)",
            "❌ 09:30–17:00 (7 год 30 хв)",
            "🟢 17:00–20:00 (3 год)",
            "❌ 20:00–24:00 (4 год)",
            "Загалом без світла за графіком: 14 год 30 хв",
        ], ScheduleText.TimelineLines(group, start, end, "❌ {start}–{end} ({duration})"));
    }

    [Fact]
    public void Timeline_SortsMergesAndClipsWithoutDoubleCounting()
    {
        var start = Kyiv(15, 0);
        var end = Kyiv(16, 0);
        var group = new GroupSchedule("1", [
            new(start.AddHours(4), start.AddHours(6)),
            new(start.AddHours(-2), start.AddHours(3)),
            new(start.AddHours(2), start.AddHours(4)),
            new(start.AddHours(23), end.AddHours(2)),
            new(end, end.AddHours(3)),
            new(start.AddHours(9), start.AddHours(9)),
        ]);
        Assert.Equal([
            "🔴 00:00 - 06:00 (6 год)",
            "🟢 06:00–23:00 (17 год)",
            "🔴 23:00 - 24:00 (1 год)",
            "Загалом без світла за графіком: 7 год",
        ], ScheduleText.TimelineLines(group, start, end));
    }

    [Theory]
    [InlineData(2026, 3, 29, 23)]
    [InlineData(2026, 10, 25, 25)]
    [InlineData(2026, 11, 3, 24)]
    public void Timeline_EmptyAndFullDaysUseActualKyivDayLength(int year, int month, int day, int hours)
    {
        var date = new DateTime(year, month, day);
        var start = ScheduleText.KyivDayStart(date);
        var end = ScheduleText.KyivDayStart(date.AddDays(1));
        Assert.Equal([
            $"🟢 00:00–24:00 ({hours} год)",
            "Загалом без світла за графіком: 0 хв",
        ], ScheduleText.TimelineLines(new("1", []), start, end));
        Assert.Equal([
            $"🔴 00:00 - 24:00 ({hours} год)",
            $"Загалом без світла за графіком: {hours} год",
        ], ScheduleText.TimelineLines(new("1", [new(start, end)]), start, end));
    }

    [Fact]
    public void Timeline_UsesSeparateRowTemplatesAndEscapesOnlyOnce()
    {
        var channel = new Channel
        {
            RegionId = "kyiv", GroupId = "45.1", ScheduleShowLightPeriods = true,
            TemplateMap = new Dictionary<string, string>
            {
                [MessageSlots.ScheduleLine] = "OFF {start} → {end} ({duration})",
                [MessageSlots.ScheduleLightLine] = "ON {date} {weekday} {daypart} {start} &amp; {end} ({duration})",
            },
        };
        var text = CombinedSchedule.Render(channel, RealSnapshot(), "Київ", Kyiv(15, 12), false);
        Assert.Contains("OFF 11:00 → 14:00 (3 год)", text);
        Assert.Contains("ON 15.10.2026 Четвер Ніч 00:00 &amp; 11:00 (11 год)", text);
        Assert.DoesNotContain("&amp;amp;", text);
        Assert.Contains("ON 16.10.2026 Пʼятниця Ніч 00:00 &amp; 06:00 (6 год)", text);
    }

    [Fact]
    public void Timeline_MissingGroupOrIncompleteDayDoesNotInventLightOrTotals()
    {
        var channel = new Channel { RegionId = "kyiv", GroupId = "missing", ScheduleShowLightPeriods = true };
        var snapshot = RealSnapshot();
        var text = CombinedSchedule.Render(channel, snapshot, "Київ", Kyiv(15, 12), false);
        Assert.Contains("Графік тимчасово недоступний.", text);
        Assert.DoesNotContain("🟢", text);
        Assert.DoesNotContain("Загалом", text);
        channel.GroupId = "45.1";
        text = CombinedSchedule.Render(channel, snapshot with { CoverageStart = Kyiv(15, 1), CoverageEnd = Kyiv(16, 0) },
            "Київ", Kyiv(15, 12), false);
        Assert.DoesNotContain("🟢", text);
        Assert.DoesNotContain("Загалом", text);
    }

    [Fact]
    public void Timeline_UnpublishedGroupDayDoesNotInventLightOrTotals()
    {
        var channel = new Channel { RegionId = "kyiv", GroupId = "45.1", ScheduleShowLightPeriods = true };
        var snapshot = RealSnapshot() with
        {
            Groups = new Dictionary<string, GroupSchedule> { ["45.1"] = new("45.1", [], [Kyiv(15, 0)]) },
        };
        var text = CombinedSchedule.Render(channel, snapshot, "Київ", Kyiv(15, 12), false);
        Assert.Equal(1, text.Split("Загалом без світла за графіком: 0 хв").Length - 1);
        Assert.EndsWith("Графік тимчасово недоступний.", text);
    }

    [Fact]
    public void Timeline_MultipleGroupsHaveIndependentTotalsAndLegacyModeIsUnchanged()
    {
        var snapshot = RealSnapshot();
        var channel = new Channel
        {
            RegionId = "kyiv", ScheduleShowLightPeriods = true,
            Addresses = [new() { RegionId = "kyiv", Name = "Дім", Groups = [new() { GroupId = "45.1" }, new() { GroupId = "empty" }] }],
        };
        snapshot = snapshot with { Groups = new Dictionary<string, GroupSchedule>(snapshot.Groups) { ["empty"] = new("empty", []) } };
        var text = CombinedSchedule.Render(channel, snapshot, "Київ", Kyiv(15, 12), false);
        Assert.Contains("Загалом без світла за графіком: 6 год 30 хв", text);
        Assert.Contains("Загалом без світла за графіком: 7 год", text);
        Assert.Equal(2, text.Split("Загалом без світла за графіком: 0 хв").Length - 1);
        channel.ScheduleShowLightPeriods = false;
        text = CombinedSchedule.Render(channel, snapshot, "Київ", Kyiv(15, 12), false);
        Assert.Contains("🔴 11:00 - 14:00 (~3 год)", text);
        Assert.DoesNotContain("Загалом", text);
        Assert.DoesNotContain("🟢 00:00", text);
    }

    [Fact]
    public async Task Settings_RoundTripPreservesModeForLegacyClientsAndCanDisableIt()
    {
        using var api = new ApiFactory();
        using var client = api.ClientFor(94001);
        api.WithDb(db => db.Channels.Add(new() { ChatId = 94001, RegionId = "kyiv" }));
        var settings = new ChatSettingsDto("kyiv", null, false, false, AlertFilter.All, true, true, null,
            ScheduleShowLightPeriods: true);
        var response = await client.PutAsJsonAsync("/api/chats/94001", settings, ApiJson.Options);
        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadFromJsonAsync<ChatSummaryDto>(ApiJson.Options))!.Settings.ScheduleShowLightPeriods);
        response = await client.PutAsJsonAsync("/api/chats/94001", settings with { ScheduleShowLightPeriods = null }, ApiJson.Options);
        response.EnsureSuccessStatusCode();
        Assert.True((await response.Content.ReadFromJsonAsync<ChatSummaryDto>(ApiJson.Options))!.Settings.ScheduleShowLightPeriods);
        var chats = await client.GetFromJsonAsync<List<ChatSummaryDto>>("/api/chats", ApiJson.Options);
        Assert.True(Assert.Single(chats!).Settings.ScheduleShowLightPeriods);
        response = await client.PutAsJsonAsync("/api/chats/94001", settings with { ScheduleShowLightPeriods = false }, ApiJson.Options);
        response.EnsureSuccessStatusCode();
        Assert.False((await response.Content.ReadFromJsonAsync<ChatSummaryDto>(ApiJson.Options))!.Settings.ScheduleShowLightPeriods);
        Assert.Empty(api.Bot.Sent);
    }

    [Theory]
    [InlineData(false, ScheduleImage.None)]
    [InlineData(true, ScheduleImage.None)]
    [InlineData(true, ScheduleImage.Grid)]
    public async Task Preview_UsesModeAndLightTemplateWithOrWithoutAddresses_WithoutSending(bool addresses, ScheduleImage image)
    {
        using var api = new ApiFactory();
        using var client = api.ClientFor(94002);
        api.Clock.Now = Kyiv(15, 12);
        api.Schedules.Snapshots["kyiv"] = RealSnapshot();
        var request = new PreviewRequest(null, AlertKind.Emergency, "kyiv", null,
            Slot: MessageSlots.Schedule, GroupId: "45.1", ImageKind: image,
            Addresses: addresses ? [new(0, "kyiv", "Дім", null, null, null, [new("45.1")])] : null,
            ScheduleShowLightPeriods: true, ScheduleLightLineTemplate: "ON {початок}–{кінець} ({тривалість_інтервалу})");
        var response = await client.PostAsJsonAsync("/api/preview", request, ApiJson.Options);
        response.EnsureSuccessStatusCode();
        var preview = (await response.Content.ReadFromJsonAsync<PreviewResponse>(ApiJson.Options))!;
        Assert.Empty(preview.Errors);
        Assert.Contains("ON 00:00–11:00 (11 год)", preview.Html);
        Assert.Contains("Загалом без світла за графіком: 6 год 30 хв", preview.Html);
        response = await client.PostAsJsonAsync("/api/preview", request with { ScheduleLightLineTemplate = "<b>{start}</b>" }, ApiJson.Options);
        Assert.NotEmpty((await response.Content.ReadFromJsonAsync<PreviewResponse>(ApiJson.Options))!.Errors);
        Assert.Empty(api.Bot.Sent);
    }

    [Fact]
    public async Task Settings_DefaultModeIsOff()
    {
        using var api = new ApiFactory();
        using var client = api.ClientFor(94000);
        api.WithDb(db => db.Channels.Add(new() { ChatId = 94000, RegionId = "kyiv" }));
        var chats = await client.GetFromJsonAsync<List<ChatSummaryDto>>("/api/chats", ApiJson.Options);
        Assert.False(Assert.Single(chats!).Settings.ScheduleShowLightPeriods);
        Assert.Empty(api.Bot.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TestSend_UsesSavedModeAndLightTemplateForLegacyAndAddressClients(bool addresses)
    {
        var chatId = addresses ? 94004 : 94003;
        using var api = new ApiFactory();
        using var client = api.ClientFor(chatId);
        api.Clock.Now = Kyiv(15, 12);
        api.Schedules.Snapshots["kyiv"] = RealSnapshot();
        api.WithDb(db => db.Channels.Add(new()
        {
            ChatId = chatId, RegionId = "kyiv", GroupId = "45.1", ScheduleShowLightPeriods = true,
            Addresses = addresses ? [new() { Name = "Дім", RegionId = "kyiv", Groups = [new() { GroupId = "45.1" }] }] : [],
            TemplateMap = new Dictionary<string, string> { [MessageSlots.ScheduleLightLine] = "SAVED {start}–{end}" },
        }));
        var response = await client.PostAsJsonAsync($"/api/chats/{chatId}/templates/schedule/send",
            new SendTestRequest(null, ScheduleImage.None), ApiJson.Options);
        response.EnsureSuccessStatusCode();
        var sent = Assert.Single(api.Bot.SentTo(chatId));
        Assert.Contains("SAVED 00:00–11:00", sent.Text);
        Assert.Contains("Загалом без світла за графіком: 6 год 30 хв", sent.Text);
    }

    [Fact]
    public async Task TestSend_LongLightRowsAreSplitWithinTelegramLimits()
    {
        using var api = new ApiFactory();
        using var client = api.ClientFor(94005);
        api.Clock.Now = Kyiv(15, 12);
        api.Schedules.Snapshots["kyiv"] = RealSnapshot();
        api.WithDb(db => db.Channels.Add(new()
        {
            ChatId = 94005, RegionId = "kyiv", GroupId = "45.1", ScheduleShowLightPeriods = true,
            TemplateMap = new Dictionary<string, string>
            {
                [MessageSlots.ScheduleLightLine] = new string('x', 900) + " {start}–{end}",
            },
        }));
        var response = await client.PostAsJsonAsync("/api/chats/94005/templates/schedule/send",
            new SendTestRequest(null, ScheduleImage.None), ApiJson.Options);
        response.EnsureSuccessStatusCode();
        var messages = api.Bot.SentTo(94005).ToArray();
        Assert.Equal(2, messages.Length);
        Assert.All(messages, message => Assert.True(message.Text.Length <= 4096));
        Assert.Contains("Загалом без світла за графіком: 7 год", string.Join("\n", messages.Select(m => m.Text)));
    }

    [Fact]
    public async Task SamplePreview_AlsoUsesTimelineMode()
    {
        using var f = new DispatcherFixture();
        var service = new TemplatePreviewService(f.Options, new FakeScheduleProvider(), f.Clock);
        var rendered = await service.RenderAsync(MessageSlots.Schedule, null, f.Kyiv, [], null, null,
            ScheduleImage.None, null, default, channel: new() { RegionId = "kyiv", ScheduleShowLightPeriods = true });
        Assert.StartsWith("<b>Зразок</b>", rendered.Html);
        Assert.Contains("🟢 00:00–14:00 (14 год)", rendered.Html);
        Assert.Contains("Загалом без світла за графіком: 4 год", rendered.Html);
    }
}
