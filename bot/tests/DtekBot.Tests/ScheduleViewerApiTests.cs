using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DtekBot.Api.Data;
using DtekBot.Api.Grid;
using DtekBot.Api.Parsing;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot.Requests;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class ScheduleViewerApiTests
{
    private const string View = "/api/schedules/view?regionId=kyiv&groupId=45.1";

    [Fact]
    public async Task Viewer_RequiresTelegramAuthentication()
    {
        using var api = new ApiFactory();
        using var client = api.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(View)).StatusCode);
        Assert.Empty(api.Schedules.Calls);
        AssertNoDelivery(api);
    }

    [Fact]
    public async Task Viewer_WithoutSubscription_IsReadOnlyAndReturnsBothDaysAndCountdown()
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(15, 12);
        api.Schedules.Snapshots["kyiv"] = RealSnapshot(api.Clock.Now);
        using var client = api.ClientFor(123);
        var before = DatabaseSnapshot(api);

        for (var i = 0; i < 2; i++)
        {
            var view = await client.GetFromJsonAsync<ScheduleViewDto>(View);
            Assert.NotNull(view);
            Assert.Equal("kyiv", view.RegionId);
            Assert.Equal("м. Київ", view.RegionName);
            Assert.Equal("45.1", view.GroupId);
            Assert.Equal(api.Clock.Now, view.ServerNow);
            Assert.Equal(api.Clock.Now, view.CheckedAt);
            Assert.False(view.IsCached);
            Assert.Equal(["2026-10-15", "2026-10-16"], view.Days.Select(d => d.Date));
            Assert.All(view.Days, d => Assert.True(d.Available));
            Assert.Contains(view.Days[0].Intervals, o => o.Start == Kyiv(15, 11) && o.End == Kyiv(15, 14));
            Assert.Equal(Kyiv(15, 14), view.NextChangeAt);
            Assert.Equal(false, view.NextChangeIsOff);
        }

        Assert.Equal(before, DatabaseSnapshot(api));
        AssertNoDelivery(api);
    }

    [Theory]
    [InlineData("atlantis", "45.1")]
    [InlineData("donetsk", "45.1")]
    [InlineData("kyiv", "99.9")]
    public async Task Viewer_UnknownRegionOrGroup_IsBadRequestWithoutWrites(string region, string group)
    {
        using var api = new ApiFactory();
        api.Schedules.Snapshots["kyiv"] = RealSnapshot();
        using var client = api.ClientFor(123);
        var before = DatabaseSnapshot(api);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await client.GetAsync($"/api/schedules/view?regionId={region}&groupId={group}")).StatusCode);
        Assert.Equal(before, DatabaseSnapshot(api));
        AssertNoDelivery(api);
    }

    [Fact]
    public async Task Viewer_MissingSource_IsUnavailableNotAnEmptySchedule()
    {
        using var api = new ApiFactory();
        using var client = api.ClientFor(123);
        var response = await client.GetAsync(View);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("недоступні", (await response.Content.ReadFromJsonAsync<ApiError>())!.Error);
        AssertNoDelivery(api);
    }

    [Fact]
    public async Task Viewer_StaleCopy_ExposesOriginalCheckTime()
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(15, 15);
        var checkedAt = api.Clock.Now.AddHours(-2);
        api.Schedules.Snapshots["kyiv"] = RealSnapshot(checkedAt);
        using var client = api.ClientFor(123);
        var view = (await client.GetFromJsonAsync<ScheduleViewDto>(View))!;
        Assert.True(view.IsCached);
        Assert.Equal(checkedAt, view.CheckedAt);
        Assert.Equal(api.Clock.Now, view.ServerNow);
        Assert.Equal(Kyiv(15, 20, 30), view.NextChangeAt);
        Assert.Equal(true, view.NextChangeIsOff);
    }

    [Theory]
    [InlineData(0, true, false)]
    [InlineData(1, false, true)]
    public async Task Viewer_PartialDayCoverage_DoesNotPublishIncompleteIntervals(
        int edge, bool todayAvailable, bool tomorrowAvailable)
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(15, 12);
        var snapshot = RealSnapshot(api.Clock.Now);
        api.Schedules.Snapshots["kyiv"] = edge == 0
            ? snapshot with { CoverageEnd = Kyiv(16, 12) }
            : snapshot with { CoverageStart = Kyiv(15, 1) };
        using var client = api.ClientFor(123);
        var view = (await client.GetFromJsonAsync<ScheduleViewDto>(View))!;
        Assert.Equal(todayAvailable, view.Days[0].Available);
        Assert.Equal(tomorrowAvailable, view.Days[1].Available);
        Assert.All(view.Days.Where(d => !d.Available), d => Assert.Empty(d.Intervals));
    }

    [Fact]
    public async Task Viewer_MissingGroupTomorrow_IsUnavailableRatherThanNoOutages()
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(15, 12);
        api.Schedules.Snapshots["kyiv"] = ScheduleParser.Parse(
            Json("v", null, (Day1, "45.1", Hours("no", 12)), (Day2, "46.1", Hours("no", 12))),
            "kyiv", api.Clock.Now);
        using var client = api.ClientFor(123);
        var view = (await client.GetFromJsonAsync<ScheduleViewDto>(View))!;
        Assert.True(view.Days[0].Available);
        Assert.False(view.Days[1].Available);
        Assert.Empty(view.Days[1].Intervals);
    }

    [Fact]
    public async Task Viewer_OutageAtLastPublishedGroupDay_DoesNotPromiseMidnightRestoration()
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(15, 23);
        var snapshot = ScheduleParser.Parse(
            Json("v", null, (Day1, "45.1", Hours("no", 23, 24)), (Day2, "46.1", [])),
            "kyiv", api.Clock.Now)!;
        api.Schedules.Snapshots["kyiv"] = snapshot;
        using var client = api.ClientFor(123);
        var current = snapshot.StatusAt("45.1", api.Clock.Now)!;
        Assert.True(current.IsOff);
        Assert.Null(current.Until);
        var view = (await client.GetFromJsonAsync<ScheduleViewDto>(View))!;
        Assert.False(view.Days[1].Available);
        Assert.Null(view.NextChangeAt);
        Assert.Null(view.NextChangeIsOff);
    }

    [Theory]
    [InlineData("2026-03-29", 23)]
    [InlineData("2026-10-25", 25)]
    public async Task Viewer_DstDay_UsesKyivCalendarBoundariesAndClipsIntervals(string date, int hours)
    {
        using var api = new ApiFactory();
        var day = DateTime.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var start = ScheduleText.KyivDayStart(day);
        var midnight = ScheduleText.KyivDayStart(day.AddDays(1));
        var end = ScheduleText.KyivDayStart(day.AddDays(2));
        api.Clock.Now = start.AddHours(12);
        api.Schedules.Snapshots["kyiv"] = new("kyiv", "v", api.Clock.Now, null,
            start.AddDays(-1), end.AddDays(1), new Dictionary<string, GroupSchedule>
            {
                ["45.1"] = new("45.1", [new(start.AddHours(-1), end.AddHours(1))]),
            });
        using var client = api.ClientFor(123);
        var view = (await client.GetFromJsonAsync<ScheduleViewDto>(View))!;
        Assert.Equal(date, view.Days[0].Date);
        var today = Assert.Single(view.Days[0].Intervals);
        Assert.Equal(start, today.Start);
        Assert.Equal(midnight, today.End);
        Assert.Equal(TimeSpan.FromHours(hours), today.End - today.Start);
        var tomorrow = Assert.Single(view.Days[1].Intervals);
        Assert.Equal(midnight, tomorrow.Start);
        Assert.Equal(end, tomorrow.End);
    }

    [Fact]
    public async Task Viewer_AtMidnight_AdvancesDatesAndDoesNotInventAnEndAtCoverageBoundary()
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(16, 0);
        var snapshot = RealSnapshot(api.Clock.Now);
        api.Schedules.Snapshots["kyiv"] = snapshot with
        {
            Groups = new Dictionary<string, GroupSchedule>
            {
                ["45.1"] = new("45.1", [new(Kyiv(16, 0), snapshot.CoverageEnd)]),
            },
        };
        using var client = api.ClientFor(123);
        var view = (await client.GetFromJsonAsync<ScheduleViewDto>(View))!;
        Assert.Equal(["2026-10-16", "2026-10-17"], view.Days.Select(d => d.Date));
        Assert.True(view.Days[0].Available);
        Assert.False(view.Days[1].Available);
        Assert.Null(view.NextChangeAt);
        Assert.Null(view.NextChangeIsOff);
    }

    [Theory]
    [InlineData("2026-03-29")]
    [InlineData("2026-10-25")]
    public async Task Viewer_ParsedDstDay_HasFullKyivDayCoverage(string date)
    {
        using var api = new ApiFactory();
        var day = DateTime.ParseExact(date, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var start = ScheduleText.KyivDayStart(day);
        var end = ScheduleText.KyivDayStart(day.AddDays(1));
        api.Clock.Now = start.AddHours(12);
        var snapshot = ScheduleParser.Parse(
            Json("dst", null, (start.ToUnixTimeSeconds(), "45.1", [])), "kyiv", api.Clock.Now)!;
        api.Schedules.Snapshots["kyiv"] = snapshot;
        using var client = api.ClientFor(123);
        var view = (await client.GetFromJsonAsync<ScheduleViewDto>(View))!;
        Assert.Equal(end, snapshot.CoverageEnd);
        Assert.True(view.Days[0].Available);
        Assert.False(view.Days[1].Available);
        Assert.Empty(view.Days[0].Intervals);
    }

    [Theory]
    [InlineData(PopupKind.Emergency)]
    [InlineData(PopupKind.Accident)]
    public async Task Viewer_RegionalEmergency_WarnsPublishedPlanMayNotApply(PopupKind kind)
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(15, 12);
        api.Schedules.Snapshots["kyiv"] = RealSnapshot(api.Clock.Now);
        using var client = api.ClientFor(123);
        api.WithDb(db => db.RegionStates.Add(new RegionState
        {
            RegionId = "kyiv", Kind = kind, ObservedAt = api.Clock.Now,
        }));
        var view = (await client.GetFromJsonAsync<ScheduleViewDto>(View))!;
        Assert.Contains(view.Warnings!, w => w.Contains("ДТЕК") && w.Contains("План може не діяти"));
        AssertNoDelivery(api);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Viewer_GridWarning_RequiresFreshFeed(bool stale)
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(15, 12);
        api.Schedules.Snapshots["kyiv"] = RealSnapshot(api.Clock.Now);
        using var client = api.ClientFor(123);
        api.WithDb(db => db.GridStates.Add(new GridState
        {
            RegionId = "kyiv", Status = GridStatuses.EmergencyOutages,
            FeedGeneratedAt = stale ? api.Clock.Now.AddDays(-1) : api.Clock.Now,
        }));
        var view = (await client.GetFromJsonAsync<ScheduleViewDto>(View))!;
        Assert.Equal(!stale, view.Warnings!.Any(w => w.Contains("Укренерго")));
        AssertNoDelivery(api);
    }

    [Fact]
    public async Task Viewer_YasnoWarning_IsScopedToSelectedGroup()
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(15, 12);
        var snapshot = RealSnapshot(api.Clock.Now);
        api.Schedules.Snapshots["kyiv"] = snapshot with
        {
            Groups = new Dictionary<string, GroupSchedule>(snapshot.Groups)
            {
                ["46.1"] = snapshot.Groups["45.1"] with { GroupId = "46.1" },
            },
        };
        using var client = api.ClientFor(123);
        api.WithDb(db => db.GroupStates.Add(new GroupState
        {
            RegionId = "kyiv", GroupId = "45.1", Today = GroupStatuses.Emergency, UpdatedAt = api.Clock.Now,
        }));
        var selected = (await client.GetFromJsonAsync<ScheduleViewDto>(View))!;
        Assert.Contains(selected.Warnings!, w => w.Contains("Yasno") && w.Contains("45.1"));
        var another = (await client.GetFromJsonAsync<ScheduleViewDto>(View.Replace("45.1", "46.1")))!;
        Assert.DoesNotContain(another.Warnings!, w => w.Contains("Yasno"));
    }

    [Theory]
    [InlineData(false, false, "адресу не знайдено.")]
    [InlineData(true, false, "запису про відключення немає.")]
    [InlineData(true, true, "є запис про відключення.")]
    public async Task Status_AddressCheck_ReportsRecordNotActualPowerAndLabelsStaleAnswer(
        bool found, bool outage, string expected)
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(15, 12);
        using var client = api.ClientFor(123);
        api.WithDb(db =>
        {
            db.Channels.Add(new Channel
            {
                ChatId = 123, RegionId = "kyiv", Addresses = [new()
                {
                    RegionId = "kyiv", Name = "Дім", City = "", Street = "Тестова", House = "1",
                }],
            });
            db.AddressResults.Add(new AddressResult
            {
                RegionId = "kyiv", City = "", Street = "Тестова", House = "1",
                Found = found, SubType = outage ? "аварійні" : "", CheckedAt = api.Clock.Now.AddDays(-1),
            });
        });
        var status = (await client.GetFromJsonAsync<ChatStatusDto>("/api/chats/123/status"))!;
        Assert.Contains(status.Diagnostics!.Lines, l => l.Contains(expected) && l.Contains("поточний стан не підтверджено"));
        Assert.DoesNotContain(status.Lines.Concat(status.Diagnostics.Lines),
            l => l.Contains("світло є") || l.Contains("світло відновлено") || l.Contains("🟢"));
        AssertNoDelivery(api);
    }

    [Fact]
    public async Task Status_DiagnosticsUseSavedSettingsNotDraftAndDoNotWriteOrSend()
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(15, 12);
        api.Schedules.Snapshots["kyiv"] = RealSnapshot(api.Clock.Now.AddHours(-2));
        using var client = api.ClientFor(123);
        api.WithDb(db => db.Channels.Add(new Channel
        {
            ChatId = 123, RegionId = "kyiv", GroupId = "45.1",
            PartIdList = ["darnytskyi"], IsPaused = true, SchedulePost = true,
            Addresses = [new() { RegionId = "kyiv", Name = "Дім", City = "Київ",
                Street = "Тестова", House = "1", Groups = [new() { GroupId = "45.1" }] }],
        }));
        var before = DatabaseSnapshot(api);
        var status = (await client.GetFromJsonAsync<ChatStatusDto>("/api/chats/123/status?regionId=odesa"))!;
        Assert.NotEmpty(status.Lines);
        Assert.NotNull(status.Diagnostics);
        var lines = status.Diagnostics.Lines;
        Assert.Contains("Збережений регіон: м. Київ.", lines);
        Assert.Contains("Підписка: на паузі.", lines);
        Assert.Contains(lines, l => l.Contains("Фактична наявність електроенергії невідома"));
        Assert.Contains(lines, l => l.Contains("Дарницький"));
        Assert.Contains("Збережені черги: 45.1.", lines);
        Assert.Contains(lines, l => l.Contains("Адреса «Дім»"));
        Assert.Contains(lines, l => l.Contains("попередня копія"));
        Assert.Contains("Графік на сьогодні: доступний для збережених черг.", lines);
        Assert.Contains("Графік на завтра: доступний для збережених черг.", lines);
        Assert.Contains(lines, l => l.Contains("не перевірка наявності повідомлення в Telegram"));
        Assert.Equal(before, DatabaseSnapshot(api));
        AssertNoDelivery(api);
    }

    [Fact]
    public async Task Status_NoSubscription_ExplainsViewerWithoutCreatingSubscription()
    {
        using var api = new ApiFactory();
        using var client = api.ClientFor(123);
        var before = DatabaseSnapshot(api);
        var status = (await client.GetFromJsonAsync<ChatStatusDto>("/api/chats/123/status"))!;
        Assert.Contains("Підписку ще не налаштовано. Перегляд графіка не потребує підписки.",
            status.Diagnostics!.Lines);
        Assert.Equal(before, DatabaseSnapshot(api));
        AssertNoDelivery(api);
    }

    [Fact]
    public async Task Status_OtherUsersPrivateChat_IsForbidden()
    {
        using var api = new ApiFactory();
        using var client = api.ClientFor(123);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await client.GetAsync("/api/chats/456/status")).StatusCode);
    }

    private static string DatabaseSnapshot(ApiFactory api) => api.WithDb(db =>
        JsonSerializer.Serialize(db.Model.GetEntityTypes().Where(t => t.BaseType is null)
            .OrderBy(t => t.Name).Select(t => new
            {
                t.Name,
                Rows = ReadRows(db, t.GetTableName()!),
            }).ToArray()));

    private static void AssertNoDelivery(ApiFactory api) =>
        Assert.All(api.Bot.Requests, r => Assert.True(r is SetChatMenuButtonRequest or SetMyCommandsRequest,
            $"Read-only endpoint made unexpected Telegram request {r.GetType().Name}."));

    private static IReadOnlyList<string> ReadRows(BotDbContext db, string table)
    {
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = $"SELECT * FROM \"{table.Replace("\"", "\"\"")}\"";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                .Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
        return rows.Order(StringComparer.Ordinal).ToArray();
    }
}
