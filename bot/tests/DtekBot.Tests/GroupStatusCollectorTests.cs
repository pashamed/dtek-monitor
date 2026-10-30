using System.Net;
using System.Net.Http.Json;
using DtekBot.Api.Data;
using DtekBot.Api.Groups;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DtekBot.Tests;

public sealed class GroupStatusCollectorTests
{
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(GroupStatusCollector.ClientName, name);
            return new(handler, disposeHandler: false);
        }
    }

    private sealed class Options(DtekSettings settings) : IOptionsMonitor<DtekSettings>
    {
        public DtekSettings CurrentValue => settings;
        public DtekSettings Get(string? name) => settings;
        public IDisposable? OnChange(Action<DtekSettings, string?> listener) => null;
    }

    [Fact]
    public async Task ConfiguredRegionsOnly_AreCollectedOnEveryApiCycle_WithRawStatuses()
    {
        var requests = new List<string>();
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var handler = new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"45.1":{"today":{"status":"EmergencyShutdowns"},"tomorrow":{"status":"ScheduleApplies"}},
                     "2.1":{"today":{"status":"new_status"}},
                     "3.1":{"today":{}},"4.1":{"today":{"status":""}}}
                    """),
            });
        });
        var settings = RealSettings.Load();
        var collector = new GroupStatusCollector(new Factory(handler), new Options(settings), clock, NullLogger<GroupStatusCollector>.Instance);

        var first = await collector.CollectAsync(CancellationToken.None);
        var second = await collector.CollectAsync(CancellationToken.None);

        var observation = Assert.Single(first);
        Assert.Equal("kyiv", observation.RegionId);
        Assert.Equal(clock.Now, observation.ObservedAt);
        Assert.Equal("EmergencyShutdowns", observation.Groups["45.1"].Today);
        Assert.Equal("ScheduleApplies", observation.Groups["45.1"].Tomorrow);
        Assert.Equal("new_status", observation.Groups["2.1"].Today);
        Assert.Equal(2, observation.Groups.Count);
        Assert.Single(second);
        Assert.Equal(2 * settings.Regions.Count(r => r.HasGroups), requests.Count);
        Assert.All(requests, url => Assert.Equal(
            "https://app.yasno.ua/api/blackout-service/public/shutdowns/regions/25/dsos/902/planned-outages", url));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("<html>challenge</html>")]
    [InlineData("""{"45.1":{"today":{"status":null}}}""")]
    public async Task EmptyOrMalformedFeed_IsUnavailable_NotClearance(string json)
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json),
        }));
        var collector = Collector(handler);
        Assert.Empty(await collector.CollectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task HttpFailure_IsUnavailable()
    {
        using var handler = new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        Assert.Empty(await Collector(handler).CollectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task NetworkFailure_IsUnavailable()
    {
        using var handler = new Handler((_, _) => throw new HttpRequestException("offline"));
        Assert.Empty(await Collector(handler).CollectAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var handler = new Handler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Collector(handler).CollectAsync(cancellation.Token));
    }

    [Fact]
    public async Task GroupIngest_RejectsOlderObservation_AndRefreshesUnchangedObservation()
    {
        using var api = new ApiFactory();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        api.WithDb(db => db.Channels.Add(new Channel
        {
            ChatId = 9001, RegionId = "kyiv", GroupId = "45.1", IsActive = true,
        }));
        await Ingest(GroupStatuses.Emergency, api.Clock.Now.AddMinutes(-2));
        await Ingest(GroupStatuses.Emergency, api.Clock.Now);
        await Ingest("ScheduleApplies", api.Clock.Now.AddMinutes(-1));
        api.WithDb(db =>
        {
            Assert.Equal(api.Clock.Now, Assert.Single(db.GroupStates).UpdatedAt);
            Assert.True(Assert.Single(db.GroupStates).IsEmergency);
            Assert.False(Assert.Single(db.Deliveries).IsClosed);
        });
        Assert.Single(api.Bot.SentTo(9001));
        await Ingest("ScheduleApplies", api.Clock.Now.AddMinutes(1));
        api.WithDb(db => Assert.True(Assert.Single(db.Deliveries).IsClosed));

        async Task Ingest(string status, DateTimeOffset observedAt)
        {
            using var response = await client.PostAsJsonAsync("/ingest/groups", new GroupsIngestRequest(
                "kyiv", new() { ["45.1"] = new(status, "") }, observedAt), ApiJson.Options);
            response.EnsureSuccessStatusCode();
            var result = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.Equal(1, result.GetProperty("followed").GetInt32());
        }
    }

    private static GroupStatusCollector Collector(HttpMessageHandler handler) =>
        new(new Factory(handler), new Options(RealSettings.Load()), TimeProvider.System, NullLogger<GroupStatusCollector>.Instance);
}
