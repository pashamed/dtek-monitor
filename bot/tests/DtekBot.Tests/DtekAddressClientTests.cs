using System.Net;
using System.Text;
using DtekBot.Api.Data;
using DtekBot.Api.Groups;
using DtekBot.Api.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace DtekBot.Tests;

public sealed class DtekAddressClientTests
{
    private const string SiteUrl = "https://www.dtek-odesa.com.ua/ua/";
    private const string StreetDirectory =
        """{"result":true,"streets":{"м. Одеса":["вул. Дерибасівська","вул. Приморська"],"м. Чорноморськ":["вул. Морська"],"г. Ирпень":["вул. Центральна"]}}""";
    private const string Houses =
        """{"result":true,"data":{"12":{"sub_type_reason":["GPV6.2","other"]},"12А":{"sub_type_reason":["GPV3.1"]},"12Б":{"sub_type_reason":["GPV 2.2"]},"14":{"sub_type_reason":[]}}}""";

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

    private sealed class TestDatabase : IDisposable
    {
        private readonly SqliteConnection _connection = new("DataSource=:memory:");

        public BotDbContext Context { get; }

        public TestDatabase()
        {
            _connection.Open();
            var options = new DbContextOptionsBuilder<BotDbContext>()
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, UtcTicksModelCustomizer>()
                .Options;
            Context = new BotDbContext(options);
            Context.Database.EnsureCreated();
        }

