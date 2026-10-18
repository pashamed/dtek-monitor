using System.Net;
using System.Text;
using DtekBot.Api.Groups;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace DtekBot.Tests;

public sealed class DtekAddressClientTests
{
    private const string SiteUrl = "https://www.dtek-odesa.com.ua/ua/";
    private const string StreetDirectory =
        """{"result":true,"streets":{"м. Одеса":["вул. Дерибасівська","вул. Приморська"],"м. Чорноморськ":["вул. Морська"]}}""";
    private const string Houses =
        """{"result":true,"data":{"12":{"sub_type_reason":["GPV6.2","other"]},"12А":{"sub_type_reason":["GPV3.1"]},"14":{"sub_type_reason":[]}}}""";

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return await respond(request, ct);
        }
    }

    private static RegionSettings Region() => new()
    {
        Id = "odesa",
        Name = "Одеська область",
        SiteUrl = SiteUrl,
    };

    private static HttpResponseMessage Page()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                """<html><head><meta name="csrf-token" content="test-token"></head></html>""",
                Encoding.UTF8,
                "text/html"),
        };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "dtek_session=test-cookie; Path=/; HttpOnly");
        return response;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static DtekAddressClient Create(FakeHandler handler) =>
        new(
            new HttpClient(handler),
            new MemoryCache(new MemoryCacheOptions()),
            NullLogger<DtekAddressClient>.Instance);

    [Fact]
    public async Task SearchesCityAndStreetDirectory_ThenParsesHouseQueuesAndSendsCsrfAndCookie()
    {
        var bodies = new List<string>();
        var handler = new FakeHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Page();
            }

            var body = await request.Content!.ReadAsStringAsync();
            bodies.Add(body);
            return request.RequestUri!.AbsolutePath == "/ua/ajax"
                ? Json(body.Contains("getHomeNum", StringComparison.Ordinal) ? Houses : StreetDirectory)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var client = Create(handler);
        var region = Region();

        var cities = await client.SearchCitiesAsync(region, "Одеса", CancellationToken.None);
        var streets = await client.SearchStreetsAsync(region, "м. Одеса", "Дерибас", CancellationToken.None);
        var houses = await client.SearchHousesAsync(region, "м. Одеса", "вул. Дерибасівська", "12", CancellationToken.None);

        Assert.Equal(["м. Одеса"], cities);
        Assert.Equal(["вул. Дерибасівська"], streets);
        Assert.Equal(
            [new DtekBot.Shared.DtekAddressHouseDto("12", "6.2"), new DtekBot.Shared.DtekAddressHouseDto("12А", "3.1")],
            houses);
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Get));
        var ajax = handler.Requests.Where(request => request.Method == HttpMethod.Post).ToList();
        Assert.Equal(2, ajax.Count);
        Assert.All(ajax, request =>
        {
            Assert.Equal("test-token", Assert.Single(request.Headers.GetValues("X-CSRF-Token")));
            Assert.Contains("dtek_session=test-cookie", Assert.Single(request.Headers.GetValues("Cookie")));
        });
        Assert.Contains("data%5B0%5D%5Bvalue%5D=%D0%BC.+%D0%9E%D0%B4%D0%B5%D1%81%D0%B0",
            bodies.Last());
    }

    [Fact]
    public async Task MissingCsrfToken_ThrowsExplicitUnavailableError()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>challenge</html>"),
        }));
        var client = Create(handler);

        var error = await Assert.ThrowsAsync<DtekAddressUnavailableException>(
            () => client.SearchCitiesAsync(Region(), "Одеса", CancellationToken.None));

        Assert.Contains("тимчасово недоступний", error.Message);
    }
}
