using System.Text.Json.Serialization;
using DtekBot.Api;
using DtekBot.Api.Bot;
using DtekBot.Api.Checks;
using DtekBot.Api.Data;
using DtekBot.Api.Grid;
using DtekBot.Api.Groups;
using DtekBot.Api.MiniApp;
using DtekBot.Api.Parsing;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using DtekBot.Api.Svitlobot;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("dtek-settings.json", optional: false, reloadOnChange: true);

// Level filtering still comes from the "Logging" config section.
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(builder.Environment.ContentRootPath, "logs")));
builder.Services.Configure<DtekSettings>(builder.Configuration.GetSection(DtekSettings.SectionName));

builder.Services.ConfigureHttpJsonOptions(o => JsonBotAPI.Configure(o.SerializerOptions));

builder.Services.AddDbContext<BotDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<IYasnoClient, YasnoClient>(client =>
{
    client.BaseAddress = new Uri(YasnoClient.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("dtek-monitor-bot/1.0");
});
builder.Services.AddHttpClient<IDtekAddressClient, DtekAddressClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; dtek-monitor-bot/1.0)");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false });
builder.Services.AddSingleton<PopupClassifier>();
builder.Services.AddSingleton<SvitlobotKeyProtector>();
// The channel key is in the query string: no request logging for this client.
builder.Services.AddHttpClient<ISvitlobotClient, SvitlobotClient>(client =>
{
    var baseUrl = builder.Configuration["Svitlobot:BaseUrl"];
    client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(baseUrl) ? SvitlobotClient.DefaultBaseUrl : baseUrl);
    client.Timeout = TimeSpan.FromSeconds(10);
    client.MaxResponseContentBufferSize = 256 * 1024;
    client.DefaultRequestHeaders.UserAgent.ParseAdd("dtek-monitor-bot/1.0");
}).RemoveAllLoggers();
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var token = config["Bot:Token"];

    // Bot:ApiBaseUrl lets tests point the client at a fake Telegram server; it is unset in production.
    var baseUrl = config["Bot:ApiBaseUrl"];
    return new TelegramBotClient(new TelegramBotClientOptions(
        string.IsNullOrWhiteSpace(token) ? "0:unset" : token,
        string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl));
});
builder.Services.AddScoped<BotUpdateHandler>();
builder.Services.AddHostedService<BotMenuInitializer>();
builder.Services.AddHostedService<TickLoop>();
builder.Services.AddScoped<AlertDispatcher>();
builder.Services.AddScoped<RegionAlertCoordinator>();
builder.Services.AddScoped<DtekBot.Api.Grid.GridTickService>();
builder.Services.AddSingleton<DtekBot.Api.Grid.IGridStatusProvider, DtekBot.Api.Grid.GridStatusProvider>();
builder.Services.AddHttpClient(DtekBot.Api.Grid.GridStatusProvider.ClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("dtek-monitor-bot/1.0");
});
builder.Services.AddScoped<ChatAccessService>();
builder.Services.AddScoped<AddressCheckService>();
builder.Services.AddScoped<OutageAssessmentService>();
builder.Services.AddScoped<ChatStatusService>();
builder.Services.AddScoped<ScheduleNotifier>();
builder.Services.AddScoped<SvitlobotScheduleSyncService>();
builder.Services.AddScoped<DtekBot.Api.MiniApp.TemplatePreviewService>();
builder.Services.AddScoped<AlertCatchUp>();
builder.Services.AddSingleton<IScheduleProvider, ScheduleProvider>();
builder.Services.AddHttpClient(ScheduleProvider.ClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("dtek-monitor-bot/1.0");
});
builder.Services.AddHttpClient<IWorkflowTrigger, GitHubWorkflowTrigger>(client =>
{
    client.BaseAddress = new Uri("https://api.github.com/");
    client.Timeout = TimeSpan.FromSeconds(10);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("dtek-monitor-bot/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
});

var app = builder.Build();

// The Mini App (plain HTML/JS) lives in wwwroot/lite.
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        if (context.Context.Request.Path.StartsWithSegments("/lite"))
        {
            context.Context.Response.Headers.CacheControl = "no-cache, must-revalidate";
        }
    },
});

