using System.Text;
using System.Text.Json;
using DtekBot.Api.Data;
using DtekBot.Api.MiniApp;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Svitlobot;

// Manual management of a channel's Svitlobot schedule. The channel key is stored encrypted and is never sent back:
// the API only says whether a key exists.
public static class SvitlobotApi
{
    private const string UserIdKey = "tg.userId";
    public const int MaxKeyLength = 200;
    private const int MaxKeyBytes = 300;

    private static readonly ApiError NotConfigured = new("Світлобот не налаштований на сервері. Адміністратор має задати Svitlobot:KeyEncryptionKey (32 випадкові байти в base64) і перезапустити застосунок.");
    private static readonly ApiError Forbidden = new("Немає доступу до цього чату.");

    public static RouteGroupBuilder MapSvitlobotApi(this RouteGroupBuilder api)
    {
        api.MapPut("/chats/{chatId:long}/svitlobot/key", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            SvitlobotKeyProtector protector,
            ISvitlobotClient svitlobot,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (!protector.IsConfigured)
            {
                return Json(NotConfigured, 503);
            }

            SetSvitlobotKeyRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<SetSvitlobotKeyRequest>(ApiJson.Options, ct);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Json(Forbidden, 403);
            }

            var key = request?.Key?.Trim();
            if (string.IsNullOrEmpty(key))
            {
                return Json(new ApiError("Вкажіть ключ каналу зі Світлобота."), 400);
            }

            if (key.Length > MaxKeyLength || key.Any(char.IsControl) || Encoding.UTF8.GetByteCount(key) > MaxKeyBytes)
            {
                return Json(new ApiError("Ключ має неправильний формат."), 400);
            }

            if (SvitlobotWriteLimiter.IsLimited(chatId, time.GetUtcNow()))
            {
                return TooMany();
            }

            var info = await svitlobot.GetChannelAsync(key, ct);
            if (!info.Ok)
            {
                return Failed(info);
            }

            var stored = protector.Protect(key);
            if (stored is null)
            {
                return Json(NotConfigured, 503);
            }

            var channel = await LoadOrCreateAsync(db, chatId, time, ct);
            channel.SvitlobotKey = stored;
            await db.SaveChangesAsync(ct);
            return Json(State(info.Value!), 200);
        });

        // Forgets the key here only; whatever is set in Svitlobot stays.
        api.MapDelete("/chats/{chatId:long}/svitlobot/key", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            SvitlobotKeyProtector protector,
            CancellationToken ct) =>
        {
            if (!protector.IsConfigured)
            {
                return Json(NotConfigured, 503);
            }

            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Json(Forbidden, 403);
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            if (channel?.SvitlobotKey is not null)
            {
                channel.SvitlobotKey = null;
                await db.SaveChangesAsync(ct);
            }

            return Json(new SvitlobotStateDto(false, [], false), 200);
        });

        api.MapGet("/chats/{chatId:long}/svitlobot", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            SvitlobotKeyProtector protector,
            ISvitlobotClient svitlobot,
            CancellationToken ct) =>
        {
            if (!protector.IsConfigured)
            {
                return Json(NotConfigured, 503);
            }

            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Json(Forbidden, 403);
            }

            var stored = await db.Channels.AsNoTracking()
                .Where(c => c.ChatId == chatId)
                .Select(c => c.SvitlobotKey)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(stored))
            {
                return Json(new SvitlobotStateDto(false, [], false), 200);
            }

            var key = protector.Unprotect(stored);
            if (key is null)
            {
                return Json(UnreadableKey, 400);
            }

            var info = await svitlobot.GetChannelAsync(key, ct);
            return info.Ok ? Json(State(info.Value!), 200) : Failed(info);
        });

        // A masked check value, so the user can tell which key is saved; the key itself is never returned.
        api.MapGet("/chats/{chatId:long}/svitlobot/key-check", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            SvitlobotKeyProtector protector,
            CancellationToken ct) =>
        {
            if (!protector.IsConfigured)
            {
                return Json(NotConfigured, 503);
            }

            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Json(Forbidden, 403);
            }

            var stored = await db.Channels.AsNoTracking()
                .Where(c => c.ChatId == chatId)
                .Select(c => c.SvitlobotKey)
                .FirstOrDefaultAsync(ct);
            if (string.IsNullOrEmpty(stored))
            {
                return Json(new ApiError("Ключ ще не збережено."), 400);
            }

            var key = protector.Unprotect(stored);
            if (key is null)
            {
                return Json(UnreadableKey, 400);
            }

            return Json(new SvitlobotKeyCheckDto(SvitlobotKeyProtector.Mask(key), protector.Fingerprint(key)!), 200);
        });

        api.MapPost("/chats/{chatId:long}/svitlobot/template", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            SvitlobotKeyProtector protector,
            ISvitlobotClient svitlobot,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (!protector.IsConfigured)
            {
                return Json(NotConfigured, 503);
            }

            SetSvitlobotTemplateRequest? request;
            try
            {
                request = await http.Request.ReadFromJsonAsync<SetSvitlobotTemplateRequest>(ApiJson.Options, ct);
            }
            catch (JsonException)
            {
                request = null;
            }

            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Json(Forbidden, 403);
            }

            if (request is null || request.TemplateId <= 0)
            {
                return Json(new ApiError("Оберіть шаблон зі списку."), 400);
            }

            var (key, error) = await StoredKeyAsync(db, protector, chatId, ct);
            if (key is null)
            {
                return error!;
            }

            if (SvitlobotWriteLimiter.IsLimited(chatId, time.GetUtcNow()))
            {
                return TooMany();
            }

            var result = await svitlobot.SetTemplateAsync(key, request.TemplateId, ct);
            return result.Ok
                ? Json(new SvitlobotResultDto("Шаблон графіка встановлено у Світлоботі.", true), 200)
                : Failed(result);
        });

        // Sends the weekly schedule of the chat's group (the typical week of the outage-data feed).
        api.MapPost("/chats/{chatId:long}/svitlobot/push", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            SvitlobotKeyProtector protector,
            ISvitlobotClient svitlobot,
            IScheduleProvider schedules,
            IOptionsMonitor<DtekSettings> settings,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (!protector.IsConfigured)
            {
                return Json(NotConfigured, 503);
            }

            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Json(Forbidden, 403);
            }

            var channel = await db.Channels.AsNoTracking().FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            if (channel is null || string.IsNullOrEmpty(channel.SvitlobotKey))
            {
                return Json(NoKey, 400);
            }

            if (string.IsNullOrEmpty(channel.GroupId))
            {
                return Json(new ApiError("Спершу оберіть чергу для цього чату."), 400);
            }

            var region = settings.CurrentValue.Regions.FirstOrDefault(r => r.HasSchedules && r.Id == channel.RegionId);
            if (region is null)
            {
                return Json(new ApiError("Для регіону цього чату графіки недоступні."), 400);
            }

            var key = protector.Unprotect(channel.SvitlobotKey);
            if (key is null)
            {
                return Json(UnreadableKey, 400);
            }

            var snapshot = await schedules.GetAsync(region.Id, ct);
            if (snapshot is null)
            {
                return Json(new ApiError("Графіки тимчасово недоступні. Спробуйте пізніше."), 503);
            }

            var includeMaybe = true;
            if (http.Request.HasJsonContentType() && http.Request.ContentLength != 0)
            {
                try
                {
                    includeMaybe = (await http.Request.ReadFromJsonAsync<SvitlobotPushRequest>(ApiJson.Options, ct))?.IncludeMaybe ?? true;
                }
                catch (JsonException)
                {
                    return Json(new ApiError("Некоректний запит."), 400);
                }
            }

            var timetable = snapshot.WeeklyTimetable(channel.GroupId, includeMaybe);
            if (timetable is null)
            {
                return Json(new ApiError($"У джерелі даних немає тижневого графіка для черги {channel.GroupId}."), 400);
            }

            if (!WeeklyPreset.HasOutages(timetable))
            {
                var hint = includeMaybe ? "" : " Можна ввімкнути «можливі» відключення.";
                return Json(new ApiError($"У тижневому графіку черги {channel.GroupId} немає запланованих відключень, надсилати нічого.{hint}"), 400);
            }

            if (SvitlobotWriteLimiter.IsLimited(chatId, time.GetUtcNow()))
            {
                return TooMany();
            }

            var result = await svitlobot.SetTimetableAsync(key, timetable, ct);
            return result.Ok
                ? Json(new SvitlobotResultDto(
                    $"Тижневі планові відключення черги {channel.GroupId} збережено у Світлоботі. Це не надсилає негайний допис у Telegram.",
                    true), 200)
                : Failed(result);
        });

        // Removes the schedule in Svitlobot and its message in the Telegram channel.
        api.MapDelete("/chats/{chatId:long}/svitlobot/schedule", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            SvitlobotKeyProtector protector,
            ISvitlobotClient svitlobot,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (!protector.IsConfigured)
            {
                return Json(NotConfigured, 503);
            }

            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Json(Forbidden, 403);
            }

            var (key, error) = await StoredKeyAsync(db, protector, chatId, ct);
            if (key is null)
            {
                return error!;
            }

            if (SvitlobotWriteLimiter.IsLimited(chatId, time.GetUtcNow()))
            {
                return TooMany();
            }

            var result = await svitlobot.RemoveAsync(key, ct);
            return result.Ok
                ? Json(new SvitlobotResultDto("Графік видалено зі Світлобота.", false), 200)
                : Failed(result);
        });

        return api;
    }

    private static readonly ApiError NoKey = new("Спершу вкажіть ключ каналу зі Світлобота.");
    private static readonly ApiError UnreadableKey = new("Збережений ключ не вдалося прочитати. Вкажіть його ще раз.");

    private static long UserId(HttpContext http) => (long)http.Items[UserIdKey]!;

    private static IResult Json<T>(T value, int status) => Results.Json(value, ApiJson.Options, statusCode: status);

    private static IResult TooMany() =>
        Json(new ApiError("Забагато змін графіка Світлобота. Спробуйте за хвилину."), 429);

    // An unknown key is the user's input to fix; everything else is on the Svitlobot side.
    private static IResult Failed(SvitlobotResult result) =>
        Json(new ApiError(result.Message ?? "Світлобот недоступний. Спробуйте пізніше."), result.Failure == SvitlobotFailure.NotFound ? 400 : 502);

    private static SvitlobotStateDto State(SvitlobotChannelInfo info) =>
        new(true, info.Templates.Select(t => new SvitlobotTemplateDto(t.Id, t.Name)).ToList(), info.HasSchedule);

    private static async Task<(string? Key, IResult? Error)> StoredKeyAsync(
        BotDbContext db, SvitlobotKeyProtector protector, long chatId, CancellationToken ct)
    {
        var stored = await db.Channels.AsNoTracking()
            .Where(c => c.ChatId == chatId)
            .Select(c => c.SvitlobotKey)
            .FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(stored))
        {
            return (null, Json(NoKey, 400));
        }

        var key = protector.Unprotect(stored);
        return key is null ? (null, Json(UnreadableKey, 400)) : (key, null);
    }

    private static async Task<Channel> LoadOrCreateAsync(BotDbContext db, long chatId, TimeProvider time, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
        if (channel is null)
        {
            channel = new Channel { ChatId = chatId, IsActive = false, CreatedAt = time.GetUtcNow() };
            db.Channels.Add(channel);
        }

        return channel;
    }
}
