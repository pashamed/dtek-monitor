using System.Net;
using System.Net.Http.Headers;
using System.Text;
using DtekBot.Api.Schedules;
using Microsoft.Extensions.Logging.Abstractions;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class ScheduleProviderTests : IDisposable
{
    private const string KyivUrl = "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/kyiv.json";

    private sealed class RecordedRequest(HttpRequestMessage request)
    {
        public string Url { get; } = request.RequestUri!.AbsoluteUri;
        public HttpMethod Method { get; } = request.Method;
        public string? IfNoneMatch { get; } = request.Headers.TryGetValues("If-None-Match", out var v) ? string.Join(",", v) : null;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _script = new();
        private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? _fallback;

        public List<RecordedRequest> Requests { get; } = [];

        public void Enqueue(Func<HttpResponseMessage> response) =>
            _script.Enqueue((_, _) => Task.FromResult(response()));

        public void Enqueue(Exception exception) =>
            _script.Enqueue((_, _) => throw exception);

        public void Always(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) => _fallback = response;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(request));
            if (_script.Count > 0)
            {
                return _script.Dequeue()(request, cancellationToken);
            }

            return _fallback is not null
                ? _fallback(request, cancellationToken)
                : throw new InvalidOperationException("Unexpected HTTP request: " + request.RequestUri);
        }
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public List<string> Names { get; } = [];

        public HttpClient CreateClient(string name)
        {
            Names.Add(name);
            return new HttpClient(handler, disposeHandler: false);
        }
    }

    private readonly DispatcherFixture _f = new();
    private readonly TestClock _clock = new(Kyiv(15, 12));
    private readonly ScriptedHandler _handler = new();
    private readonly FakeHttpClientFactory _factory;
    private readonly ScheduleProvider _provider;

    public ScheduleProviderTests()
    {
        _factory = new FakeHttpClientFactory(_handler);
        _provider = new ScheduleProvider(_factory, _f.Options, _clock, NullLogger<ScheduleProvider>.Instance);
    }

    public void Dispose()
    {
        _f.Dispose();
        _handler.Dispose();
    }

    private static HttpResponseMessage Ok(string? json = null, string? etag = "\"etag-1\"")
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json ?? RealJson(), Encoding.UTF8, "text/plain"),
        };
        if (etag is not null)
        {
            response.Headers.ETag = new EntityTagHeaderValue(etag);
        }

        return response;
    }

    private static HttpResponseMessage Status(HttpStatusCode code) => new(code);

    private static string JsonWithVersion(string version) =>
        Json(version, "01.01.2026 10:00", (Day1, "45.1", Hours("no", 12)));

    // ---- basic fetching ----

    [Fact]
    public async Task FirstCall_FetchesTheDataFile_AndParsesIt()
    {
        _handler.Enqueue(() => Ok());

        var snapshot = await _provider.GetAsync("kyiv", CancellationToken.None);

        var request = Assert.Single(_handler.Requests);
        Assert.Equal(KyivUrl, request.Url);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Null(request.IfNoneMatch);
        Assert.Equal([ScheduleProvider.ClientName], _factory.Names);
        Assert.Equal("outage-data", ScheduleProvider.ClientName);

        Assert.NotNull(snapshot);
        Assert.Equal("kyiv", snapshot.DataFile);
        Assert.Equal(RealVersion, snapshot.Version);
        Assert.Equal(_clock.Now, snapshot.FetchedAt);
        Assert.Contains("45.1", snapshot.GroupIds);
    }

    [Theory]
    [InlineData("kyiv-oblast", "kyiv-region")]
    [InlineData("dnipropetrovsk", "dnipro")]
    [InlineData("odesa", "odesa")]
    public async Task EachRegion_UsesItsOwnDataFile(string regionId, string file)
    {
        _handler.Enqueue(() => Ok());

        var snapshot = await _provider.GetAsync(regionId, CancellationToken.None);

        Assert.Equal(
            $"https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/data/{file}.json",
            Assert.Single(_handler.Requests).Url);
        Assert.Equal(file, snapshot!.DataFile);
    }

    [Fact]
    public async Task SecondCall_InsideCacheWindow_DoesNotHitTheNetwork()
    {
        _handler.Enqueue(() => Ok());
        var first = await _provider.GetAsync("kyiv", CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));
        var second = await _provider.GetAsync("kyiv", CancellationToken.None);

        Assert.Single(_handler.Requests);
        Assert.Same(first, second);
    }

    [Fact]
    public async Task CallAtExactlyTheTtl_Refreshes()
    {
        _handler.Enqueue(() => Ok());
        _handler.Enqueue(() => Status(HttpStatusCode.NotModified));
        await _provider.GetAsync("kyiv", CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(5));
        await _provider.GetAsync("kyiv", CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task DifferentRegions_AreCachedSeparately()
    {
        _handler.Always((_, _) => Task.FromResult(Ok()));

        await _provider.GetAsync("kyiv", CancellationToken.None);
        await _provider.GetAsync("odesa", CancellationToken.None);
        await _provider.GetAsync("kyiv", CancellationToken.None);
        await _provider.GetAsync("odesa", CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
    }

    // ---- conditional requests ----

    [Fact]
    public async Task AfterTheTtl_SendsIfNoneMatch_And304KeepsTheSnapshot_WithoutReparsing()
    {
        _handler.Enqueue(() => Ok(etag: "\"abc123\""));
        var first = await _provider.GetAsync("kyiv", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Enqueue(() => Status(HttpStatusCode.NotModified));

        var second = await _provider.GetAsync("kyiv", CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
        Assert.Equal("\"abc123\"", _handler.Requests[1].IfNoneMatch);
        Assert.NotNull(second);
        Assert.Same(first!.Groups, second.Groups);
        Assert.Equal(first.Version, second.Version);
        Assert.Equal(_clock.Now, second.FetchedAt);
    }

    [Fact]
    public async Task After304_TheCacheWindowRestarts()
    {
        _handler.Enqueue(() => Ok());
        await _provider.GetAsync("kyiv", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Enqueue(() => Status(HttpStatusCode.NotModified));
        await _provider.GetAsync("kyiv", CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(3));
        await _provider.GetAsync("kyiv", CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task FirstResponseWithoutETag_ThenRefresh_SendsNoConditionalHeader()
    {
        _handler.Enqueue(() => Ok(etag: null));
        await _provider.GetAsync("kyiv", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Enqueue(() => Ok(etag: null));

        await _provider.GetAsync("kyiv", CancellationToken.None);

        Assert.Null(_handler.Requests[1].IfNoneMatch);
    }

    [Fact]
    public async Task Fresh200_ReplacesTheSnapshot_AndStoresTheNewETag()
    {
        _handler.Enqueue(() => Ok(etag: "\"old\""));
        var first = await _provider.GetAsync("kyiv", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Enqueue(() => Ok(JsonWithVersion("new-version-hash"), "\"new\""));

        var second = await _provider.GetAsync("kyiv", CancellationToken.None);

        Assert.Equal("new-version-hash", second!.Version);
        Assert.NotEqual(first!.Version, second.Version);
        Assert.Equal(_clock.Now, second.FetchedAt);

        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Enqueue(() => Status(HttpStatusCode.NotModified));
        await _provider.GetAsync("kyiv", CancellationToken.None);
        Assert.Equal("\"new\"", _handler.Requests[2].IfNoneMatch);
    }

    // ---- failures after a good fetch ----

    public static IEnumerable<object[]> Failures() =>
    [
        [new Func<HttpResponseMessage>(() => Status(HttpStatusCode.InternalServerError)), "500"],
        [new Func<HttpResponseMessage>(() => Status(HttpStatusCode.NotFound)), "404"],
        [new Func<HttpResponseMessage>(() => Status(HttpStatusCode.TooManyRequests)), "429"],
        [new Func<HttpResponseMessage>(() => Ok("{not json")), "malformed"],
        [new Func<HttpResponseMessage>(() => Ok("{}")), "unexpected shape"],
        [new Func<HttpResponseMessage>(() => Ok("")), "empty body"],
    ];

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task FailedRefresh_KeepsServingTheStaleSnapshot_UntilMaxStale(Func<HttpResponseMessage> failure, string label)
    {
        _ = label;
        _handler.Enqueue(() => Ok());
        var good = await _provider.GetAsync("kyiv", CancellationToken.None);
        var fetchedAt = good!.FetchedAt;

        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Enqueue(failure);
        var stale = await _provider.GetAsync("kyiv", CancellationToken.None);

        Assert.NotNull(stale);
        Assert.Equal(fetchedAt, stale.FetchedAt);
        Assert.Equal(good.Version, stale.Version);

        // Exactly at MaxStaleMinutes (120) it is still served ...
        _clock.Now = fetchedAt.AddMinutes(120);
        _handler.Enqueue(failure);
        Assert.NotNull(await _provider.GetAsync("kyiv", CancellationToken.None));

        // ... and once it is older (and a new refresh attempt is due) it is not.
        _clock.Now = fetchedAt.AddMinutes(126);
        _handler.Enqueue(failure);
        Assert.Null(await _provider.GetAsync("kyiv", CancellationToken.None));
    }

    [Fact]
    public async Task WellFormedJsonOfTheWrongShape_IsTreatedAsAFailedRefresh()
    {
        _handler.Enqueue(() => Ok("[]"));

        Assert.Null(await _provider.GetAsync("kyiv", CancellationToken.None));
    }

    [Fact]
    public async Task SnapshotOlderThanMaxStale_IsNotServed_EvenInsideTheBackoffWindowOfAFailedRefresh()
    {
        _handler.Enqueue(() => Ok());
        var good = await _provider.GetAsync("kyiv", CancellationToken.None);
        _clock.Now = good!.FetchedAt.AddMinutes(120);
        _handler.Enqueue(() => Status(HttpStatusCode.InternalServerError));
        Assert.NotNull(await _provider.GetAsync("kyiv", CancellationToken.None));

        _clock.Now = good.FetchedAt.AddMinutes(121);

        Assert.Null(await _provider.GetAsync("kyiv", CancellationToken.None));
    }

    [Fact]
    public async Task NetworkError_AfterAGoodFetch_KeepsServingTheStaleSnapshot_ThenNull()
    {
        _handler.Enqueue(() => Ok());
        var good = await _provider.GetAsync("kyiv", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Enqueue(new HttpRequestException("connection refused"));

        Assert.NotNull(await _provider.GetAsync("kyiv", CancellationToken.None));

        _clock.Now = good!.FetchedAt.AddMinutes(130);
        _handler.Enqueue(new HttpRequestException("connection refused"));
        Assert.Null(await _provider.GetAsync("kyiv", CancellationToken.None));
    }

    [Fact]
    public async Task Timeout_AfterAGoodFetch_KeepsServingTheStaleSnapshot()
    {
        _handler.Enqueue(() => Ok());
        await _provider.GetAsync("kyiv", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Enqueue(new TaskCanceledException("timed out"));

        Assert.NotNull(await _provider.GetAsync("kyiv", CancellationToken.None));
    }

    [Fact]
    public async Task FailedRefresh_BacksOffForOneCacheWindow()
    {
        _handler.Enqueue(() => Ok());
        await _provider.GetAsync("kyiv", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Enqueue(() => Status(HttpStatusCode.InternalServerError));
        await _provider.GetAsync("kyiv", CancellationToken.None);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await _provider.GetAsync("kyiv", CancellationToken.None);
        await _provider.GetAsync("kyiv", CancellationToken.None);

        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task RecoveryAfterFailure_ReplacesTheStaleSnapshot()
    {
        _handler.Enqueue(() => Ok());
        await _provider.GetAsync("kyiv", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Enqueue(() => Status(HttpStatusCode.BadGateway));
        await _provider.GetAsync("kyiv", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(6));
        _handler.Enqueue(() => Ok(JsonWithVersion("recovered")));

        var snapshot = await _provider.GetAsync("kyiv", CancellationToken.None);

        Assert.Equal("recovered", snapshot!.Version);
        Assert.Equal(_clock.Now, snapshot.FetchedAt);
    }

    // ---- first-ever failure ----

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task FirstEverFailure_ReturnsNull(Func<HttpResponseMessage> failure, string label)
    {
        _ = label;
        _handler.Enqueue(failure);

        Assert.Null(await _provider.GetAsync("kyiv", CancellationToken.None));
    }

    [Fact]
    public async Task FirstEverNetworkError_ReturnsNull_WithoutThrowing()
    {
        _handler.Enqueue(new HttpRequestException("dns failure"));

        Assert.Null(await _provider.GetAsync("kyiv", CancellationToken.None));
    }

    [Fact]
    public async Task FirstEverFailure_DoesNotRetryOnEveryCall_InsideTheCacheWindow()
    {
        _handler.Enqueue(() => Status(HttpStatusCode.InternalServerError));

        Assert.Null(await _provider.GetAsync("kyiv", CancellationToken.None));
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Null(await _provider.GetAsync("kyiv", CancellationToken.None));
        Assert.Null(await _provider.GetAsync("kyiv", CancellationToken.None));

        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task FirstEverFailure_RetriesAfterTheCacheWindow()
    {
        _handler.Enqueue(() => Status(HttpStatusCode.InternalServerError));
        await _provider.GetAsync("kyiv", CancellationToken.None);
        _clock.Advance(TimeSpan.FromMinutes(5));
        _handler.Enqueue(() => Ok());

        Assert.NotNull(await _provider.GetAsync("kyiv", CancellationToken.None));
        Assert.Equal(2, _handler.Requests.Count);
    }

    [Fact]
    public async Task FirstEverNetworkError_DoesNotRetryOnEveryCall_InsideTheCacheWindow()
    {
        _handler.Enqueue(new HttpRequestException("dns failure"));

        await _provider.GetAsync("kyiv", CancellationToken.None);
        await _provider.GetAsync("kyiv", CancellationToken.None);
        await _provider.GetAsync("kyiv", CancellationToken.None);

        Assert.Single(_handler.Requests);
    }

    // ---- unknown regions ----

    [Theory]
    [InlineData("atlantis")]
    [InlineData("donetsk")]
    [InlineData("")]
    [InlineData("KYIV")]
    public async Task UnknownRegion_OrRegionWithoutDataFile_ReturnsNull_WithoutAnyHttpCall(string regionId)
    {
        Assert.Null(await _provider.GetAsync(regionId, CancellationToken.None));

        Assert.Empty(_handler.Requests);
        Assert.Empty(_factory.Names);
    }

    // ---- concurrency ----

    [Fact]
    public async Task ConcurrentCallers_CauseExactlyOneRequest()
    {
        _handler.Always(async (_, _) =>
        {
            await Task.Delay(100);
            return Ok();
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _provider.GetAsync("kyiv", CancellationToken.None)));

        Assert.Single(_handler.Requests);
        Assert.All(results, r => Assert.NotNull(r));
        Assert.Single(results.Distinct());
    }

    [Fact]
    public async Task ConcurrentCallers_AfterAFailure_StillCauseOneRequest()
    {
        _handler.Always(async (_, _) =>
        {
            await Task.Delay(100);
            return Status(HttpStatusCode.InternalServerError);
        });

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => _provider.GetAsync("kyiv", CancellationToken.None)));

        Assert.Single(_handler.Requests);
        Assert.All(results, r => Assert.Null(r));
    }

    // ---- cancellation ----

    [Fact]
    public async Task CancelledToken_DuringTheRequest_Propagates()
    {
        using var cts = new CancellationTokenSource();
        _handler.Always(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return Ok();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.GetAsync("kyiv", cts.Token));
    }

    [Fact]
    public async Task CancelledToken_WithoutCachedData_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        _handler.Always((_, _) => Task.FromResult(Ok()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _provider.GetAsync("kyiv", cts.Token));
    }
}