if (app.Configuration.GetValue("Database:AutoMigrate", false))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<BotDbContext>().Database.Migrate();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapMiniAppApi();
app.MapGroupIngest();
app.MapAddressCheckIngest();
app.MapScheduleTick();
app.MapGridTick();

app.MapPost("/webhook", async (
    Update update,
    HttpRequest request,
    BotUpdateHandler handler,
    IConfiguration config,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var header = request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
    if (!Security.SecretMatches(header, config["Bot:WebhookSecret"]))
    {
        return Results.Unauthorized();
    }

    try
    {
        await handler.HandleAsync(update, ct);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        // Returning an error would make Telegram redeliver the same update over and over.
        logger.LogError(ex, "Update {UpdateId} failed", update.Id);
    }

    return Results.Ok();
});

app.MapPost("/ingest", async (
    IngestRequest body,
    HttpRequest request,
    BotDbContext db,
    PopupClassifier classifier,
    RegionAlertCoordinator coordinator,
    IOptionsMonitor<DtekSettings> settings,
    TimeProvider time,
    IConfiguration config,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var header = request.Headers["X-Ingest-Secret"].ToString();
    if (!Security.SecretMatches(header, config["Ingest:Secret"]))
    {
        logger.LogWarning("Ingest rejected: bad or missing secret (header length {Length})", header.Length);
        return Results.Unauthorized();
    }

    var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
        string.Equals(r.Id, body.RegionId, StringComparison.OrdinalIgnoreCase));
    if (region is null)
    {
        logger.LogWarning("Ingest rejected: unknown region '{RegionId}'", body.RegionId);
        return Results.BadRequest(new { error = "unknown region" });
    }

    var result = classifier.Classify(region.Id, body.PopupText, body.Structured);
    logger.LogInformation(
        "Ingest {Region}: kind={Kind} parts=[{Parts}] textLength={Length} structured={Structured}",
        region.Id, result.Kind, string.Join(",", result.PartIds), result.NormalizedText.Length, body.Structured?.Scope ?? "none");
    var now = time.GetUtcNow();

    var state = await db.RegionStates.FirstOrDefaultAsync(s => s.RegionId == region.Id, ct);
    var changed = state is null || state.Hash != result.Hash;
    if (state is null)
    {
        state = new RegionState { RegionId = region.Id };
        db.RegionStates.Add(state);
    }

    state.ObservedAt = body.ObservedAt ?? now;
    if (changed)
    {
        state.Kind = result.Kind;
        state.NormalizedText = result.NormalizedText;
        state.PopupText = body.PopupText?.Trim() is { Length: > 4000 } longText ? longText[..4000] : body.PopupText?.Trim();
        state.Hash = result.Hash;
        state.PartIdList = result.PartIds;
        state.UpdatedAt = now;
    }

    await db.SaveChangesAsync(ct);

    var dispatch = changed ? await coordinator.RecomputeAsync(region.Id, ct) : null;
    logger.LogInformation("Ingest {Region}: changed={Changed} dispatch={@Dispatch}", region.Id, changed, dispatch);

    return Results.Ok(new
    {
        regionId = region.Id,
        changed,
        kind = result.Kind.ToString(),
        parts = result.PartIds,
        dispatch,
    });
});

app.MapFallbackToFile("/lite/{*path:nonfile}", "lite/index.html");

app.Run();

public sealed record IngestRequest(
    [property: JsonPropertyName("regionId")] string RegionId,
    [property: JsonPropertyName("popupText")] string? PopupText,
    [property: JsonPropertyName("observedAt")] DateTimeOffset? ObservedAt,
    [property: JsonPropertyName("structured")] StructuredPopup? Structured = null);

public partial class Program;
