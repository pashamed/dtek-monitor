using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace DtekBot.Api.MiniApp;

// Every /api call carries "Authorization: tma <initData>" from the Mini App.
public static class MiniAppApi
{
    private const string UserIdKey = "tg.userId";
    private static readonly TimeSpan MaxInitDataAge = TimeSpan.FromHours(24);

    public static void MapMiniAppApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");
        api.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var header = http.Request.Headers.Authorization.ToString();
            var initData = header.StartsWith("tma ", StringComparison.OrdinalIgnoreCase) ? header[4..] : null;
            var token = http.RequestServices.GetRequiredService<IConfiguration>()["Bot:Token"];
            var time = http.RequestServices.GetRequiredService<TimeProvider>();

            if (!TelegramInitData.TryValidate(initData, token, time.GetUtcNow(), MaxInitDataAge, out var userId))
            {
                return Results.Json(new ApiError("Відкрийте застосунок із Telegram."), ApiJson.Options, statusCode: 401);
            }

            http.Items[UserIdKey] = userId;
            return await next(context);
        });

        api.MapGet("/bootstrap", (HttpContext http, IOptionsMonitor<DtekSettings> settings) =>
        {
            var regions = settings.CurrentValue.Regions
                .Select(r => new RegionDto(r.Id, r.Name, r.SiteUrl, r.Parts.Select(p => new PartDto(p.Id, p.Name)).ToList()))
                .ToList();
            return Json(new BootstrapDto(UserId(http), regions, MessageTemplate.Placeholders, MessageTemplate.Default, MessageTemplate.MaxLength));
        });

        api.MapGet("/chats", async (HttpContext http, BotDbContext db, CancellationToken ct) =>
        {
            var userId = UserId(http);
            var linked = await db.ChannelAdmins.Where(a => a.UserId == userId).Select(a => a.ChatId).ToListAsync(ct);
            linked.Add(userId);

            var rows = await db.Channels.Where(c => linked.Contains(c.ChatId)).ToListAsync(ct);
            var summaries = rows.Select(c => ToSummary(c, userId)).ToList();
            if (rows.All(c => c.ChatId != userId))
            {
                summaries.Insert(0, ToSummary(new Channel { ChatId = userId, IsActive = false }, userId));
            }

            return Json(summaries.OrderByDescending(s => s.IsPrivate).ThenBy(s => s.Title).ToList());
        });

        api.MapPost("/chats/link", async (
            HttpContext http,
            BotDbContext db,
            ITelegramBotClient bot,
            ChatAccessService access,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var userId = UserId(http);
            var request = await http.Request.ReadFromJsonAsync<LinkChatRequest>(ApiJson.Options, ct);
            var target = request?.Chat?.Trim();
            if (string.IsNullOrEmpty(target))
            {
                return Error("Вкажіть @імʼя каналу або числовий id чату.");
            }

            Telegram.Bot.Types.ChatFullInfo chat;
            try
            {
                chat = long.TryParse(target, out var numeric)
                    ? await bot.GetChat(numeric, ct)
                    : await bot.GetChat(target.StartsWith('@') ? target : "@" + target, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return Error("Чат не знайдено. Додайте бота до каналу чи групи та спробуйте ще раз.");
            }

            if (chat.Type is ChatType.Private)
            {
                return Error("Особистий чат підключати не потрібно, він уже є у списку.");
            }

            if (!await access.BotIsMemberAsync(chat.Id, ct))
            {
                return Error("Бот не доданий до цього чату.");
            }

            if (!await access.IsAdminAsync(chat.Id, userId, ct))
            {
                return Error("Керувати чатом можуть лише його адміністратори.");
            }

            var now = time.GetUtcNow();
            if (!await db.ChannelAdmins.AnyAsync(a => a.ChatId == chat.Id && a.UserId == userId, ct))
            {
                db.ChannelAdmins.Add(new ChannelAdmin { ChatId = chat.Id, UserId = userId, LinkedAt = now });
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chat.Id, ct);
            if (channel is null)
            {
                channel = new Channel { ChatId = chat.Id, IsActive = false, CreatedAt = now };
                db.Channels.Add(channel);
            }

            channel.Title = chat.Title ?? chat.Username;
            await db.SaveChangesAsync(ct);
            return Json(ToSummary(channel, userId));
        });

        api.MapPut("/chats/{chatId:long}", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            IOptionsMonitor<DtekSettings> settings,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var userId = UserId(http);
            var dto = await http.Request.ReadFromJsonAsync<ChatSettingsDto>(ApiJson.Options, ct);
            if (dto is null)
            {
                return Error("Порожній запит.");
            }

            if (!await access.CanManageAsync(chatId, userId, ct))
            {
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            }

            var template = string.IsNullOrWhiteSpace(dto.Template) || dto.Template == MessageTemplate.Default
                ? null
                : dto.Template;
            if (template is not null)
            {
                var errors = MessageTemplate.Validate(template);
                if (errors.Count > 0)
                {
                    return Results.Json(new ApiError("Шаблон містить помилки.", errors), ApiJson.Options, statusCode: 400);
                }
            }

            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, dto.RegionId, StringComparison.OrdinalIgnoreCase));
            if (dto.IsActive && region is null)
            {
                return Error("Оберіть регіон.");
            }

            string? partId = null;
            if (region is not null && !string.IsNullOrEmpty(dto.PartId))
            {
                partId = region.Parts.FirstOrDefault(p => string.Equals(p.Id, dto.PartId, StringComparison.OrdinalIgnoreCase))?.Id;
                if (partId is null)
                {
                    return Error("Невідома частина регіону.");
                }
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            if (channel is null)
            {
                channel = new Channel { ChatId = chatId, CreatedAt = time.GetUtcNow() };
                db.Channels.Add(channel);
            }

            if (region is not null)
            {
                channel.RegionId = region.Id;
                channel.PartId = partId;
            }

            channel.IsActive = dto.IsActive;
            channel.IsPaused = dto.IsPaused;
            channel.Filter = dto.Filter;
            channel.NotifyOnEnd = dto.NotifyOnEnd;
            channel.EditInPlace = dto.EditInPlace;
            channel.Template = template;
            await db.SaveChangesAsync(ct);

            return Json(ToSummary(channel, userId));
        });

        api.MapPost("/preview", async (HttpContext http, IOptionsMonitor<DtekSettings> settings, CancellationToken ct) =>
        {
            var request = await http.Request.ReadFromJsonAsync<PreviewRequest>(ApiJson.Options, ct);
            if (request is null)
            {
                return Error("Порожній запит.");
            }

            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, request.RegionId, StringComparison.OrdinalIgnoreCase))
                ?? settings.CurrentValue.Regions.First();
            var part = region.Parts.FirstOrDefault(p => string.Equals(p.Id, request.PartId, StringComparison.OrdinalIgnoreCase));

            var errors = string.IsNullOrWhiteSpace(request.Template) ? [] : MessageTemplate.Validate(request.Template);
            var html = errors.Count == 0
                ? MessageTemplate.RenderPreviewHtml(
                    request.Template,
                    request.Kind,
                    region.Name,
                    part is null ? [] : [part.Name],
                    region.SiteUrl)
                : "";
            return Json(new PreviewResponse(html, errors));
        });
    }

    private static long UserId(HttpContext http) => (long)http.Items[UserIdKey]!;

    private static IResult Json<T>(T value) => Results.Json(value, ApiJson.Options);

    private static IResult Error(string message) => Results.Json(new ApiError(message), ApiJson.Options, statusCode: 400);

    private static ChatSummaryDto ToSummary(Channel c, long userId)
    {
        var isPrivate = c.ChatId == userId;
        var title = isPrivate ? "Цей чат із ботом" : c.Title ?? c.ChatId.ToString();
        var settings = new ChatSettingsDto(
            string.IsNullOrEmpty(c.RegionId) ? null : c.RegionId,
            c.PartId,
            c.IsActive,
            c.IsPaused,
            c.Filter,
            c.NotifyOnEnd,
            c.EditInPlace,
            c.Template);
        return new ChatSummaryDto(c.ChatId, title, isPrivate, settings);
    }
}
