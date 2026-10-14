using DtekBot.Api;
using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("dtek-settings.json", optional: false, reloadOnChange: true);
builder.Services.Configure<DtekSettings>(builder.Configuration.GetSection(DtekSettings.SectionName));

builder.Services.ConfigureHttpJsonOptions(o => JsonBotAPI.Configure(o.SerializerOptions));

builder.Services.AddDbContext<BotDbContext>(o =>
    o.UseSqlServer(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<PopupClassifier>();
builder.Services.AddSingleton<ITelegramBotClient>(sp =>
{
    var token = sp.GetRequiredService<IConfiguration>()["Bot:Token"];
    return new TelegramBotClient(string.IsNullOrWhiteSpace(token) ? "0:unset" : token);
});
builder.Services.AddScoped<BotUpdateHandler>();

var app = builder.Build();

if (app.Configuration.GetValue("Database:AutoMigrate", false))
{
    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<BotDbContext>().Database.Migrate();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/webhook", async (
    Update update,
    HttpRequest request,
    BotUpdateHandler handler,
    IConfiguration config,
    CancellationToken ct) =>
{
    var header = request.Headers["X-Telegram-Bot-Api-Secret-Token"].ToString();
    if (!Security.SecretMatches(header, config["Bot:WebhookSecret"]))
    {
        return Results.Unauthorized();
    }

    await handler.HandleAsync(update, ct);
    return Results.Ok();
});

app.MapPost("/ingest", async (
    IngestRequest body,
    HttpRequest request,
    BotDbContext db,
    PopupClassifier classifier,
    IOptionsMonitor<DtekSettings> settings,
    TimeProvider time,
    IConfiguration config,
    CancellationToken ct) =>
{
    var header = request.Headers["X-Ingest-Secret"].ToString();
    if (!Security.SecretMatches(header, config["Ingest:Secret"]))
    {
        return Results.Unauthorized();
    }

    var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
        string.Equals(r.Id, body.RegionId, StringComparison.OrdinalIgnoreCase));
    if (region is null)
    {
        return Results.BadRequest(new { error = "unknown region" });
    }

    var result = classifier.Classify(region.Id, body.PopupText);
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
        state.Hash = result.Hash;
        state.UpdatedAt = now;
    }

    await db.SaveChangesAsync(ct);

    return Results.Ok(new
    {
        regionId = region.Id,
        changed,
        kind = result.Kind.ToString(),
        parts = result.PartIds,
    });
});

app.Run();

public sealed record IngestRequest(string RegionId, string? PopupText, DateTimeOffset? ObservedAt);

public partial class Program;
