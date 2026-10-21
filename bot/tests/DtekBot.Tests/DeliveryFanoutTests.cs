using System.Net.Http.Json;
using DtekBot.Api.Data;
using DtekBot.Api.Groups;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using Telegram.Bot.Requests;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class DeliveryFanoutTests
{
    [Fact]
    public async Task LocalIngest_ReachesEveryMatchingActiveSubscriber_AndClosesOnlyTheirDeliveries()
    {
        using var api = new ApiFactory();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        api.WithDb(db =>
        {
            for (var i = 0; i < 6; i++)
                db.Channels.Add(new Channel
                {
                    ChatId = 83000 + i,
                    RegionId = i == 5 ? "kyiv-oblast" : "kyiv",
                    GroupId = i == 2 ? "2.1" : "45.1",
                    IsPaused = i == 3,
                    IsActive = i != 4,
                });
        });
        await Groups(GroupStatuses.Emergency);
        Assert.Equal(new long[] { 83000, 83001 },
            api.Bot.Sent.Select(r => long.Parse(r.ChatId.ToString()!)).Order().ToArray());
        var before = api.Bot.Requests.Count;
        await Groups(GroupStatuses.Emergency);
        Assert.Equal(before, api.Bot.Requests.Count);
        await Groups("");
        Assert.Equal(4, api.Bot.Sent.Count());
        Assert.All(api.Bot.Sent.Skip(2), r => Assert.NotNull(r.ReplyParameters));
        api.WithDb(db =>
        {
            Assert.Equal(2, db.Deliveries.Count());
            Assert.All(db.Deliveries, d => Assert.True(d.IsClosed));
        });
        before = api.Bot.Requests.Count;
        await Groups("");
        Assert.Equal(before, api.Bot.Requests.Count);

        async Task Groups(string status)
        {
            var response = await client.PostAsJsonAsync("/ingest/groups",
                new GroupsIngestRequest("kyiv", new() { ["45.1"] = new(status, "") }), ApiJson.Options);
            response.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task Ingest_DeliversToEveryEligibleSubscriber_WithoutCrossingCityOblastBoundary()
    {
        using var api = new ApiFactory();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        var regions = new[] { "kyiv", "kyiv-oblast" };
        api.WithDb(db =>
        {
            foreach (var (region, index) in regions.Select((r, i) => (r, i)))
            for (var subscriber = 0; subscriber < 4; subscriber++)
                db.Channels.Add(new Channel
                {
                    ChatId = 81000 + index * 10 + subscriber,
                    RegionId = region,
                    PartIdList = subscriber == 1 ? [index == 0 ? "darnytskyi" : "boryspil"] : [],
                    IsActive = subscriber != 2,
                    IsPaused = subscriber == 3,
                });
        });
        foreach (var (region, index) in regions.Select((r, i) => (r, i)))
        {
            var before = api.Bot.Sent.Count();
            await Ingest(region, DispatcherFixture.EmergencyText);
            Assert.Equal(new long[] { 81000 + index * 10, 81001 + index * 10 },
                api.Bot.Sent.Skip(before).Select(r => long.Parse(r.ChatId.ToString()!)).Order().ToArray());
            before = api.Bot.Requests.Count;
            await Ingest(region, DispatcherFixture.EmergencyText);
            Assert.Equal(before, api.Bot.Requests.Count);
            before = api.Bot.Sent.Count();
            await Ingest(region, DispatcherFixture.StabilizationText);
            Assert.Equal(2, api.Bot.Sent.Count() - before);
            Assert.All(api.Bot.Sent.Skip(before), r => Assert.NotNull(r.ReplyParameters));
        }
        api.WithDb(db =>
        {
            Assert.Equal(4, db.Deliveries.Count());
            Assert.All(db.Deliveries, d => Assert.True(d.IsClosed));
        });

        async Task Ingest(string region, string text)
        {
            api.Clock.Now = api.Clock.Now.AddSeconds(1);
            var response = await client.PostAsJsonAsync("/ingest", new IngestRequest(region, text, api.Clock.Now), ApiJson.Options);
            response.EnsureSuccessStatusCode();
        }
    }

    [Theory]
    [InlineData(PostMode.Edit)]
    [InlineData(PostMode.New)]
    [InlineData(PostMode.ReplaceDelete)]
    public async Task CronEndpoint_PostsChangedSchedulesToEveryEligibleCityAndOblastSubscriber(PostMode mode)
    {
        using var api = new ApiFactory();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        var eligible = new long[] { 82000, 82001, 82010, 82011 };
        api.WithDb(db =>
        {
            for (var region = 0; region < 2; region++)
            for (var subscriber = 0; subscriber < 5; subscriber++)
                db.Channels.Add(new Channel
                {
                    ChatId = 82000 + region * 10 + subscriber,
                    RegionId = region == 0 ? "kyiv" : "kyiv-oblast",
                    GroupId = "45.1",
                    SchedulePost = subscriber != 4,
                    IsActive = subscriber != 2,
                    IsPaused = subscriber == 3,
                    SchedulePostMode = mode,
                    SchedulePostImage = ScheduleImage.None,
                });
        });
        foreach (var region in new[] { "kyiv", "kyiv-oblast" })
            api.Schedules.Snapshots[region] = RealSnapshot(dataFile: region == "kyiv" ? "kyiv" : "kyiv-region");
        var first = await Tick();
        Assert.Equal(4, first.Sent);
        Assert.Equal(eligible, api.Bot.Sent.Select(r => long.Parse(r.ChatId.ToString()!)).Order().ToArray());
        var before = api.Bot.Requests.Count;
        await Tick();
        Assert.Equal(before, api.Bot.Requests.Count);
        foreach (var region in new[] { "kyiv", "kyiv-oblast" })
        {
            var snapshot = api.Schedules.Snapshots[region]!;
            api.Schedules.Snapshots[region] = snapshot with
            {
                Version = "changed",
                Groups = new Dictionary<string, GroupSchedule>(snapshot.Groups)
                {
                    ["45.1"] = snapshot.Groups["45.1"] with
                    {
                        Outages = snapshot.Groups["45.1"].Outages.Select((o, i) =>
                            i == 0 ? o with { End = o.End.AddMinutes(30) } : o).ToArray(),
                    },
                },
            };
        }
        api.Clock.Now = api.Clock.Now.AddMinutes(5);
        var changed = await Tick();
        Assert.Equal(0, changed.Failed);
        Assert.Equal(4, changed.Sent + changed.Edited);
        var requests = api.Bot.Requests.Skip(before).ToArray();
        var recipients = requests.OfType<SendMessageRequest>().Select(r => r.ChatId)
            .Concat(requests.OfType<EditMessageTextRequest>().Select(r => r.ChatId));
        Assert.Equal(eligible, recipients.Select(id => long.Parse(id.ToString()!)).Order().ToArray());
        Assert.Equal(mode == PostMode.ReplaceDelete ? 4 : 0, requests.OfType<DeleteMessageRequest>().Count());
        before = api.Bot.Requests.Count;
        await Tick();
        Assert.Equal(before, api.Bot.Requests.Count);

        async Task<ScheduleTickSummary> Tick()
        {
            var response = await client.PostAsync("/ingest/schedules/tick", null);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<ScheduleTickSummary>(ApiJson.Options))!;
        }
    }
}
