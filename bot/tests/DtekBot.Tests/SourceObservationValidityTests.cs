using System.Net.Http.Json;
using DtekBot.Api.Data;
using DtekBot.Api.Grid;
using DtekBot.Api.Groups;
using DtekBot.Shared;

namespace DtekBot.Tests;

public sealed class SourceObservationValidityTests
{
    [Fact]
    public async Task UnknownStatusesWithoutPriorEvidence_CreateNoStateOrDelivery()
    {
        using var api = new ApiFactory();
        using var client = IngestClient(api);
        AddSubscriber(api, "45.1");
        await GridAsync(api, client, "unknown", api.Clock.Now);
        await GroupsAsync(client, new() { ["45.1"] = new("unknown", "") });

        api.WithDb(db =>
        {
            Assert.Empty(db.GridStates);
            Assert.Empty(db.GroupStates);
            Assert.Empty(db.Deliveries);
        });
        Assert.Empty(api.Bot.SentTo(9001));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("new_provider_status")]
    [InlineData("")]
    public async Task UnknownGridStatus_PreservesEmergencyAndItsTimestamp(string status)
    {
        using var api = new ApiFactory();
        using var client = IngestClient(api);
        AddSubscriber(api);
        await GridAsync(api, client, GridStatuses.EmergencyOutages, api.Clock.Now);
        await GridAsync(api, client, status, api.Clock.Now.AddMinutes(1));

        api.WithDb(db =>
        {
            var state = Assert.Single(db.GridStates);
            Assert.Equal(GridStatuses.EmergencyOutages, state.Status);
            Assert.Equal(api.Clock.Now, state.FeedGeneratedAt);
            Assert.False(Assert.Single(db.Deliveries).IsClosed);
        });
        Assert.Single(api.Bot.SentTo(9001));
    }

    [Theory]
    [InlineData(GridStatuses.Normal)]
    [InlineData(GridStatuses.Shortage)]
    [InlineData(GridStatuses.OutageSchedules)]
    public async Task RecognizedGridClearance_ClosesEmergency(string status)
    {
        using var api = new ApiFactory();
        using var client = IngestClient(api);
        AddSubscriber(api);
        await GridAsync(api, client, GridStatuses.EmergencyOutages, api.Clock.Now);
        await GridAsync(api, client, status, api.Clock.Now.AddMinutes(1));

        api.WithDb(db => Assert.True(Assert.Single(db.Deliveries).IsClosed));
        Assert.Equal(2, api.Bot.SentTo(9001).Count());
    }

    [Theory]
    [InlineData(GridStatuses.EmergencyOutages, GridStatuses.Normal)]
    [InlineData(GridStatuses.Normal, GridStatuses.EmergencyOutages)]
    public async Task OlderGridSnapshotWithinFreshnessWindow_CannotReverseNewerState(string newer, string older)
    {
        using var api = new ApiFactory();
        using var client = IngestClient(api);
        AddSubscriber(api);
        await GridAsync(api, client, newer, api.Clock.Now);
        var sends = api.Bot.SentTo(9001).Count();
        await GridAsync(api, client, older, api.Clock.Now.AddMinutes(-1));

        api.WithDb(db =>
        {
            var state = Assert.Single(db.GridStates);
            Assert.Equal(newer, state.Status);
            Assert.Equal(api.Clock.Now, state.FeedGeneratedAt);
        });
        Assert.Equal(sends, api.Bot.SentTo(9001).Count());
    }

    [Fact]
    public async Task EqualTimestampGridClearance_RemainsCompatible()
    {
        using var api = new ApiFactory();
        using var client = IngestClient(api);
        AddSubscriber(api);
        await GridAsync(api, client, GridStatuses.EmergencyOutages, api.Clock.Now);
        await GridAsync(api, client, GridStatuses.Normal, api.Clock.Now);

        api.WithDb(db => Assert.True(Assert.Single(db.Deliveries).IsClosed));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("new_provider_status")]
    public async Task UnknownGroupStatus_PreservesEmergencyWhileValidSiblingClears(string status)
    {
        using var api = new ApiFactory();
        using var client = IngestClient(api);
        AddSubscriber(api, "45.1");
        AddSubscriber(api, "2.1", 9002);
        await GroupsAsync(client, new()
        {
            ["45.1"] = new(GroupStatuses.Emergency, ""),
            ["2.1"] = new(GroupStatuses.Emergency, ""),
        });
        await GroupsAsync(client, new()
        {
            ["45.1"] = new(status, ""),
            ["2.1"] = new("ScheduleApplies", ""),
        });

        api.WithDb(db =>
        {
            Assert.True(db.GroupStates.Single(g => g.GroupId == "45.1").IsEmergency);
            Assert.False(db.GroupStates.Single(g => g.GroupId == "2.1").IsEmergency);
            Assert.False(db.Deliveries.Single(d => d.ChatId == 9001).IsClosed);
            Assert.True(db.Deliveries.Single(d => d.ChatId == 9002).IsClosed);
        });
        Assert.Single(api.Bot.SentTo(9001));
        Assert.Equal(2, api.Bot.SentTo(9002).Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ScheduleApplies")]
    [InlineData("Normal")]
    [InlineData("normal")]
    public async Task RecognizedAndLegacyGroupClearance_RemainsCompatible(string? status)
    {
        using var api = new ApiFactory();
        using var client = IngestClient(api);
        AddSubscriber(api, "45.1");
        await GroupsAsync(client, new() { ["45.1"] = new(GroupStatuses.Emergency, "") });
        await GroupsAsync(client, new() { ["45.1"] = new(status, "") });

        api.WithDb(db => Assert.True(Assert.Single(db.Deliveries).IsClosed));
        Assert.Equal(2, api.Bot.SentTo(9001).Count());
    }

    private static HttpClient IngestClient(ApiFactory api)
    {
        var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        return client;
    }

    private static void AddSubscriber(ApiFactory api, string? group = null, long chatId = 9001) =>
        api.WithDb(db => db.Channels.Add(new Channel
        {
            ChatId = chatId, RegionId = "kyiv", GroupId = group, IsActive = true, CreatedAt = api.Clock.Now,
        }));

    private static async Task GridAsync(ApiFactory api, HttpClient client, string status, DateTimeOffset generatedAt)
    {
        api.Grid.Feed = new GridFeed(generatedAt, new Dictionary<string, GridRegionStatus>
        {
            ["kyiv"] = new("kyiv", status, status, null),
        });
        using var response = await client.PostAsync("/ingest/grid/tick", null);
        response.EnsureSuccessStatusCode();
    }

    private static async Task GroupsAsync(HttpClient client, Dictionary<string, GroupStatusEntry> groups)
    {
        using var response = await client.PostAsJsonAsync("/ingest/groups", new GroupsIngestRequest("kyiv", groups), ApiJson.Options);
        response.EnsureSuccessStatusCode();
    }
}
