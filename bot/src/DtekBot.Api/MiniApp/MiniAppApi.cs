using DtekBot.Api.Bot;
using DtekBot.Api.Checks;
using DtekBot.Api.Data;
using DtekBot.Api.Groups;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using DtekBot.Api.Svitlobot;
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

        api.MapSvitlobotApi();

        api.MapGet("/bootstrap", (HttpContext http, IOptionsMonitor<DtekSettings> settings) =>
        {
            var regions = settings.CurrentValue.Regions
                .Select(r => new RegionDto(r.Id, r.Name, r.SiteUrl, r.Parts.Select(p => new PartDto(p.Id, p.Name)).ToList(), r.HasGroups, r.HasSchedules))
                .ToList();
            return Json(new BootstrapDto(
                UserId(http),
                regions,
                PlaceholderNames.ToDisplayList(MessageTemplate.Placeholders),
                PlaceholderNames.ToDisplay(MessageTemplate.Default),
                MessageTemplate.MaxLength,
                MessageSlots.All.Select(PlaceholderNames.ToDisplaySlot).ToList()));
        });

        api.MapGet("/chats", async (HttpContext http, BotDbContext db, IOptionsMonitor<DtekSettings> settings, CancellationToken ct) =>
        {
            var userId = UserId(http);
            var linked = await db.ChannelAdmins.Where(a => a.UserId == userId).Select(a => a.ChatId).ToListAsync(ct);
            linked.Add(userId);

            var rows = await db.Channels.Where(c => linked.Contains(c.ChatId)).ToListAsync(ct);

            // Saved districts that were retired (the Kyiv banks) become the districts that replaced them.
            var upgraded = false;
            foreach (var row in rows)
            {
                var region = settings.CurrentValue.Regions.FirstOrDefault(r => r.Id == row.RegionId);
                upgraded |= region?.UpgradeRetiredParts(row) == true;
            }

            if (upgraded)
            {
                await db.SaveChangesAsync(ct);
            }

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
                return Error("Вкажіть @імʼя каналу.");
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
                return Error("Канал не знайдено. Додайте бота адміністратором у канал і спробуйте ще раз.");
            }

            if (chat.Type is ChatType.Private)
            {
                return Error("Особистий чат підключати не потрібно, він уже є у списку.");
            }

            if (chat.Type is not ChatType.Channel)
            {
                return Error("Підключіть канал, а не групу: бот публікує сповіщення в каналах.");
            }

            if (!await access.BotIsMemberAsync(chat.Id, ct))
            {
                return Error("Бот не доданий до цього каналу.");
            }

            if (!await access.IsAdminAsync(chat.Id, userId, ct))
            {
                return Error("Керувати каналом можуть лише його адміністратори.");
            }

            var now = time.GetUtcNow();
            var managed = await db.ChannelAdmins.AnyAsync(a => a.ChatId == chat.Id, ct);
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
            else if (!managed)
            {
                // Nobody manages it, so it is connected as a new channel, not with what was left from before.
                await ChannelReset.ResetAsync(db, channel, ct);
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

            // Templates by slot: only customised, non-default texts are kept. Old clients send the single Template.
            Dictionary<string, string>? templates = null;
            var templateErrors = new List<string>();
            if (dto.Templates is not null)
            {
                templates = [];
                foreach (var (slot, text) in dto.Templates)
                {
                    var definition = MessageSlots.Find(slot);
                    if (definition is null)
                    {
                        templateErrors.Add($"Невідомий тип повідомлення «{slot}».");
                        continue;
                    }

                    var canonical = PlaceholderNames.ToCanonical(text);
                    if (string.IsNullOrWhiteSpace(canonical) || canonical == definition.DefaultTemplate)
                    {
                        continue;
                    }

                    templateErrors.AddRange(MessageTemplate.Validate(canonical, slot).Select(e => $"{definition.Title}: {PlaceholderNames.ToDisplay(e)}"));
                    templates[slot] = canonical;
                }
            }
            else if (!string.IsNullOrWhiteSpace(dto.Template) && PlaceholderNames.ToCanonical(dto.Template) != MessageTemplate.Default)
            {
                templateErrors.AddRange(MessageTemplate.Validate(PlaceholderNames.ToCanonical(dto.Template)).Select(e => $"Екстрені відключення: {PlaceholderNames.ToDisplay(e)}"));
            }

            if (templateErrors.Count > 0)
            {
                return Results.Json(new ApiError("Шаблон містить помилки.", templateErrors), ApiJson.Options, statusCode: 400);
            }

            Dictionary<string, SlotSettingDto>? slotSettings = null;
            if (dto.SlotSettings is not null)
            {
                slotSettings = [];
                foreach (var (slot, value) in dto.SlotSettings)
                {
                    if (!MessageSlots.IsAlert(slot) || value is null)
                    {
                        return Error($"Невідомий тип сповіщення «{slot}».");
                    }

                    slotSettings[slot] = value;
                }
            }

            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, dto.RegionId, StringComparison.OrdinalIgnoreCase));
            if (dto.IsActive && region is null)
            {
                return Error("Оберіть регіон.");
            }

            // Districts: PartIds (new, many) wins; the legacy single PartId is still understood.
            List<string> partIds = [];
            if (region is not null)
            {
                var requested = dto.PartIds ?? (string.IsNullOrEmpty(dto.PartId) ? [] : [dto.PartId]);
                foreach (var raw in requested.Where(r => !string.IsNullOrWhiteSpace(r)))
                {
                    var match = region.Parts.FirstOrDefault(p => string.Equals(p.Id, raw.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        return Error("Невідомий район.");
                    }

                    if (!partIds.Contains(match.Id))
                    {
                        partIds.Add(match.Id);
                    }
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
                // A group belongs to one region's power network.
                if (!string.Equals(channel.RegionId, region.Id, StringComparison.Ordinal))
                {
                    channel.GroupId = null;
                    channel.AddressLabel = null;
                    channel.AddressStreet = null;
                    channel.AddressHouse = null;
                    channel.ScheduleNotify = false;
                }

                channel.RegionId = region.Id;
                channel.PartId = null;
                channel.PartIdList = partIds;
            }

            // Switching the subscription off also removes the saved address and everything that depends on it.
            if (channel.IsActive && !dto.IsActive)
            {
                channel.GroupId = null;
                channel.AddressLabel = null;
                channel.AddressStreet = null;
                channel.AddressHouse = null;
                channel.OutageTrigger = false;
                channel.ScheduleNotify = false;
            }

            channel.IsActive = dto.IsActive;
            channel.IsPaused = dto.IsPaused;
            channel.Filter = dto.Filter;
            if (slotSettings is not null)
            {
                // Mirrored into the older columns so clients that do not know the per-slot map still read sensible values.
                channel.SlotSettingMap = slotSettings;
                channel.NotifyOnEnd = slotSettings.TryGetValue(MessageSlots.Ended, out var ended) ? ended.Enabled : channel.NotifyOnEnd;
                channel.EditInPlace = slotSettings.TryGetValue(MessageSlots.Emergency, out var emergency) ? emergency.EditInPlace : channel.EditInPlace;
            }
            else
            {
                channel.SlotSettingMap = new Dictionary<string, SlotSettingDto>();
                channel.NotifyOnEnd = dto.NotifyOnEnd;
                channel.EditInPlace = dto.EditInPlace;
            }
            if (templates is not null)
            {
                channel.TemplateMap = templates;
                channel.Template = null;
            }
            else
            {
                channel.Template = string.IsNullOrWhiteSpace(dto.Template) || PlaceholderNames.ToCanonical(dto.Template) == MessageTemplate.Default ? null : PlaceholderNames.ToCanonical(dto.Template);
            }

            channel.OutageTrigger = dto.IsActive && dto.OutageTrigger;
            var pattern = OutageTriggerMatcher.Normalize(dto.OutageTriggerPattern);
            if (pattern is not null && pattern.Length > OutageTriggerMatcher.MaxLength)
            {
                return Error($"Фраза задовга: максимум {OutageTriggerMatcher.MaxLength} символів.");
            }

            channel.OutageTriggerPattern = pattern;

            // Schedule messages need a group and a region that has schedules.
            var regionHasSchedules = settings.CurrentValue.Regions.Any(r => r.Id == channel.RegionId && r.HasSchedules);
            var canSchedule = dto.IsActive && channel.GroupId is not null && regionHasSchedules;
            channel.ScheduleNotify = canSchedule && dto.ScheduleNotify;
            channel.ScheduleImage = dto.ScheduleImage;
            channel.ScheduleEventMode = dto.ScheduleEventMode;
            channel.SchedulePost = canSchedule && dto.SchedulePost;
            channel.SchedulePostMode = dto.SchedulePostMode;
            channel.SchedulePostImage = dto.SchedulePostImage;
            await db.SaveChangesAsync(ct);

            return Json(ToSummary(channel, userId));
        });

        api.MapPost("/preview", async (
            HttpContext http,
            IOptionsMonitor<DtekSettings> settings,
            TemplatePreviewService previews,
            CancellationToken ct) =>
        {
            var request = await http.Request.ReadFromJsonAsync<PreviewRequest>(ApiJson.Options, ct);
            if (request is null)
            {
                return Error("Порожній запит.");
            }

            var slot = string.IsNullOrWhiteSpace(request.Slot) ? MessageSlots.Emergency : request.Slot;
            if (MessageSlots.Find(slot) is null)
            {
                return Error("Невідомий тип повідомлення.");
            }

            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, request.RegionId, StringComparison.OrdinalIgnoreCase))
                ?? settings.CurrentValue.Regions.First();
            var wantedParts = request.PartIds ?? (string.IsNullOrEmpty(request.PartId) ? [] : [request.PartId]);

            var previewTemplate = PlaceholderNames.ToCanonical(request.Template);
            var errors = string.IsNullOrWhiteSpace(previewTemplate)
                ? new List<string>()
                : MessageTemplate.Validate(previewTemplate, slot).Select(e => PlaceholderNames.ToDisplay(e)).ToList();
            if (errors.Count > 0)
            {
                return Json(new PreviewResponse("", errors));
            }

            // The legacy single-template call has no slot: its Kind picks the sample.
            var kind = string.IsNullOrWhiteSpace(request.Slot) ? request.Kind : (AlertKind?)null;
            var rendered = await previews.RenderAsync(
                slot,
                previewTemplate,
                region,
                wantedParts,
                request.GroupId,
                null,
                request.ImageKind ?? ScheduleImage.Grid,
                kind,
                ct);
            return Json(new PreviewResponse(rendered.Html, errors, rendered.ImageUrl));
        });

        // Posts a sample of the slot into the chat so the user sees the real thing in Telegram.
        api.MapPost("/chats/{chatId:long}/templates/{slot}/send", async (
            long chatId,
            string slot,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            IOptionsMonitor<DtekSettings> settings,
            TemplatePreviewService previews,
            ScheduleNotifier notifier,
            TimeProvider time,
            CancellationToken ct) =>
        {
            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            }

            if (MessageSlots.Find(slot) is null)
            {
                return Error("Невідомий тип повідомлення.");
            }

            var request = await http.Request.ReadFromJsonAsync<SendTestRequest>(ApiJson.Options, ct);
            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            if (channel is null || string.IsNullOrEmpty(channel.RegionId))
            {
                return Error("Спершу оберіть регіон і збережіть налаштування.");
            }

            var template = string.IsNullOrWhiteSpace(request?.Template) ? channel.TemplateFor(slot) : PlaceholderNames.ToCanonical(request!.Template);
            if (!string.IsNullOrWhiteSpace(template))
            {
                var errors = MessageTemplate.Validate(template, slot).Select(e => PlaceholderNames.ToDisplay(e)).ToList();
                if (errors.Count > 0)
                {
                    return Results.Json(new ApiError("Шаблон містить помилки.", errors), ApiJson.Options, statusCode: 400);
                }
            }

            var now = time.GetUtcNow();
            if (TestSendLimiter.IsLimited(chatId, now))
            {
                return Results.Json(new ApiError("Забагато тестових повідомлень. Спробуйте за кілька секунд."), ApiJson.Options, statusCode: 429);
            }

            var region = settings.CurrentValue.Regions.FirstOrDefault(r => r.Id == channel.RegionId);
            if (region is null)
            {
                return Error("Оберіть регіон.");
            }

            var rendered = await previews.RenderAsync(
                slot,
                template,
                region,
                channel.PartIdList,
                channel.GroupId,
                channel.AddressLabel,
                request?.ImageKind ?? (slot == MessageSlots.Schedule ? channel.SchedulePostImage : ScheduleImage.None),
                null,
                ct);

            try
            {
                await notifier.SendNewAsync(chatId, "🧪 <b>Тест</b>\n\n" + rendered.Html, rendered.ImageUrl, ct);
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex) when (ex.ErrorCode == 403)
            {
                return Error("Бот не може писати в цей чат. Перевірте, що він адміністратор каналу з правом публікації.");
            }
            catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            {
                return Error("Telegram не прийняв повідомлення: " + ex.Message);
            }

            return Json(new { ok = true });
        });
        // Advice about the «Світло зникло» phrase: does it occur in the posts the bot has seen in this channel?
        api.MapGet("/chats/{chatId:long}/trigger-check", async (
            long chatId,
            string? pattern,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            CancellationToken ct) =>
        {
            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            var recent = OutageTriggerMatcher.ParseRecent(channel?.RecentPosts);
            var check = OutageTriggerMatcher.Check(pattern, recent);
            return Json(new TriggerCheckDto(check.Valid, check.Seen, check.Matched, check.Warning));
        });

        // The editor's "current state" card (replaces the bot's old status button).
        api.MapGet("/chats/{chatId:long}/status", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            ChatStatusService status,
            CancellationToken ct) =>
        {
            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            return Json(new ChatStatusDto(await status.BuildAsync(channel, ct)));
        });

        // "The power is off, why?" from the editor (replaces the old /check). Only for the user's own chat:
        // a queued DTEK address check answers there, and channels get this through the «Світло зникло» post.
        api.MapPost("/chats/{chatId:long}/check", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            OutageAssessmentService assessment,
            CancellationToken ct) =>
        {
            if (chatId != UserId(http))
            {
                return Error("Перевірка за запитом доступна в особистому чаті. Для каналу її запускає повідомлення «Світло зникло».");
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId && c.IsActive, ct);
            if (channel is null)
            {
                return Error("Спершу оберіть регіон і ввімкніть підписку.");
            }

            return Json(new CheckResultDto(await assessment.AssessAsync(channel, ct)));
        });

        api.MapGet("/address/streets", async (
            string regionId,
            string? query,
            IYasnoClient yasno,
            IOptionsMonitor<DtekSettings> settings,
            CancellationToken ct) =>
        {
            var region = GroupRegion(settings.CurrentValue, regionId);
            if (region is null)
            {
                return Error("Для цього регіону пошук за адресою недоступний.");
            }

            if ((query?.Trim().Length ?? 0) < 2)
            {
                return Json(Array.Empty<AddressItemDto>());
            }

            var items = await yasno.SearchStreetsAsync(region.YasnoRegionId, region.YasnoDsoId, query!.Trim(), ct);
            return Json(items.Take(20).Select(i => new AddressItemDto(i.Id, i.Value)).ToList());
        });

        api.MapGet("/address/houses", async (
            string regionId,
            int streetId,
            string? query,
            IYasnoClient yasno,
            IOptionsMonitor<DtekSettings> settings,
            CancellationToken ct) =>
        {
            var region = GroupRegion(settings.CurrentValue, regionId);
            if (region is null)
            {
                return Error("Для цього регіону пошук за адресою недоступний.");
            }

            var items = await yasno.SearchHousesAsync(region.YasnoRegionId, region.YasnoDsoId, streetId, query?.Trim() ?? "", ct);
            return Json(items.Take(50).Select(i => new AddressItemDto(i.Id, i.Value)).ToList());
        });

        api.MapDelete("/chats/{chatId:long}", async (
            long chatId,
            HttpContext http,
            ChatAccessService access,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var userId = UserId(http);
            if (chatId == userId)
            {
                return Error("Особистий чат не можна видалити зі списку. Вимкніть підписку в налаштуваннях.");
            }

            var outcome = await access.UnlinkAsync(chatId, userId, time.GetUtcNow(), ct);
            return outcome == UnlinkOutcome.NotLinked
                ? Results.Json(new ApiError("Чат не знайдено у вашому списку."), ApiJson.Options, statusCode: 404)
                : Json(new { unsubscribed = outcome == UnlinkOutcome.UnlinkedAndUnsubscribed });
        });

        api.MapPut("/chats/{chatId:long}/address", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            IYasnoClient yasno,
            IOptionsMonitor<DtekSettings> settings,
            CancellationToken ct) =>
        {
            var userId = UserId(http);
            var request = await http.Request.ReadFromJsonAsync<SetAddressRequest>(ApiJson.Options, ct);
            if (request is null)
            {
                return Error("Порожній запит.");
            }

            if (!await access.CanManageAsync(chatId, userId, ct))
            {
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            }

            var region = GroupRegion(settings.CurrentValue, request.RegionId);
            if (region is null)
            {
                return Error("Для цього регіону пошук за адресою недоступний.");
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            if (channel is null || channel.RegionId != region.Id)
            {
                return Error($"Спершу оберіть регіон «{region.Name}» і збережіть налаштування.");
            }

            var group = await yasno.GetGroupAsync(region.YasnoRegionId, region.YasnoDsoId, request.StreetId, request.HouseId, ct);
            if (group is null)
            {
                return Error("Не вдалося визначити чергу для цієї адреси. Спробуйте пізніше.");
            }

            var label = request.Label?.Trim() ?? "";
            channel.GroupId = group.Id;
            channel.AddressLabel = label.Length > 256 ? label[..256] : label;
            channel.AddressStreet = CleanPart(request.Street, 256);
            channel.AddressHouse = CleanPart(request.House, 64);
            await db.SaveChangesAsync(ct);
            return Json(ToSummary(channel, userId));
        });

        api.MapDelete("/chats/{chatId:long}/address", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            CancellationToken ct) =>
        {
            var userId = UserId(http);
            if (!await access.CanManageAsync(chatId, userId, ct))
            {
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            if (channel is null)
            {
                return Error("Чат не знайдено.");
            }

            channel.GroupId = null;
            channel.AddressLabel = null;
            channel.AddressStreet = null;
            channel.AddressHouse = null;
            channel.ScheduleNotify = false;
            channel.SchedulePost = false;
            await db.SaveChangesAsync(ct);
            return Json(ToSummary(channel, userId));
        });

        api.MapGet("/schedules/groups", async (
            string regionId,
            IOptionsMonitor<DtekSettings> settings,
            IScheduleProvider schedules,
            CancellationToken ct) =>
        {
            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                r.HasSchedules && string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
            if (region is null)
            {
                return Error("Для цього регіону графіки недоступні.");
            }

            var snapshot = await schedules.GetAsync(region.Id, ct);
            return snapshot is null
                ? Results.Json(new ApiError("Графіки тимчасово недоступні. Спробуйте пізніше."), ApiJson.Options, statusCode: 503)
                : Json(snapshot.GroupIds.Select(id => new GroupItemDto(id, $"Черга {id}")).ToList());
        });

        // Manual group choice, for regions without the address picker.
        api.MapPut("/chats/{chatId:long}/group", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            IScheduleProvider schedules,
            IOptionsMonitor<DtekSettings> settings,
            CancellationToken ct) =>
        {
            var userId = UserId(http);
            var request = await http.Request.ReadFromJsonAsync<SetGroupRequest>(ApiJson.Options, ct);
            if (request is null)
            {
                return Error("Порожній запит.");
            }

            if (!await access.CanManageAsync(chatId, userId, ct))
            {
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            }

            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                r.HasSchedules && string.Equals(r.Id, request.RegionId, StringComparison.OrdinalIgnoreCase));
            if (region is null)
            {
                return Error("Для цього регіону графіки недоступні.");
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            if (channel is null || channel.RegionId != region.Id)
            {
                return Error($"Спершу оберіть регіон «{region.Name}» і збережіть налаштування.");
            }

            var snapshot = await schedules.GetAsync(region.Id, ct);
            if (snapshot is null)
            {
                return Results.Json(new ApiError("Графіки тимчасово недоступні. Спробуйте пізніше."), ApiJson.Options, statusCode: 503);
            }

            if (snapshot.Find(request.GroupId ?? "") is null)
            {
                return Error("Невідома черга.");
            }

            // A hand-picked group only replaces the detected one: the saved address stays for the DTEK check.
            channel.GroupId = request.GroupId;
            await db.SaveChangesAsync(ct);
            return Json(ToSummary(channel, userId));
        });

        // What the bot would post for this group right now, so the user sees the picture and text before enabling it.
        api.MapGet("/schedules/preview", async (
            string regionId,
            string groupId,
            string? kind,
            IOptionsMonitor<DtekSettings> settings,
            IScheduleProvider schedules,
            TimeProvider time,
            CancellationToken ct) =>
        {
            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                r.HasSchedules && string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
            if (region is null)
            {
                return Error("Для цього регіону графіки недоступні.");
            }

            var snapshot = await schedules.GetAsync(region.Id, ct);
            if (snapshot is null)
            {
                return Results.Json(new ApiError("Графіки тимчасово недоступні. Спробуйте пізніше."), ApiJson.Options, statusCode: 503);
            }

            var now = time.GetUtcNow();
            var status = snapshot.StatusAt(groupId, now);
            if (status is null)
            {
                return Error("Для цієї черги немає даних графіка.");
            }

            var imageKind = Enum.TryParse<ScheduleImage>(kind, ignoreCase: true, out var parsed) ? parsed : ScheduleImage.Grid;
            var text = status.IsOff
                ? ScheduleText.OffMessage(region.Name, groupId, status, now)
                : ScheduleText.OnMessage(region.Name, groupId, status, null, now);
            return Json(new SchedulePreviewDto(
                groupId,
                text,
                ScheduleText.ImageUrl(settings.CurrentValue.Schedules, snapshot.DataFile, groupId, snapshot.Version, imageKind)));
        });
    }

    // The street and house are later sent to the DTEK site, so only plain text of sane length is kept.
    private static string? CleanPart(string? value, int max)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > max || text.Any(char.IsControl))
        {
            return null;
        }

        return text;
    }

    private static RegionSettings? GroupRegion(DtekSettings settings, string? regionId) =>
        settings.Regions.FirstOrDefault(r => r.HasGroups && string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));

    private static long UserId(HttpContext http) => (long)http.Items[UserIdKey]!;

    private static IResult Json<T>(T value) => Results.Json(value, ApiJson.Options);

    private static IResult Error(string message) => Results.Json(new ApiError(message), ApiJson.Options, statusCode: 400);

    private static ChatSummaryDto ToSummary(Channel c, long userId)
    {
        var isPrivate = c.ChatId == userId;
        var title = isPrivate ? "Цей чат із ботом" : c.Title ?? c.ChatId.ToString();
        var settings = new ChatSettingsDto(
            string.IsNullOrEmpty(c.RegionId) ? null : c.RegionId,
            c.PartIdList.FirstOrDefault(),
            c.IsActive,
            c.IsPaused,
            c.Filter,
            c.NotifyOnEnd,
            c.EditInPlace,
            null,
            c.GroupId,
            c.AddressLabel,
            c.OutageTrigger,
            c.ScheduleNotify,
            c.ScheduleImage,
            c.PartIdList,
            c.SchedulePost,
            c.SchedulePostMode,
            c.SchedulePostImage,
            c.ScheduleEventMode,
            MergedTemplates(c),
            c.OutageTriggerPattern,
            MessageSlots.All.Where(s => MessageSlots.IsAlert(s.Id))
                .ToDictionary(s => s.Id, s => new SlotSettingDto(c.SlotEnabled(s.Id), c.SlotEditInPlace(s.Id))),
            !string.IsNullOrEmpty(c.SvitlobotKey));
        return new ChatSummaryDto(c.ChatId, title, isPrivate, settings);
    }

    // The older single template counts as the customised text of every alert slot that has none of its own.
    private static Dictionary<string, string> MergedTemplates(Channel c)
    {
        var map = c.TemplateMap.Where(p => MessageSlots.Find(p.Key) is not null).ToDictionary(p => p.Key, p => p.Value);
        if (!string.IsNullOrWhiteSpace(c.Template))
        {
            foreach (var slot in MessageSlots.All.Where(s => MessageSlots.IsAlert(s.Id)))
            {
                map.TryAdd(slot.Id, c.Template);
            }
        }

        return map.ToDictionary(p => p.Key, p => PlaceholderNames.ToDisplay(p.Value));
    }
}
