using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Telegram.Bot;

namespace DtekBot.Tests;

// In-memory SQLite + fake Telegram client + real classifier/settings, wired into a real AlertDispatcher.
public sealed class DispatcherFixture : IDisposable
{
    public const string EmergencyText =
        "За наказом НЕК Укренерго введені екстрені відключення електроенергії. Графіки відключень при цьому не діють.";

    public const string AccidentRightBankText =
        "Через аварію в мережі знеструмлено частину правого берега столиці. Стабілізаційні графіки в Києві не діють.";

    public const string AccidentLeftBankText =
        "Через аварію в мережі знеструмлено частину лівого берега столиці. Стабілізаційні графіки в Києві не діють.";

    public const string AccidentBothBanksText =
        "Через аварію в мережі знеструмлено частину правого та лівого берегів столиці. Стабілізаційні графіки в Києві не діють.";

    public const string StabilizationText =
        "За наказом НЕК Укренерго діють стабілізаційні відключення електроенергії.";

    public const string UnknownText = "Шановні клієнти! Щось нове і незрозуміле.";

    private readonly SqliteConnection _connection;

    public DispatcherFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "dtek-settings.json");
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        Settings = config.GetSection(DtekSettings.SectionName).Get<DtekSettings>()!;
        Settings.Schedules.EnableEventNotifications = true;
        Classifier = new PopupClassifier(new TestOptionsMonitor(Settings));
        Kyiv = Settings.Regions.Single(r => r.Id == "kyiv");

        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        Db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>()
            .UseSqlite(_connection)
            .ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCustomizer, UtcTicksModelCustomizer>()
            .Options);
        Db.Database.EnsureCreated();

        Bot = new FakeTelegramBotClient();
        Clock = new TestClock(new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.FromHours(3)));
        Dispatcher = new AlertDispatcher(Bot, Db, Clock, NullLogger<AlertDispatcher>.Instance, CreateMirror());
    }

    public DtekSettings Settings { get; }

    public PopupClassifier Classifier { get; }

    public RegionSettings Kyiv { get; }

    public BotDbContext Db { get; }

    public FakeTelegramBotClient Bot { get; }

    public TestClock Clock { get; }

    public AlertDispatcher Dispatcher { get; }

    public PrivateNotificationMirror CreateMirror(ITelegramBotClient? bot = null, TimeProvider? time = null) =>
        new(bot ?? Bot, Db,
            new DtekBot.Api.MiniApp.ChatAccessService(bot ?? Bot, Db,
                NullLogger<DtekBot.Api.MiniApp.ChatAccessService>.Instance),
            time ?? Clock, NullLogger<PrivateNotificationMirror>.Instance);

    public IOptionsMonitor<DtekSettings> Options => new TestOptionsMonitor(Settings);

    public Channel AddChannel(long chatId, Action<Channel>? configure = null, string regionId = "kyiv")
    {
        var channel = new Channel
        {
            ChatId = chatId,
            RegionId = regionId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        configure?.Invoke(channel);
        Db.Channels.Add(channel);
        Db.SaveChanges();
        return channel;
    }

    public Delivery AddOpenDelivery(long chatId, int messageId, string regionId = "kyiv", string hash = "old")
    {
        var delivery = new Delivery
        {
            ChatId = chatId,
            RegionId = regionId,
            TelegramMessageId = messageId,
            ContentHash = hash,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        Db.Deliveries.Add(delivery);
        Db.SaveChanges();
        return delivery;
    }

    public Task<DispatchSummary> DispatchAsync(string? popupText, RegionSettings? region = null)
    {
        region ??= Kyiv;
        var result = Classifier.Classify(region.Id, popupText);
        return Dispatcher.DispatchAsync(region, result, popupText, CancellationToken.None);
    }

    // Reads straight from the database so assertions see what was actually persisted.
    public List<Delivery> Deliveries(long chatId) =>
        Db.Deliveries.AsNoTracking().Where(d => d.ChatId == chatId).OrderBy(d => d.Id).ToList();

    public Channel LoadChannel(long chatId) =>
        Db.Channels.AsNoTracking().Single(c => c.ChatId == chatId);

    public void Dispose()
    {
        Db.Dispose();
        _connection.Dispose();
    }

    private sealed class TestOptionsMonitor(DtekSettings value) : IOptionsMonitor<DtekSettings>
    {
        public DtekSettings CurrentValue => value;
        public DtekSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<DtekSettings, string?> listener) => null;
    }
}
