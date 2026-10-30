using System.Net;
using DtekBot.Api.Checks;
using DtekBot.Api.Data;
using DtekBot.Api.Grid;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class GridStatusProviderTests
{
    // The shape of the real feed (data/regions.json of lasercat12/ua-power-status), trimmed.
    private const string Json = """
        {
          "schema_version": 1,
          "generated_at": "2026-10-16T20:00:21Z",
          "changed_at": "2026-10-16T20:00:21Z",
          "statuses": {
            "normal": { "code": 3, "title_uk": "Достатньо електроенергії" },
            "emergency_outages": { "code": 10, "title_uk": "Застосовуються аварійні відключення" }
          },
          "regions": [
            { "id": 31, "slug": "kyiv", "status": "emergency_outages", "title_uk": "Аварійні відключення діють", "since": "2026-10-16T19:59:53Z",
              "period": { "start": "2026-10-16T19:00:00Z", "end": "2026-10-16T22:00:00Z" }, "next_change": null },
            { "id": 9, "slug": "dnipropetrovsk", "status": "normal", "since": "2026-07-01T19:00:02Z", "period": null, "next_change": null },
            { "id": 1, "status": "normal" }
          ]
        }
        """;

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class Options(DtekSettings value) : IOptionsMonitor<DtekSettings>
    {
        public DtekSettings CurrentValue => value;
        public DtekSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<DtekSettings, string?> listener) => null;
    }

    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 16, 23, 0, 0, TimeSpan.FromHours(3)));

    private GridStatusProvider Provider(Handler handler) =>
        new(new Factory(handler), new Options(new DtekSettings()), _clock, NullLogger<GridStatusProvider>.Instance);

    private static HttpResponseMessage Ok(string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Json) };
        if (etag is not null)
        {
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
        }

        return response;
    }

    // ---- parsing ----

    [Fact]
    public async Task RuntimeRefresh_BypassesCache_AndKeepsEtag()
    {
        var calls = 0;
        var handler = new Handler(request =>
        {
            calls++;
            if (calls == 1) return Ok("\"grid-v1\"");
            Assert.Equal("\"grid-v1\"", Assert.Single(request.Headers.IfNoneMatch).Tag);
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });
        var provider = Provider(handler);
        var first = await provider.GetAsync(CancellationToken.None);
        var refreshed = await provider.RefreshAsync(CancellationToken.None);
        Assert.Same(first, refreshed);
        Assert.Equal(2, calls);
        await provider.GetAsync(CancellationToken.None);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Parser_ReadsTheRealShape()
    {
        var feed = GridFeedParser.Parse(Json)!;

        Assert.Equal(new DateTimeOffset(2026, 10, 16, 20, 0, 21, TimeSpan.Zero), feed.GeneratedAt);
        Assert.Equal(2, feed.Regions.Count);
        var kyiv = feed.Regions["kyiv"];
        Assert.Equal("emergency_outages", kyiv.Status);
        Assert.Equal("Аварійні відключення діють", kyiv.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 19, 59, 53, TimeSpan.Zero), kyiv.Since);

        // A region without its own title falls back to the title of its status.
        Assert.Equal("normal", feed.Regions["dnipropetrovsk"].Status);
        Assert.Equal("Достатньо електроенергії", feed.Regions["dnipropetrovsk"].Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"regions": []}""")]
    [InlineData("""{"generated_at": "2026-10-16T20:00:21Z"}""")]
    public void Parser_RejectsAnUnexpectedShape(string json) => Assert.Null(GridFeedParser.Parse(json));

    [Fact]
    public void ConfiguredSlugs_ExistInTheShippedSettings()
    {
        var settings = new DispatcherFixture().Settings;

        Assert.Equal(
            ["dnipropetrovsk", "donetsk", "kyiv", "kyiv-oblast", "odesa"],
            settings.Regions.Select(r => r.GridStatusSlug).Order().ToArray());
        Assert.Equal(45, settings.Grid.MaxAgeMinutes);
        Assert.StartsWith("https://", settings.Grid.FeedUrl);
    }

    // ---- provider ----

    [Fact]
    public async Task Provider_FetchesTheFeed_AndServesItFromTheCacheInsideTheWindow()
    {
        var handler = new Handler(_ => Ok());
        var provider = Provider(handler);

        var first = await provider.GetAsync(CancellationToken.None);
        var second = await provider.GetAsync(CancellationToken.None);

        Assert.NotNull(first);
        Assert.Same(first, second);
        Assert.Single(handler.Requests);
        Assert.Contains("ua-power-status", handler.Requests[0].RequestUri!.ToString());
    }

    [Fact]
    public async Task Provider_RefreshesWithTheETag_AndKeepsTheCopyOnNotModified()
    {
        var calls = 0;
        var handler = new Handler(_ => ++calls == 1 ? Ok("\"v1\"") : new HttpResponseMessage(HttpStatusCode.NotModified));
        var provider = Provider(handler);
        var first = await provider.GetAsync(CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(2));
        var second = await provider.GetAsync(CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("\"v1\"", handler.Requests[1].Headers.IfNoneMatch.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Provider_HttpFailure_ReturnsNull_WithoutThrowing(HttpStatusCode status)
    {
        var provider = Provider(new Handler(_ => new HttpResponseMessage(status)));

        Assert.Null(await provider.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Provider_NetworkFailure_ReturnsNull_WithoutThrowing_AndBacksOffForTheWindow()
    {
        var handler = new Handler(_ => throw new HttpRequestException("down"));
        var provider = Provider(handler);

        Assert.Null(await provider.GetAsync(CancellationToken.None));
        Assert.Null(await provider.GetAsync(CancellationToken.None));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Provider_Garbage_ReturnsNull()
    {
        var provider = Provider(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") }));

        Assert.Null(await provider.GetAsync(CancellationToken.None));
    }

    // ---- "is the power off?" assessment ----

    [Fact]
    public async Task Assessment_TreatsAUkrenergoEmergencyLikeARegionalPopup()
    {
        using var f = new DispatcherFixture();
        var trigger = new FakeWorkflowTrigger();
        var schedules = new FakeScheduleProvider();
        var clock = new TestClock(Kyiv(15, 15));
        var checks = new AddressCheckService(f.Db, f.Bot, trigger, f.Options, clock, NullLogger<AddressCheckService>.Instance, f.CreateMirror(time: clock));
        var service = new OutageAssessmentService(f.Db, checks, schedules, f.Options, clock);
        f.Db.GridStates.Add(new GridState
        {
            RegionId = "kyiv",
            Status = GridStatuses.EmergencyOutages,
            FeedGeneratedAt = clock.Now,
            UpdatedAt = clock.Now,
        });
        f.Db.SaveChanges();
        var channel = f.AddChannel(701, c =>
        {
            c.AddressStreet = "вул. Драгоманова";
            c.AddressHouse = "12";
        });

        var text = await service.AssessAsync(channel, CancellationToken.None);

        Assert.StartsWith("ℹ️ Укренерго повідомляє про екстрені відключення", text);
        Assert.Empty(trigger.Events);
        Assert.Empty(f.Db.AddressChecks);
    }
}
