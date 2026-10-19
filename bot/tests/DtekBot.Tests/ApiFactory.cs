using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using DtekBot.Api.Data;
using DtekBot.Api.Groups;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Telegram.Bot;

namespace DtekBot.Tests;

// The real application in-process (Program), with SQLite in memory, a fake Telegram client and a fake schedule provider.
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public const string BotToken = "123456:API-TEST-TOKEN";
    public const string IngestSecret = "test-ingest-secret";

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private long _nextId = 10_000;

    public FakeTelegramBotClient Bot { get; } = new();

    public FakeScheduleProvider Schedules { get; } = new();

    public FakeDtekAddressClient DtekAddresses { get; } = new();

    public FakeYasnoClient Yasno { get; } = new();

    public FakeGridProvider Grid { get; } = new();

    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.FromHours(3)));

    public FakeSvitlobotHandler Svitlobot { get; } = new();

    // base64 of 32 bytes; set to null to play a server without the Svitlobot setting.
    public string? SvitlobotEncryptionKey { get; set; } = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.UseContentRoot(AppContext.BaseDirectory);
        builder.UseSetting("Bot:Token", BotToken);
        builder.UseSetting("App:PublicUrl", "https://example.test");
        builder.UseSetting("Ingest:Secret", IngestSecret);

        // The built-in tick loop would post on its own in the middle of a test.
        builder.UseSetting("Ticks:Enabled", "false");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<BotDbContext>>();
            services.RemoveAll<BotDbContext>();
            services.RemoveAll(typeof(IDbContextOptionsConfiguration<BotDbContext>));
            services.AddDbContext<BotDbContext>(o => o
                .UseSqlite(_connection)
                .ReplaceService<IModelCustomizer, UtcTicksModelCustomizer>());

            services.RemoveAll<ITelegramBotClient>();
            services.AddSingleton<ITelegramBotClient>(Bot);
            services.RemoveAll<IScheduleProvider>();
            services.AddSingleton<IScheduleProvider>(Schedules);
            services.RemoveAll<IDtekAddressClient>();
            services.AddSingleton<IDtekAddressClient>(DtekAddresses);
            services.RemoveAll<IYasnoClient>();
            services.AddSingleton<IYasnoClient>(Yasno);
            services.RemoveAll<DtekBot.Api.Grid.IGridStatusProvider>();
            services.AddSingleton<DtekBot.Api.Grid.IGridStatusProvider>(Grid);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);

            services.AddHttpClient<DtekBot.Api.Svitlobot.ISvitlobotClient, DtekBot.Api.Svitlobot.SvitlobotClient>()
                .ConfigurePrimaryHttpMessageHandler(() => Svitlobot);
            services.RemoveAll<DtekBot.Api.Svitlobot.SvitlobotKeyProtector>();
            services.AddSingleton(new DtekBot.Api.Svitlobot.SvitlobotKeyProtector(() => SvitlobotEncryptionKey));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<BotDbContext>().Database.EnsureCreated();
        return host;
    }

    // Unique ids keep tests of one class independent of each other inside the shared database.
    public long NextId() => Interlocked.Increment(ref _nextId);

    public T WithDb<T>(Func<BotDbContext, T> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var result = action(db);
        db.SaveChanges();
        return result;
    }

    public void WithDb(Action<BotDbContext> action) => WithDb<int>(db => { action(db); return 0; });

    public HttpClient ClientFor(long userId, string? token = BotToken, DateTimeOffset? authDate = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("tma", InitData(userId, token!, authDate ?? Clock.Now));
        return client;
    }

    public static string InitData(long userId, string token, DateTimeOffset authDate)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = authDate.ToUnixTimeSeconds().ToString(),
            ["query_id"] = "AAH-test",
            ["user"] = $"{{\"id\":{userId},\"first_name\":\"T\"}}",
        };
        var secret = HMACSHA256.HashData(Encoding.UTF8.GetBytes("WebAppData"), Encoding.UTF8.GetBytes(token));
        var hash = Convert.ToHexString(HMACSHA256.HashData(secret, Encoding.UTF8.GetBytes(
            string.Join('\n', fields.Select(kv => $"{kv.Key}={kv.Value}"))))).ToLowerInvariant();
        return string.Join('&', fields.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}")) + "&hash=" + hash;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _connection.Dispose();
        }
    }
}

public sealed class FakeDtekAddressClient : IDtekAddressClient
{
    public IReadOnlyList<string> Cities { get; set; } = ["м. Одеса"];
    public IReadOnlyList<string> Streets { get; set; } = ["вул. Дерибасівська"];
    public IReadOnlyList<DtekAddressHouseDto> Houses { get; set; } = [new("12", null)];
    public IReadOnlyDictionary<string, IReadOnlyList<string>>? ImportedDirectory { get; private set; }

    public (string Region, string City, string Query)? LastCitySearch { get; private set; }
    public (string Region, string City, string Query)? LastStreetSearch { get; private set; }
    public (string Region, string City, string Street, string Query)? LastHouseSearch { get; private set; }

    public Task<IReadOnlyList<string>> SearchCitiesAsync(RegionSettings region, string query, CancellationToken ct)
    {
        LastCitySearch = (region.Id, "", query);
        return Task.FromResult(Cities);
    }

    public Task<IReadOnlyList<string>> SearchStreetsAsync(RegionSettings region, string city, string query, CancellationToken ct)
    {
        LastStreetSearch = (region.Id, city, query);
        return Task.FromResult(Streets);
    }

    public Task<IReadOnlyList<DtekAddressHouseDto>> SearchHousesAsync(
        RegionSettings region, string city, string street, string query, CancellationToken ct)
    {
        LastHouseSearch = (region.Id, city, street, query);
        return Task.FromResult(Houses);
    }

    public void ImportDirectory(RegionSettings region, IReadOnlyDictionary<string, IReadOnlyList<string>> streets) =>
        ImportedDirectory = streets;
}

public sealed class FakeYasnoClient : IYasnoClient
{
    public IReadOnlyList<YasnoItem> Streets { get; set; } = [new(283, "вул. Драгоманова")];
    public IReadOnlyList<YasnoItem> Houses { get; set; } = [new(12634, "12")];
    public Dictionary<int, YasnoGroup?> Groups { get; } = new() { [12634] = new YasnoGroup(6, 2) };

    public Task<IReadOnlyList<YasnoItem>> SearchStreetsAsync(int regionId, int dsoId, string query, CancellationToken ct) =>
        Task.FromResult(Streets);

    public Task<IReadOnlyList<YasnoItem>> SearchHousesAsync(int regionId, int dsoId, int streetId, string query, CancellationToken ct) =>
        Task.FromResult(Houses);

    public Task<YasnoGroup?> GetGroupAsync(int regionId, int dsoId, int streetId, int houseId, CancellationToken ct) =>
        Task.FromResult(Groups.TryGetValue(houseId, out var group) ? group : null);
}

public static class ApiClientExtensions
{
    public static Task<HttpResponseMessage> PutJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PutAsJsonAsync(url, body, ApiJson.Options);

    public static Task<HttpResponseMessage> PostJsonAsync<T>(this HttpClient client, string url, T body) =>
        client.PostAsJsonAsync(url, body, ApiJson.Options);

    public static Task<T?> ReadAsync<T>(this HttpResponseMessage response) =>
        response.Content.ReadFromJsonAsync<T>(ApiJson.Options);
}