        public void Dispose()
        {
            Context.Dispose();
            _connection.Dispose();
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
                """<html><head><meta name="csrf-token" content="test-token"></head><body><input type="hidden" name="updateFact" value="18.10.2026 22:42"></body></html>""",
                Encoding.UTF8,
                "text/html"),
        };
        response.Headers.TryAddWithoutValidation("Set-Cookie", "dtek_session=test-cookie; Path=/; HttpOnly");
        return response;
    }

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static DtekAddressClient Create(FakeHandler handler, BotDbContext db) =>
        new(
            new HttpClient(handler),
            new MemoryCache(new MemoryCacheOptions()),
            db,
            TimeProvider.System,
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
        using var database = new TestDatabase();
        var client = Create(handler, database.Context);
        var region = Region();

        var cities = await client.SearchCitiesAsync(region, "Одеса", CancellationToken.None);
        var streets = await client.SearchStreetsAsync(region, "м. Одеса", "Дерибас", CancellationToken.None);
        var houses = await client.SearchHousesAsync(region, "м. Одеса", "вул. Дерибасівська", "12", CancellationToken.None);

        Assert.Equal(["м. Одеса"], cities);
        Assert.Equal(["вул. Дерибасівська"], streets);
        Assert.Equal(["г. Ирпень"], await client.SearchCitiesAsync(region, "Ірпінь", CancellationToken.None));
        Assert.Equal(
            [
                new DtekBot.Shared.DtekAddressHouseDto("12", "6.2"),
                new DtekBot.Shared.DtekAddressHouseDto("12А", "3.1"),
                new DtekBot.Shared.DtekAddressHouseDto("12Б", "2.2"),
            ],
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
        var houseRequest = WebUtility.UrlDecode(bodies.Last());
        Assert.Contains("data[1][name]=street&data[1][value]=вул. Дерибасівська", houseRequest);
        Assert.DoesNotContain("house_num", houseRequest);
        Assert.Contains("data[2][name]=updateFact&data[2][value]=18.10.2026 22:42", houseRequest);
    }

    [Fact]
    public async Task SearchesKyivOblastHouses_AndParsesKremQueueReasonShape()
    {
        const string kremHouses =
            """{"result":true,"data":{"8":{"sub_type_reason":["GPV3.2"]},"14":{"sub_type_reason":["GPV3.2"]},"10/139":{"sub_type_reason":["GPV3.2"]},"4":{"sub_type_reason":["GPV3.2"]},"10/140":{"sub_type_reason":["GPV3.2"]},"10/156":{"sub_type_reason":["GPV3.2"]},"10/144/2":{"sub_type_reason":["GPV3.2"]},"12":{"sub_type_reason":["GPV3.2"]},"10/148-1":{"sub_type_reason":["GPV3.2"]},"10":{"sub_type_reason":["GPV3.2"]},"6":{"sub_type_reason":["GPV3.2"]},"10/143":{"sub_type_reason":["GPV3.2"]},"2":{"sub_type_reason":["GPV3.2"]}}}""";
        string? requestBody = null;
        var handler = new FakeHandler(async (request, _) =>
        {
            if (request.Method == HttpMethod.Get)
            {
                return Page();
            }

            requestBody = await request.Content!.ReadAsStringAsync();
            return Json(kremHouses);
        });
        using var database = new TestDatabase();
        var client = Create(handler, database.Context);
        var region = new RegionSettings
        {
            Id = "kyiv-oblast",
            Name = "Київська область",
            SiteUrl = "https://www.dtek-krem.com.ua/ua/",
        };

        var houses = await client.SearchHousesAsync(
            region, "м. Ірпінь", "вул. Західна", "2", CancellationToken.None);

        Assert.Equal(
            [
                new DtekBot.Shared.DtekAddressHouseDto("10/144/2", "3.2"),
                new DtekBot.Shared.DtekAddressHouseDto("12", "3.2"),
                new DtekBot.Shared.DtekAddressHouseDto("2", "3.2"),
            ],
            houses);
        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post);
        var body = WebUtility.UrlDecode(requestBody);
        Assert.Contains("method=getHomeNum", body);
        Assert.Contains("data[0][name]=city&data[0][value]=м. Ірпінь", body);
        Assert.Contains("data[1][name]=street&data[1][value]=вул. Західна", body);
        Assert.DoesNotContain("house_num", body);
        Assert.Contains("data[2][name]=updateFact&data[2][value]=18.10.2026 22:42", body);
    }

    [Fact]
    public async Task ImportedDirectory_ServesCitiesAndStreetsWithoutRequestingBlockedSite()
    {
        var handler = new FakeHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        using var database = new TestDatabase();
        var client = Create(handler, database.Context);
        var region = new RegionSettings
        {
            Id = "kyiv-oblast",
            Name = "Київська область",
            SiteUrl = "https://www.dtek-krem.com.ua/ua/shutdowns",
        };
        await client.ImportDirectoryAsync(region, new Dictionary<string, IReadOnlyList<string>>
        {
            ["м. Ірпінь"] = ["вул. Західна", "вул. Центральна"],
        }, CancellationToken.None);

        Assert.Equal(["м. Ірпінь"], await client.SearchCitiesAsync(region, "Ірпінь", CancellationToken.None));
        Assert.Equal(["вул. Західна"], await client.SearchStreetsAsync(region, "м. Ірпінь", "Західна", CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DirectorySurvivesClientRestartAndUsesPersistedCacheWhenDtekIsBlocked()
    {
        using var database = new TestDatabase();
        var importer = Create(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))), database.Context);
        await importer.ImportDirectoryAsync(Region(), new Dictionary<string, IReadOnlyList<string>>
        {
            ["м. Ірпінь"] = ["вул. Західна"],
        }, CancellationToken.None);

        var blockedHandler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var restartedClient = Create(blockedHandler, database.Context);

        Assert.Equal(["м. Ірпінь"], await restartedClient.SearchCitiesAsync(Region(), "Ірпінь", CancellationToken.None));
        Assert.Equal(["вул. Західна"], await restartedClient.SearchStreetsAsync(
            Region(), "м. Ірпінь", "Західна", CancellationToken.None));
        Assert.Empty(blockedHandler.Requests);
    }

    [Fact]
    public async Task MissingCsrfToken_ThrowsExplicitUnavailableError()
    {
        var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>challenge</html>"),
        }));
        using var database = new TestDatabase();
        var client = Create(handler, database.Context);

        var error = await Assert.ThrowsAsync<DtekAddressUnavailableException>(
            () => client.SearchCitiesAsync(Region(), "Одеса", CancellationToken.None));

        Assert.Contains("тимчасово недоступний", error.Message);
    }
    [Fact]
    public async Task HouseWithMultipleGpvReasonsPreservesAllDistinctGroups()
    {
        var handler = new FakeHandler((request, _) => Task.FromResult(request.Method == HttpMethod.Get ? Page()
            : Json("""{"result":true,"data":{"7":{"sub_type_reason":["GPV3.2","GPV4.1","GPV3.2","other"]}}}""")));
        using var database = new TestDatabase();
        var client = Create(handler, database.Context);
        var houses = await client.SearchHousesAsync(Region(), "місто", "вулиця", "7", default);
        Assert.Equal(2, houses.Count);
        Assert.All(houses, house => Assert.Equal(new[] { "3.2", "4.1" }, house.GroupIds));
    }

}
