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
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, SemaphoreSlim> AddressGates = new();
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

        api.MapPut("/chats/{chatId:long}/addresses", async (
            long chatId, HttpContext http, BotDbContext db, ChatAccessService access,
            IOptionsMonitor<DtekSettings> settings, IScheduleProvider schedules,
            AlertCatchUp catchUp, TimeProvider time, CancellationToken ct) =>
        {
            if (!await access.CanManageAsync(chatId, UserId(http), ct))
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            var request = await http.Request.ReadFromJsonAsync<SaveAddressesRequest>(ApiJson.Options, ct);
            if (request?.Addresses is null || request.Addresses.Count > 3)
                return Error("Можна зберегти до 3 адрес.");
            var gate = AddressGates.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
                var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
                if (channel is null) return Error("Спершу збережіть регіон.");
                var region = settings.CurrentValue.Regions.FirstOrDefault(r => r.Id == channel.RegionId);
                if (region is null) return Error("Оберіть регіон.");
                var snapshot = region.HasSchedules ? await schedules.GetAsync(region.Id, ct) : null;
                var addressError = ValidateAddresses(request.Addresses, channel, region, snapshot);
                if (addressError is not null) return Error(addressError);
                ApplyAddresses(channel, request.Addresses);
                if (channel.GroupId is null) { channel.SchedulePost = false; channel.ScheduleNotify = false; channel.ScheduleReminder = false; }
                await ReconcileAddressStateAsync(db, channel, time.GetUtcNow(), ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
                await catchUp.RunAsync(channel, ct);
                return Json(ToSummary(channel, UserId(http)));
            }
            finally { gate.Release(); }
        });

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
            var parsed = ChatLinkParser.Parse(request?.Chat);
            if (parsed.Chat is null)
            {
                return Error(parsed.Error ?? "Вкажіть посилання на канал (t.me/…) або @імʼя.");
            }

            var target = parsed.Chat;

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

            var (isAdmin, problem) = await access.CheckAdminAsync(chat.Id, userId, ct);
            if (problem is not null)
            {
                return Error($"Telegram не дав перевірити ваші права в каналі ({problem}). Переконайтеся, що бот доданий адміністратором каналу, і спробуйте ще раз. Простіше: додайте бота адміністратором із Telegram, і канал зʼявиться тут сам.");
            }

            if (!isAdmin)
            {
                return Error("Керувати каналом можуть лише його адміністратори. Ваш акаунт у Telegram, з якого відкрито редактор, не є адміністратором цього каналу.");
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
            AlertCatchUp catchUp,
            IScheduleProvider schedules,
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

                    slotSettings[slot] = value with
                    {
                        EndStyle = slot == MessageSlots.Ended && Enum.IsDefined(value.EndStyle) ? value.EndStyle : EndStyle.Reply,
                    };
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

            var gate = AddressGates.GetOrAdd(chatId, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, ct);
                var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
                var isNew = channel is null;
                if (channel is null)
                {
                    channel = new Channel { ChatId = chatId, CreatedAt = time.GetUtcNow() };
                    db.Channels.Add(channel);
                }

                if (dto.ScheduleReminderMinutes is not (5 or 10 or 15 or 30 or 60))
                {
                    return Error("Оберіть час попередження: 5, 10, 15, 30 або 60 хвилин.");
                }
                if (dto.QuietHoursStartMinute is < 0 or >= 1440
                    || dto.QuietHoursEndMinute is < 0 or >= 1440
                    || (dto.QuietHoursEnabled && dto.QuietHoursStartMinute == dto.QuietHoursEndMinute))
                {
                    return Error("Оберіть коректний проміжок тихих годин.");
                }

                if (channel.Addresses.Count > 0 && region is not null && channel.RegionId != region.Id && dto.Addresses is null)
                {
                    return Error("Спершу приберіть адреси поточного регіону.");
                }

                var before = (WasActive: !isNew && channel.IsActive, channel.IsPaused, channel.RegionId, channel.PartIds, channel.SlotSettings, channel.GroupId);

                if (dto.Addresses is not null)
                {
                    if (region is null) return Error("Оберіть регіон.");
                    var snapshot = region.HasSchedules ? await schedules.GetAsync(region.Id, ct) : null;
                    var addressError = ValidateAddresses(dto.Addresses, channel, region, snapshot);
                    if (addressError is not null) return Error(addressError);
                }

                if (region is not null)
                {
                    // A group belongs to one region's power network.
                    if (!string.Equals(channel.RegionId, region.Id, StringComparison.Ordinal))
                    {
                        channel.GroupId = null;
                        channel.AddressLabel = null;
                        channel.AddressCity = null;
                        channel.AddressStreet = null;
                        channel.AddressHouse = null;
                        channel.ScheduleNotify = false;
                        channel.ScheduleReminder = false;
                    }

                    channel.RegionId = region.Id;
                    channel.PartId = null;
                    channel.PartIdList = partIds;
                }

                if (dto.Addresses is not null)
                    ApplyAddresses(channel, dto.Addresses);

                // Switching the subscription off also removes the saved address and everything that depends on it.
                if (channel.IsActive && !dto.IsActive)
                {
                    channel.Addresses.Clear();
                    channel.GroupId = null;
                    channel.AddressLabel = null;
                    channel.AddressCity = null;
                    channel.AddressStreet = null;
                    channel.AddressHouse = null;
                    channel.OutageTrigger = false;
                    channel.ScheduleNotify = false;
                    channel.ScheduleReminder = false;
                }

                channel.IsActive = dto.IsActive;
                channel.IsPaused = dto.IsPaused;
                channel.Filter = dto.Filter;
                channel.DeletePreviousAlert = dto.DeletePreviousAlert ?? channel.DeletePreviousAlert;
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
                channel.ScheduleReminder = canSchedule && dto.ScheduleReminder;
                channel.ScheduleReminderMinutes = dto.ScheduleReminderMinutes;
                channel.QuietHoursEnabled = dto.QuietHoursEnabled;
                channel.QuietHoursStartMinute = dto.QuietHoursStartMinute;
                channel.QuietHoursEndMinute = dto.QuietHoursEndMinute;
                channel.ScheduleImage = dto.ScheduleImage;
                channel.ScheduleEventMode = dto.ScheduleEventMode;
                channel.SchedulePost = canSchedule && dto.SchedulePost;
                channel.SchedulePostMode = dto.SchedulePostMode;
                channel.SchedulePostImage = dto.SchedulePostImage;
                if (dto.Addresses is not null || before.RegionId != channel.RegionId || (before.WasActive && !channel.IsActive))
                    await ReconcileAddressStateAsync(db, channel, time.GetUtcNow(), ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                // Switched on, moved or re-tuned during an ongoing emergency: deliver what is already active.
                if (channel.IsActive && (!before.WasActive
                    || before.IsPaused != channel.IsPaused
                    || before.RegionId != channel.RegionId
                    || before.PartIds != channel.PartIds
                    || before.SlotSettings != channel.SlotSettings
                    || before.GroupId != channel.GroupId
                    || dto.Addresses is not null))
                {
                    await catchUp.RunAsync(channel, ct);
                }

                return Json(ToSummary(channel, userId));
            }
            finally { gate.Release(); }
        });

        api.MapPost("/preview", async (
            HttpContext http,
            IOptionsMonitor<DtekSettings> settings,
            TemplatePreviewService previews,
            IScheduleProvider schedules,
            TimeProvider time,
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
            var lineTemplate = PlaceholderNames.ToCanonical(request.ScheduleLineTemplate);
            var headingTemplate = PlaceholderNames.ToCanonical(request.ScheduleHeadingTemplate);
            if (!string.IsNullOrWhiteSpace(headingTemplate))
            {
                var headingErrors = MessageTemplate.Validate(headingTemplate, MessageSlots.ScheduleHeading);
                if (headingErrors.Count > 0) return Json(new PreviewResponse("", headingErrors));
            }
            if (!string.IsNullOrWhiteSpace(lineTemplate))
            {
                var lineErrors = MessageTemplate.Validate(lineTemplate, MessageSlots.ScheduleLine);
                if (lineErrors.Count > 0) return Json(new PreviewResponse("", lineErrors));
            }
            var errors = string.IsNullOrWhiteSpace(previewTemplate)
                ? new List<string>()
                : MessageTemplate.Validate(previewTemplate, slot).Select(e => PlaceholderNames.ToDisplay(e)).ToList();
            if (errors.Count > 0)
            {
                return Json(new PreviewResponse("", errors));
            }

            if (slot == MessageSlots.Schedule && request.Addresses is { Count: > 0 })
            {
                if (request.Addresses.Count > 3 || request.Addresses.Any(a => a is null || a.RegionId != region.Id || a.Groups is null))
                    return Error("Перевірте адреси та регіон.");
                var channel = new Channel { RegionId = region.Id, PartIdList = wantedParts.ToList() };
                foreach (var a in request.Addresses)
                    channel.Addresses.Add(new ChatAddress { Name = a.Name, RegionId = region.Id, Label = a.Label,
                        City = a.City, Street = a.Street, House = a.House, PartIds = a.PartIds,
                        Groups = a.Groups.Where(g => g is not null).DistinctBy(g => g.GroupId)
                            .Select(g => new ChatAddressGroup { GroupId = g.GroupId, Label = g.Label }).ToList() });
                if (channel.GroupIds().Count > 0)
                {
                    channel.TemplateMap = string.IsNullOrWhiteSpace(previewTemplate) ? new Dictionary<string, string>()
                        : new Dictionary<string, string> { [MessageSlots.Schedule] = previewTemplate };
                    if (!string.IsNullOrWhiteSpace(lineTemplate))
                        channel.TemplateMap = channel.TemplateMap.Append(new KeyValuePair<string, string>(MessageSlots.ScheduleLine, lineTemplate))
                            .ToDictionary(p => p.Key, p => p.Value);
                    if (!string.IsNullOrWhiteSpace(headingTemplate))
                        channel.TemplateMap = channel.TemplateMap.Append(new KeyValuePair<string, string>(MessageSlots.ScheduleHeading, headingTemplate))
                            .ToDictionary(p => p.Key, p => p.Value);
                    var snapshot = await schedules.GetAsync(region.Id, ct);
                    if (snapshot is null) return Error("Графіки тимчасово недоступні.");
                    var html = CombinedSchedule.Render(channel, snapshot, region.Name, time.GetUtcNow(), false,
                        region.DisplayNames(channel.DistrictIds()));
                    var image = channel.IsCombined() ? null
                        : ScheduleText.ImageUrl(settings.CurrentValue.Schedules, snapshot.DataFile, channel.GroupIds()[0],
                            snapshot.Version, request.ImageKind ?? ScheduleImage.Grid);
                    return Json(new PreviewResponse(html, [], image));
                }
            }

            // The legacy single-template call has no slot: its Kind picks the sample.
            var kind = string.IsNullOrWhiteSpace(request.Slot) ? request.Kind : (AlertKind?)null;
            var rendered = await previews.RenderAsync(
                slot,
                previewTemplate,
                region,
                wantedParts,
                request.GroupId,
                request.Addresses is { Count: > 0 } ? new Channel { Addresses = request.Addresses.Select(a =>
                    new ChatAddress { City = a.City, Street = a.Street, House = a.House, Label = a.Label }).ToList() }.AddressText() : null,
                request.ImageKind ?? ScheduleImage.Grid,
                kind,
                ct,
                lineTemplate,
                headingTemplate);
            return Json(new PreviewResponse(rendered.Html, errors, rendered.ImageUrl));
        });

        api.MapPost("/chats/{chatId:long}/schedules/send", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            ScheduleNotifier notifier,
            CancellationToken ct) =>
        {
            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            if (channel is null || string.IsNullOrEmpty(channel.RegionId) || string.IsNullOrEmpty(channel.GroupId))
            {
                return Error("Спершу оберіть регіон і чергу.");
            }

            await DtekBot.Api.Bot.TickGate.Schedules.WaitAsync(ct);
            try
            {
                var result = await notifier.SendScheduleNowAsync(channel, ct);
                return result.Sent
                    ? Json(new { message = result.Message })
                    : Results.Json(new ApiError(result.Message), ApiJson.Options,
                        statusCode: result.Failed ? 502 : 503);
            }
            finally
            {
                DtekBot.Api.Bot.TickGate.Schedules.Release();
            }
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

            var combined = slot == MessageSlots.Schedule && channel.IsCombined()
                ? await notifier.PreviewCombinedAsync(channel, ct) : null;
            var rendered = await previews.RenderAsync(
                slot,
                template,
                region,
                channel.PartIdList,
                channel.GroupId,
                channel.AddressText(),
                request?.ImageKind ?? (slot == MessageSlots.Schedule ? channel.SchedulePostImage : ScheduleImage.None),
                null,
                ct,
                channel.TemplateFor(MessageSlots.ScheduleLine),
                channel.TemplateFor(MessageSlots.ScheduleHeading));

            try
            {
                await notifier.SendNewAsync(chatId, "🧪 <b>Тест</b>\n\n" + (combined ?? rendered.Html),
                    combined is null ? rendered.ImageUrl : null, ct);
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
            string? regionId,
            string? partIds,
            IOptionsMonitor<DtekSettings> settings,
            CancellationToken ct) =>
        {
            if (!await access.CanManageAsync(chatId, UserId(http), ct))
            {
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            var targetRegionId = string.IsNullOrWhiteSpace(regionId) ? channel?.RegionId : regionId;
            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, targetRegionId, StringComparison.OrdinalIgnoreCase));
            IReadOnlyList<string>? requestedParts = null;
            if (partIds is not null && region is not null)
            {
                var requested = partIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var knownParts = region.Parts.ToDictionary(p => p.Id, p => p.Id, StringComparer.OrdinalIgnoreCase);
                if (requested.Any(id => !knownParts.ContainsKey(id)))
                {
                    return Error("Невідомий район.");
                }

                requestedParts = requested.Select(id => knownParts[id]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }

            return Json(new ChatStatusDto(await status.BuildAsync(channel, ct, regionId, requestedParts),
                await status.DiagnosticsAsync(channel, ct)));
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

        api.MapGet("/address/cities", async (
            string regionId,
            string? query,
            IOptionsMonitor<DtekSettings> settings,
            IDtekAddressClient dtek,
            CancellationToken ct) =>
        {
            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
            if (region is null || string.IsNullOrWhiteSpace(region.SiteUrl) || region.HasGroups)
            {
                return Error("Пошук населених пунктів для цього регіону недоступний.");
            }

            var search = query?.Trim() ?? "";
            if (search.Length > 64)
            {
                return Error("Пошуковий запит задовгий.");
            }

            if (search.Length < 2)
            {
                return Json(Array.Empty<AddressNameDto>());
            }

            try
            {
                return Json((await dtek.SearchCitiesAsync(region, search, ct))
                    .Select(name => new AddressNameDto(name)).ToList());
            }
            catch (DtekAddressUnavailableException ex)
            {
                return Results.Json(new ApiError(ex.Message), ApiJson.Options, statusCode: 503);
            }
        });

        api.MapGet("/address/streets", async (
            string regionId,
            string? city,
            string? query,
            IYasnoClient yasno,
            IDtekAddressClient dtek,
            IOptionsMonitor<DtekSettings> settings,
            CancellationToken ct) =>
        {
            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
            if (region is null || string.IsNullOrWhiteSpace(region.SiteUrl))
            {
                return Error("Для цього регіону пошук за адресою недоступний.");
            }

            var search = query?.Trim() ?? "";
            if (search.Length > 64)
            {
                return Error("Пошуковий запит задовгий.");
            }

            if (search.Length < 2)
            {
                return Json(Array.Empty<AddressItemDto>());
            }

            if (region.HasGroups)
            {
                var items = await yasno.SearchStreetsAsync(region.YasnoRegionId, region.YasnoDsoId, search, ct);
                return Json(items.Take(20).Select(i => new AddressItemDto(i.Id, i.Value)).ToList());
            }

            if (string.IsNullOrWhiteSpace(city))
            {
                return Error("Спершу оберіть населений пункт.");
            }

            try
            {
                return Json((await dtek.SearchStreetsAsync(region, city, search, ct))
                    .Select(name => new AddressNameDto(name)).ToList());
            }
            catch (DtekAddressUnavailableException ex)
            {
                return Results.Json(new ApiError(ex.Message), ApiJson.Options, statusCode: 503);
            }
        });

        api.MapGet("/address/houses", async (
            string regionId,
            int? streetId,
            string? city,
            string? street,
            string? query,
            IYasnoClient yasno,
            BotDbContext db,
            IOptionsMonitor<DtekSettings> settings,
            CancellationToken ct) =>
        {
            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
            if (region is null || string.IsNullOrWhiteSpace(region.SiteUrl))
            {
                return Error("Для цього регіону пошук за адресою недоступний.");
            }

            if (region.HasGroups)
            {
                if (streetId is null)
                {
                    return Error("Оберіть вулицю.");
                }

                var items = (await yasno.SearchHousesAsync(
                    region.YasnoRegionId, region.YasnoDsoId, streetId.Value, query?.Trim() ?? "", ct))
                    .Take(50)
                    .ToArray();
                var addresses = new AddressItemDto[items.Length];
                await Parallel.ForEachAsync(
                    Enumerable.Range(0, items.Length),
                    new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
                    async (index, token) =>
                    {
                        var item = items[index];
                        var group = await yasno.GetGroupAsync(
                            region.YasnoRegionId, region.YasnoDsoId, streetId.Value, item.Id, token);
                        addresses[index] = new AddressItemDto(item.Id, item.Value, group?.Id);
                    });
                return Json(addresses);
            }

            if (string.IsNullOrWhiteSpace(city) || city.Length > 128
                || string.IsNullOrWhiteSpace(street) || street.Length > 256)
            {
                return Error("Оберіть населений пункт і вулицю.");
            }

            if (string.IsNullOrWhiteSpace(query))
            {
                return Json(Array.Empty<DtekAddressHouseDto>());
            }

            if (query.Length > 64)
            {
                return Error("Пошуковий запит задовгий.");
            }

            var knownGroupId = await FindStreetGroupAsync(db, region.Id, city.Trim(), street.Trim(), ct);
            return Json(new[] { new DtekAddressHouseDto(query.Trim(), knownGroupId) });
        });

        api.MapPut("/chats/{chatId:long}/address/dtek", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            IScheduleProvider schedules,
            IOptionsMonitor<DtekSettings> settings,
            AlertCatchUp catchUp,
            CancellationToken ct) =>
        {
            var userId = UserId(http);
            var request = await http.Request.ReadFromJsonAsync<SetDtekAddressRequest>(ApiJson.Options, ct);
            if (request is null)
            {
                return Error("Порожній запит.");
            }

            if (!await access.CanManageAsync(chatId, userId, ct))
            {
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            }

            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, request.RegionId, StringComparison.OrdinalIgnoreCase));
            if (region is null || string.IsNullOrWhiteSpace(region.SiteUrl) || region.HasGroups)
            {
                return Error("Для цього регіону адресу обирають через інше джерело.");
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            if (channel is null || channel.RegionId != region.Id)
            {
                return Error($"Спершу оберіть регіон «{region.Name}» і збережіть налаштування.");
            }

            var parts = new[]
            {
                ManualPart(request.City, 128, "населений пункт"),
                ManualPart(request.Street, 256, "вулицю"),
                ManualPart(request.House, 64, "будинок"),
            };
            var problem = parts.Select(p => p.Error).FirstOrDefault(e => e is not null);
            if (problem is not null)
            {
                return Error(problem);
            }

            var (city, street, house) = (parts[0].Value!, parts[1].Value!, parts[2].Value!);
            var groupId = string.IsNullOrWhiteSpace(request.GroupId)
                ? await FindStreetGroupAsync(db, region.Id, city, street, ct)
                : request.GroupId.Trim();
            var manuallySelectedGroup = !string.IsNullOrWhiteSpace(request.GroupId);

            if (groupId is not null)
            {
                if (!region.HasSchedules)
                {
                    if (manuallySelectedGroup)
                    {
                        return Error("Графіки для цього регіону недоступні.");
                    }

                    groupId = null;
                }
                else
                {
                    var snapshot = await schedules.GetAsync(region.Id, ct);
                    if (snapshot is null)
                    {
                        if (manuallySelectedGroup)
                        {
                            return Results.Json(new ApiError("Графіки тимчасово недоступні. Спробуйте пізніше."), ApiJson.Options, statusCode: 503);
                        }

                        groupId = null;
                    }
                    else if (snapshot.Find(groupId) is null)
                    {
                        if (manuallySelectedGroup)
                        {
                            return Error("У вибраній черзі немає даних графіка. Оберіть її вручну.");
                        }

                        groupId = null;
                    }
                }
            }

            if (channel.IsCombined()) return Error("Змініть адресу у списку адрес.");
            var selected = request.GroupIds?.Distinct(StringComparer.Ordinal).ToArray();
            if (selected is { Length: > 0 })
            {
                var snapshot = region.HasSchedules ? await schedules.GetAsync(region.Id, ct) : null;
                if (selected.Length > 12 || snapshot is null || selected.Any(id => string.IsNullOrWhiteSpace(id) || snapshot.Find(id) is null))
                    return Error("Перевірте черги обраного регіону.");
                groupId = selected.Order(StringComparer.Ordinal).First();
            }
            channel.GroupId = groupId;
            channel.AddressCity = city;
            channel.AddressStreet = street;
            channel.AddressHouse = house;
            var label = $"{city}, {street}, {house}";
            channel.AddressLabel = label.Length > 256 ? label[..256] : label;
            channel.UpdateLegacyPrimary();
            if (selected is { Length: > 1 })
            {
                var address = channel.Addresses.First();
                address.Groups.Clear();
                address.Groups.AddRange(selected.Select(id => new ChatAddressGroup { GroupId = id }));
            }
            await db.SaveChangesAsync(ct);
            await catchUp.RunAsync(channel, ct);
            return Json(ToSummary(channel, userId));
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
            AlertCatchUp catchUp,
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
            if (channel.IsCombined()) return Error("Змініть адресу у списку адрес.");
            channel.GroupId = group.Id;
            channel.AddressLabel = label.Length > 256 ? label[..256] : label;
            channel.AddressCity = null;
            channel.AddressStreet = CleanPart(request.Street, 256);
            channel.AddressHouse = CleanPart(request.House, 64);
            channel.UpdateLegacyPrimary();
            await db.SaveChangesAsync(ct);
            await catchUp.RunAsync(channel, ct);
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

            if (channel.IsCombined()) return Error("Приберіть адресу зі списку адрес.");
            channel.Addresses.Clear();
            channel.GroupId = null;
            channel.AddressLabel = null;
            channel.AddressCity = null;
            channel.AddressStreet = null;
            channel.AddressHouse = null;
            channel.ScheduleNotify = false;
            channel.SchedulePost = false;
            await db.SaveChangesAsync(ct);
            return Json(ToSummary(channel, userId));
        });

        // Address typed by hand for regions without the picker: city, street and house exactly as on the DTEK site.
        api.MapPut("/chats/{chatId:long}/address/manual", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            IOptionsMonitor<DtekSettings> settings,
            CancellationToken ct) =>
        {
            var userId = UserId(http);
            var request = await http.Request.ReadFromJsonAsync<ManualAddressRequest>(ApiJson.Options, ct);
            if (request is null)
            {
                return Error("Порожній запит.");
            }

            if (!await access.CanManageAsync(chatId, userId, ct))
            {
                return Results.Json(new ApiError("Немає доступу до цього чату."), ApiJson.Options, statusCode: 403);
            }

            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                string.Equals(r.Id, request.RegionId, StringComparison.OrdinalIgnoreCase));
            if (region is null)
            {
                return Error("Невідомий регіон.");
            }

            if (region.HasGroups)
            {
                return Error("Для цього регіону адресу обирають зі списку.");
            }
            if (string.IsNullOrWhiteSpace(region.SiteUrl))
            {
                return Error("Для цього регіону перевірка адреси недоступна.");
            }

            var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
            if (channel is null || channel.RegionId != region.Id)
            {
                return Error($"Спершу оберіть регіон «{region.Name}» і збережіть налаштування.");
            }

            var parts = new[]
            {
                ManualPart(request.City, 128, "населений пункт"),
                ManualPart(request.Street, 256, "вулицю"),
                ManualPart(request.House, 64, "будинок"),
            };
            var problem = parts.Select(p => p.Error).FirstOrDefault(e => e is not null);
            if (problem is not null)
            {
                return Error(problem);
            }

            if (channel.IsCombined()) return Error("Змініть адресу у списку адрес.");
            var (city, street, house) = (parts[0].Value!, parts[1].Value!, parts[2].Value!);
            var label = $"{city}, {street}, {house}";
            channel.AddressCity = city;
            channel.AddressStreet = street;
            channel.AddressHouse = house;
            channel.AddressLabel = label.Length > 256 ? label[..256] : label;
            channel.UpdateLegacyPrimary();
            await db.SaveChangesAsync(ct);
            return Json(ToSummary(channel, userId));
        });

        // Forgets the typed address only; the group and every other setting stay.
        api.MapDelete("/chats/{chatId:long}/address/manual", async (
            long chatId,
            HttpContext http,
            BotDbContext db,
            ChatAccessService access,
            TimeProvider time,
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

            if (channel.IsCombined()) return Error("Приберіть адресу зі списку адрес.");
            channel.AddressLabel = null;
            channel.AddressCity = null;
            channel.AddressStreet = null;
            channel.AddressHouse = null;

            channel.UpdateLegacyPrimary();

            // A queued check is about the address that was just removed.
            var pending = await db.AddressChecks
                .Where(c => c.ChatId == chatId && c.Status == AddressCheckStatus.Pending)
                .ToListAsync(ct);
            foreach (var check in pending)
            {
                check.Status = AddressCheckStatus.Expired;
                check.CompletedAt = time.GetUtcNow();
            }

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
            AlertCatchUp catchUp,
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
            if (channel.IsCombined()) return Error("Змініть черги у списку адрес.");
            channel.GroupId = request.GroupId;
            channel.UpdateLegacyPrimary();
            await db.SaveChangesAsync(ct);
            await catchUp.RunAsync(channel, ct);
            return Json(ToSummary(channel, userId));
        });

        api.MapGet("/schedules/view", async (
            string regionId, string groupId,
            IOptionsMonitor<DtekSettings> settings, IScheduleProvider schedules, BotDbContext db,
            TimeProvider time, CancellationToken ct) =>
        {
            var region = settings.CurrentValue.Regions.FirstOrDefault(r =>
                r.HasSchedules && string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
            if (region is null) return Error("Для цього регіону графіки недоступні.");
            var snapshot = await schedules.GetAsync(region.Id, ct);
            if (snapshot is null)
                return Results.Json(new ApiError("Графіки тимчасово недоступні. Спробуйте пізніше."), ApiJson.Options, statusCode: 503);
            var group = snapshot.Find(groupId);
            if (group is null) return Error("Для цієї черги немає даних графіка.");
            var now = time.GetUtcNow();
            var today = KyivTime.FromUtc(now).Date;
            var days = Enumerable.Range(0, 2).Select(offset =>
            {
                var date = today.AddDays(offset);
                var start = ScheduleText.KyivDayStart(date);
                var end = ScheduleText.KyivDayStart(date.AddDays(1));
                var available = snapshot.CoversDay(groupId, start, end);
                return new ScheduleViewDayDto(date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    available, available ? group.Outages.Where(o => o.Start < end && o.End > start)
                        .Select(o => new ScheduleViewIntervalDto(o.Start > start ? o.Start : start, o.End < end ? o.End : end))
                        .ToArray() : []);
            }).ToArray();
            var status = snapshot.StatusAt(groupId, now);
            var next = status is null ? null : status.IsOff ? status.Until : status.NextStart;
            var warnings = new List<string>();
            var popup = await db.RegionStates.AsNoTracking().FirstOrDefaultAsync(s => s.RegionId == region.Id, ct);
            if (popup?.Kind is Parsing.PopupKind.Emergency or Parsing.PopupKind.Accident)
                warnings.Add(popup.PartIdList is { Count: > 0 } affected
                    ? $"ДТЕК повідомляє про екстрені відключення або аварію: {string.Join(", ", region.DisplayNames(affected))}. План у цих районах може не діяти."
                    : "У регіоні є повідомлення ДТЕК про екстрені відключення або аварію. План може не діяти; перевірте, яких районів стосується повідомлення.");
            var grid = await db.GridStates.AsNoTracking().FirstOrDefaultAsync(s => s.RegionId == region.Id, ct);
            if (grid?.IsAlert == true && now - grid.FeedGeneratedAt <= TimeSpan.FromMinutes(settings.CurrentValue.Grid.MaxAgeMinutes))
                warnings.Add("Укренерго повідомляє про екстрені обмеження або пошкодження мережі. План може не діяти.");
            var groupState = await db.GroupStates.AsNoTracking().FirstOrDefaultAsync(s => s.RegionId == region.Id && s.GroupId == groupId, ct);
            if (groupState?.IsEmergency == true)
                warnings.Add($"Останній запис Yasno для черги {groupId} повідомляє про екстрені відключення (оновлено {KyivTime.Format(groupState.UpdatedAt)}). План може не діяти.");
            return Json(new ScheduleViewDto(region.Id, region.Name, groupId, now, snapshot.FetchedAt,
                now - snapshot.FetchedAt > TimeSpan.FromMinutes(settings.CurrentValue.Schedules.CacheMinutes),
                days, next, next is null ? null : !status!.IsOff, warnings));
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

    private static (string? Value, string? Error) ManualPart(string? value, int max, string what)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return (null, $"Вкажіть {what}.");
        }

        if (text.Length > max)
        {
            return (null, $"Поле «{what}» задовге: щонайбільше {max} символів.");
        }

        return text.Any(char.IsControl) ? (null, $"Поле «{what}» містить недопустимі символи.") : (text, null);
    }

    private static RegionSettings? GroupRegion(DtekSettings settings, string? regionId) =>
        settings.Regions.FirstOrDefault(r => r.HasGroups && string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));

    private static async Task<string?> FindStreetGroupAsync(
        BotDbContext db, string regionId, string city, string street, CancellationToken ct)
    {
        var groupIds = await db.Channels.AsNoTracking()
            .Where(channel => channel.RegionId == regionId
                && channel.AddressCity == city
                && channel.AddressStreet == street
                && channel.GroupId != null)
            .Select(channel => channel.GroupId!)
            .Distinct()
            .Take(2)
            .ToListAsync(ct);

        return groupIds.Count == 1 ? groupIds[0] : null;
    }

    private static string? ValidateAddresses(
        IReadOnlyList<ChatAddressDto> addresses, Channel channel, RegionSettings region, ScheduleSnapshot? snapshot)
    {
        if (addresses.Count > 3) return "Можна зберегти до 3 адрес.";
        var ids = new HashSet<long>();
        foreach (var item in addresses)
        {
            if (item is null || item.RegionId != region.Id)
                return "Усі адреси мають бути в одному регіоні.";
            if (item.Id != 0 && (!ids.Add(item.Id) || !channel.Addresses.Any(a => a.Id == item.Id && a.RegionId == region.Id)))
                return "Адресу не знайдено.";
            if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 64 || item.Name.Any(char.IsControl)
                || item.City?.Length > 128 || item.Street?.Length > 256 || item.House?.Length > 64 || item.Label?.Length > 256
                || new[] { item.City, item.Street, item.House, item.Label }.Any(t => t?.Any(char.IsControl) == true)
                || item.Groups is null || item.Groups.Count > 12)
                return "Перевірте назву, адресу та черги.";
            if (item.PartIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Any(id => !region.Parts.Any(p => p.Id == id)) == true)
                return "Невідомий район.";
            foreach (var group in item.Groups)
            {
                if (group is null || !System.Text.RegularExpressions.Regex.IsMatch(group.GroupId ?? "", @"^\d{1,2}\.\d{1,2}$")
                    || group.Label?.Length > 64 || group.Label?.Any(char.IsControl) == true
                    || !region.HasSchedules || (snapshot is not null && snapshot.Find(group.GroupId!) is null))
                    return "Перевірте черги обраного регіону.";
            }
        }
        return null;
    }

    private static void ApplyAddresses(Channel channel, IReadOnlyList<ChatAddressDto> addresses)
    {
        var ids = addresses.Where(a => a.Id != 0).Select(a => a.Id).ToHashSet();
        foreach (var removed in channel.Addresses.Where(a => !ids.Contains(a.Id)).ToArray())
            channel.Addresses.Remove(removed);
        foreach (var item in addresses)
        {
            var address = item.Id == 0 ? new ChatAddress { ChatId = channel.ChatId } : channel.Addresses.First(a => a.Id == item.Id);
            if (item.Id == 0) channel.Addresses.Add(address);
            address.RegionId = channel.RegionId; address.Name = item.Name.Trim();
            address.City = item.City?.Trim(); address.Street = item.Street?.Trim(); address.House = item.House?.Trim();
            address.Label = item.Label ?? string.Join(", ", new[] { address.City, address.Street, address.House }.Where(v => !string.IsNullOrEmpty(v)));
            address.PartIds = item.PartIds;
            var wanted = item.Groups.DistinctBy(g => g.GroupId).ToDictionary(g => g.GroupId);
            foreach (var old in address.Groups.Where(g => !wanted.ContainsKey(g.GroupId)).ToArray()) address.Groups.Remove(old);
            foreach (var group in wanted.Values)
            {
                var saved = address.Groups.FirstOrDefault(g => g.GroupId == group.GroupId);
                if (saved is null) { saved = new ChatAddressGroup { GroupId = group.GroupId }; address.Groups.Add(saved); }
                saved.Label = group.Label?.Trim();
            }
        }
        channel.MirrorPrimary();
    }

    private static async Task ReconcileAddressStateAsync(BotDbContext db, Channel channel, DateTimeOffset now, CancellationToken ct)
    {
        var keys = channel.GroupIds().Select(id => AlertDispatcher.GroupDeliveryKey(channel.RegionId, id)).ToArray();
        foreach (var delivery in await db.Deliveries.Where(d => d.ChatId == channel.ChatId && !d.IsClosed
            && d.RegionId.Contains(":group:") && !keys.Contains(d.RegionId)).ToListAsync(ct))
            delivery.IsClosed = true;
        foreach (var check in await db.AddressChecks.Where(c => c.ChatId == channel.ChatId && c.Status == AddressCheckStatus.Pending).ToListAsync(ct))
        {
            if (check.RegionId == channel.RegionId && channel.Addresses.Any(a => (check.AddressId is null || a.Id == check.AddressId)
                && (a.City ?? "") == check.City && a.Street == check.Street && a.House == check.House)) continue;
            check.Status = AddressCheckStatus.Expired;
            check.CompletedAt = now;
        }
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
                .ToDictionary(s => s.Id, s => new SlotSettingDto(c.SlotEnabled(s.Id), c.SlotEditInPlace(s.Id), c.SlotEndStyle(s.Id))),
            !string.IsNullOrEmpty(c.SvitlobotKey),
            c.AddressCity,
            c.ScheduleReminder,
            c.ScheduleReminderMinutes,
            c.QuietHoursEnabled,
            c.QuietHoursStartMinute,
            c.QuietHoursEndMinute,
            c.DeletePreviousAlert,
            c.Addresses.Count > 0 ? c.Addresses.OrderBy(a => a.Id).Select(a => a.ToDto()).ToArray()
                : c.GroupId is not null || c.AddressLabel is not null || c.AddressStreet is not null
                    ? [new ChatAddressDto(0, c.RegionId, "Дім", c.AddressCity, c.AddressStreet, c.AddressHouse,
                        c.GroupId is null ? [] : [new ChatAddressGroupDto(c.GroupId)], c.PartIds, c.AddressLabel)] : [],
            c.SvitlobotGroup());
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
