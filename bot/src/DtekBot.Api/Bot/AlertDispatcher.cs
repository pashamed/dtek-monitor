using System.Text.RegularExpressions;
using System.Text.Json;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DtekBot.Api.Bot;

public sealed record DispatchSummary(int Sent, int Edited, int Closed, int Failed);

public sealed class AlertDispatcher(
    ITelegramBotClient bot,
    BotDbContext db,
    TimeProvider time,
    ILogger<AlertDispatcher> logger,
    PrivateNotificationMirror mirror)
{
    private const int MaxRetryAfterSeconds = 10;
    private static readonly TimeSpan TelegramDeleteWindow = TimeSpan.FromHours(48);
    private static readonly TimeSpan SendSpacing = TimeSpan.FromMilliseconds(50);
    private static readonly LinkPreviewOptions NoPreview = new() { IsDisabled = true };

    private enum Outcome
    {
        Failed,
        Unchanged,
        Sent,
        Edited,
        Closed,
    }

    public async Task<DispatchSummary> DispatchAsync(
        RegionSettings region,
        PopupClassification result,
        string? popupText,
        CancellationToken ct,
        AlertSource source = AlertSource.None,
        bool regionWideActive = false,
        PopupClassification? regionWideFallback = null,
        string? regionWideFallbackText = null)
    {
        int sent = 0, edited = 0, closed = 0, failed = 0;
        var now = time.GetUtcNow();
        await mirror.RetryPendingAsync(ct);

        var channels = await db.Channels
            .Where(c => c.IsActive && (c.Addresses.Any(a => a.RegionId == region.Id)
                || (!c.Addresses.Any() && c.RegionId == region.Id)))
            .ToListAsync(ct);
        foreach (var saved in channels)
        {
            if (saved.RegionId == region.Id) region.UpgradeRetiredParts(saved);
        }
        channels = channels.Select(c => c.ForRegion(region.Id)).ToList();
        foreach (var target in channels) region.UpgradeRetiredParts(target);

        var openDeliveries = await db.Deliveries
            .Where(d => d.RegionId == region.Id && !d.IsClosed)
            .ToListAsync(ct);

        // Threads whose chat left the region or unsubscribed are closed without a message.
        var activeChats = channels.Select(c => c.ChatId).ToHashSet();
        foreach (var stale in openDeliveries.Where(d => !activeChats.Contains(d.ChatId)))
        {
            stale.IsClosed = true;
            stale.UpdatedAt = now;
        }

        foreach (var channel in channels)
        {
            var resolved = result.Resolve(channel.PopupDistrictIds());
            var kind = resolved.Kind == PopupKind.Emergency ? AlertKind.Emergency
                : resolved.Kind == PopupKind.Accident ? AlertKind.Accident : AlertKind.Unknown;
            var delivery = openDeliveries.FirstOrDefault(d => d.ChatId == channel.ChatId);
            var slot = SlotFor(kind, false);
            var slotOn = channel.SlotEnabled(slot);
            var takesRegional = channel.TakesRegionalAlerts();
            var eligible = resolved.IsAlert
                && takesRegional
                && !channel.IsPaused
                && Matches(channel, resolved)
                && slotOn
                && !(resolved.Kind == PopupKind.Unknown && regionWideFallback is { IsAlert: true } unknownFallback
                    && Matches(channel, unknownFallback));

            Outcome outcome;
            if (eligible)
            {
                var text = Render(channel, region, kind, PartNames(region, resolved), popupText, now, source: source, affectedPartIds: resolved.PartIds);
                outcome = await TrySendAsync(
                    channel,
                    () => SendRegionalAsync(channel, region, resolved, slot, delivery, text, now, ct),
                    ct);
            }
            else if (regionWideFallback is { IsAlert: true } fallback
                && takesRegional
                && !channel.IsPaused
                && channel.SlotEnabled(SlotFor(fallback.Kind == PopupKind.Emergency ? AlertKind.Emergency
                    : fallback.Kind == PopupKind.Accident ? AlertKind.Accident : AlertKind.Unknown, false))
                && Matches(channel, fallback))
            {
                var fallbackKind = fallback.Kind == PopupKind.Emergency ? AlertKind.Emergency
                    : fallback.Kind == PopupKind.Accident ? AlertKind.Accident
                    : AlertKind.Unknown;
                var fallbackSlot = SlotFor(fallbackKind, false);
                var text = Render(channel, region, fallbackKind, PartNames(region, fallback), regionWideFallbackText, now,
                    source: AlertSource.Ukrenergo, affectedPartIds: fallback.PartIds);
                outcome = await TrySendAsync(
                    channel,
                    () => SendRegionalAsync(channel, region, fallback, fallbackSlot, delivery, text, now, ct),
                    ct);
            }
            else if (delivery is not null && resolved.IsAlert && regionWideActive && slotOn && !channel.IsPaused && takesRegional)
            {
                // Narrowed out by the districts of the newer popup while the region-wide alert is still on:
                // the chat keeps its open thread, so the alert is neither repeated later nor announced as ended.
                continue;
            }
            else if (delivery is not null)
            {
                // The DTEK alert is gone but Yasno still reports an emergency for a group it covered:
                // the same message carries on as that group's alert instead of an end notice and a new post.
                var (coveredGroup, groupThreadOpen) = channel.TakesGroupAlerts()
                    ? await CoveredGroupEmergencyAsync(channel, region, delivery, ct)
                    : (null, false);
                if (coveredGroup is not null && !groupThreadOpen && !channel.IsPaused && delivery.TelegramMessageId > 0)
                {
                    delivery.RegionId = GroupDeliveryKey(region.Id, coveredGroup);
                    delivery.ContentHash = "";
                    delivery.EmergencyPartsJson = null;
                    delivery.UpdatedAt = now;
                    logger.LogInformation("Chat {ChatId}: regional alert {MessageId} continues as group {Region}/{Group} emergency",
                        channel.ChatId, delivery.TelegramMessageId, region.Id, coveredGroup);
                    continue;
                }

                // A switched-off alert type or source closes its open thread silently.
                var announce = takesRegional && !channel.IsPaused && channel.SlotEnabled(MessageSlots.Ended)
                    && !(resolved.IsAlert && !slotOn)
                    && coveredGroup is null;
                outcome = announce
                    ? await TrySendAsync(
                        channel,
                        () => SendEndAsync(channel, region, delivery, OwnPartNames(region, channel, result), popupText, now, ct,
                            stabilizationActive: resolved.Kind == PopupKind.Stabilization, source: source),
                        ct)
                    : Outcome.Closed;

                // Close even on failure so a dead chat is not retried forever.
                delivery.IsClosed = true;
                delivery.UpdatedAt = now;
            }
            else
            {
                continue;
            }

            switch (outcome)
            {
                case Outcome.Sent: sent++; break;
                case Outcome.Edited: edited++; break;
                case Outcome.Closed: closed++; break;
                case Outcome.Failed: failed++; break;
            }

            if (outcome is not Outcome.Unchanged)
            {
                await Task.Delay(SendSpacing, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        return new DispatchSummary(sent, edited, closed, failed);
    }

    // Group alerts keep their own delivery threads, keyed apart from the region-wide ones.
    public static string GroupDeliveryKey(string regionId, string groupId) => $"{regionId}:group:{groupId}";

    public async Task<DispatchSummary> DispatchGroupAsync(
        RegionSettings region,
        string groupId,
        bool isEmergency,
        CancellationToken ct)
    {
        int sent = 0, edited = 0, closed = 0, failed = 0;
        var now = time.GetUtcNow();
        var key = GroupDeliveryKey(region.Id, groupId);
        var hash = $"group:{groupId}:{(isEmergency ? "emergency" : "ok")}";
        await mirror.RetryPendingAsync(ct);

        var channels = await db.Channels
            .Where(c => c.IsActive && (c.Addresses.Any(a => a.RegionId == region.Id && a.Groups.Any(g => g.GroupId == groupId))
                || (!c.Addresses.Any() && c.RegionId == region.Id && c.GroupId == groupId)))
            .ToListAsync(ct);
        channels = channels.Select(c => c.ForRegion(region.Id)).ToList();
        var openDeliveries = await db.Deliveries
            .Where(d => d.RegionId == key && !d.IsClosed)
            .ToListAsync(ct);
        var regionalDeliveries = await db.Deliveries.AsNoTracking()
            .Where(d => d.RegionId == region.Id && !d.IsClosed && d.EmergencyPartsJson != null)
            .ToDictionaryAsync(d => d.ChatId, ct);

        // Chats that dropped this group or unsubscribed: close silently.
        var chatIds = channels.Select(c => c.ChatId).ToHashSet();
        foreach (var stale in openDeliveries.Where(d => !chatIds.Contains(d.ChatId)))
        {
            stale.IsClosed = true;
            stale.UpdatedAt = now;
        }

        // Yasno reports a group, not a district: the group can span the whole city, so the text says so.
        var partNames = new List<string> { $"загально по групі {groupId}" };
        foreach (var channel in channels)
        {
            var delivery = openDeliveries.FirstOrDefault(d => d.ChatId == channel.ChatId);
            if (!channel.TakesGroupAlerts())
            {
                // The chat follows DTEK only: a group thread left from before the switch closes silently.
                if (delivery is not null)
                {
                    delivery.IsClosed = true;
                    delivery.UpdatedAt = now;
                    closed++;
                }
                continue;
            }

            Delivery? regional = null;
            var covered = channel.CombinesAlertSources()
                && regionalDeliveries.TryGetValue(channel.ChatId, out regional)
                && CoversGroup(channel, region, groupId, regional);
            if (isEmergency && covered)
            {
                if (delivery is not null)
                {
                    delivery.IsClosed = true;
                    delivery.UpdatedAt = now;
                    if (channel.DeletePreviousAlert) await CleanupAsync(channel, delivery, now, ct);
                    closed++;
                }
                logger.LogInformation("Chat {ChatId}: group {Region}/{Group} emergency is covered by regional message {MessageId}",
                    channel.ChatId, region.Id, groupId, regional!.TelegramMessageId);
                continue;
            }
            Outcome outcome;
            if (isEmergency && !channel.IsPaused && channel.SlotEnabled(MessageSlots.Local))
            {
                var text = Render(channel, region, AlertKind.Emergency, partNames, null, now, isGroup: true, groupId: groupId);
                outcome = await TrySendAsync(
                    channel,
                    () => SendOrEditAsync(channel, MessageSlots.Local, key, delivery, text, RenderHash(channel, hash, text), now, ct),
                    ct);
            }
            else if (delivery is not null)
            {
                var announce = !channel.IsPaused && channel.SlotEnabled(MessageSlots.Ended)
                    && !(isEmergency && !channel.SlotEnabled(MessageSlots.Local)) && !covered;
                outcome = announce
                    ? await TrySendAsync(
                        channel,
                        () => SendEndAsync(channel, region, delivery, partNames, null, now, ct, isGroup: true, groupId: groupId),
                        ct)
                    : Outcome.Closed;
                delivery.IsClosed = true;
                delivery.UpdatedAt = now;
            }
            else
            {
                continue;
            }

            switch (outcome)
            {
                case Outcome.Sent: sent++; break;
                case Outcome.Edited: edited++; break;
                case Outcome.Closed: closed++; break;
                case Outcome.Failed: failed++; break;
            }

            if (outcome is not Outcome.Unchanged)
            {
                await Task.Delay(SendSpacing, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        return new DispatchSummary(sent, edited, closed, failed);
    }

    private async Task<Outcome> SendRegionalAsync(Channel channel, RegionSettings region, PopupClassification result,
        string slot, Delivery? delivery, string text, DateTimeOffset now, CancellationToken ct)
    {
        var coverage = result.Kind == PopupKind.Emergency
            ? (result.PartIds.Count == 0 ? [] : PopupInterpretation.Expand(region, result.PartIds).ToArray())
            : null;
        if (delivery is null && coverage is not null && channel.CombinesAlertSources())
        {
            // A Yasno group alert the new DTEK alert covers continues as the regional one: one emergency message per chat.
            var prefix = GroupDeliveryKey(region.Id, "");
            var groupThreads = await db.Deliveries
                .Where(d => d.ChatId == channel.ChatId && !d.IsClosed && d.RegionId.StartsWith(prefix))
                .ToListAsync(ct);
            delivery = groupThreads.FirstOrDefault(d => d.TelegramMessageId > 0
                && CoversGroup(channel, region, d.RegionId[prefix.Length..], coverage));
            if (delivery is not null)
            {
                logger.LogInformation("Chat {ChatId}: group alert {MessageId} continues as the regional {Region} alert",
                    channel.ChatId, delivery.TelegramMessageId, region.Id);
                delivery.RegionId = region.Id;
                delivery.ContentHash = "";
            }
        }

        var outcome = await SendOrEditAsync(channel, slot, region.Id, delivery, text,
            RenderHash(channel, result.Hash, text), now, ct);
        var stored = delivery ?? db.Deliveries.Local.Single(d =>
            d.ChatId == channel.ChatId && d.RegionId == region.Id && !d.IsClosed);
        stored.EmergencyPartsJson = coverage is null ? null : JsonSerializer.Serialize(coverage);
        return outcome;
    }

    private static bool CoversGroup(Channel channel, RegionSettings region, string groupId, Delivery delivery)
    {
        if (delivery.TelegramMessageId <= 0 || delivery.EmergencyPartsJson is null) return false;
        var coverage = JsonSerializer.Deserialize<string[]>(delivery.EmergencyPartsJson);
        return coverage is not null && CoversGroup(channel, region, groupId, coverage);
    }

    private static bool CoversGroup(Channel channel, RegionSettings region, string groupId, IReadOnlyCollection<string> coverage)
    {
        if (coverage.Count == 0) return true;
        var addresses = channel.Addresses.Where(a => a.Groups.Any(g => g.GroupId == groupId)).ToArray();
        IEnumerable<IReadOnlyList<string>> scopes = addresses.Length == 0 ? [channel.PartIdList]
            : addresses.Select(a => (IReadOnlyList<string>)(a.PartIds?.Split(',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? channel.PartIdList.ToArray()));
        return scopes.All(scope => scope.Count > 0 && PopupInterpretation.Expand(region, scope)
            .Where(id => region.Parts.FirstOrDefault(p => p.Id == id)?.Children is not { Count: > 0 })
            .All(id => coverage.Contains(id, StringComparer.OrdinalIgnoreCase)));
    }

    // A group the closing regional alert covered that Yasno still reports in emergency, and whether it already has its own thread.
    private async Task<(string? GroupId, bool HasThread)> CoveredGroupEmergencyAsync(
        Channel channel, RegionSettings region, Delivery delivery, CancellationToken ct)
    {
        var covered = channel.GroupIds().Where(id => CoversGroup(channel, region, id, delivery)).ToArray();
        if (covered.Length == 0) return (null, false);
        var states = await db.GroupStates.AsNoTracking().Where(s => s.RegionId == region.Id
            && covered.Contains(s.GroupId)).ToListAsync(ct);
        var keys = covered.Select(id => GroupDeliveryKey(region.Id, id)).ToArray();
        var threads = (await db.Deliveries.AsNoTracking()
            .Where(d => d.ChatId == channel.ChatId && !d.IsClosed && keys.Contains(d.RegionId))
            .Select(d => d.RegionId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var known = states.Select(s => s.GroupId).ToHashSet(StringComparer.Ordinal);
        var active = covered.Where(id => states.Any(s => s.GroupId == id && s.Today == GroupStatuses.Emergency)
            || (!known.Contains(id) && threads.Contains(GroupDeliveryKey(region.Id, id)))).ToArray();
        if (active.Length == 0) return (null, false);
        if (!channel.SlotEnabled(MessageSlots.Local)) return (active[0], true);
        var free = active.FirstOrDefault(id => !threads.Contains(GroupDeliveryKey(region.Id, id)));
        return free is null ? (active[0], true) : (free, false);
    }

    private async Task<Outcome> SendOrEditAsync(
        Channel channel,
        string slot,
        string deliveryKey,
        Delivery? delivery,
        string text,
        string hash,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (delivery is not null && delivery.ContentHash == hash)
        {
            return Outcome.Unchanged;
        }

        // The slot's own choice decides between editing and posting anew; deletion only tidies what posting leaves behind.
        if (delivery is not null && channel.SlotEditInPlace(slot))
        {
            var edited = await WithMarkupFallback(
                text,
                t => bot.EditMessageText(
                    channel.ChatId,
                    delivery.TelegramMessageId,
                    t,
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: NoPreview,
                    cancellationToken: ct),
                channel);
            await mirror.SyncAsync(edited, ct, disableNotification: true);
            delivery.ContentHash = hash;
            delivery.UpdatedAt = now;
            return Outcome.Edited;
        }

        var message = await WithMarkupFallback(
            text,
            t => bot.SendMessage(
                channel.ChatId,
                t,
                parseMode: ParseMode.Html,
                linkPreviewOptions: NoPreview,
                disableNotification: QuietHours.IsQuiet(channel, now),
                cancellationToken: ct),
            channel);
        await mirror.SyncAsync(message, ct, disableNotification: QuietHours.IsQuiet(channel, now));

        if (delivery is not null && channel.DeletePreviousAlert)
        {
            await DeleteMessageAsync(channel, delivery.TelegramMessageId, delivery.SentAt ?? delivery.UpdatedAt, now, ct);
        }

        if (delivery is null)
        {
            if (channel.DeletePreviousAlert) await CleanupClosedAsync(channel, now, ct);
            db.Deliveries.Add(new Delivery
            {
                ChatId = channel.ChatId,
                RegionId = deliveryKey,
                TelegramMessageId = message.Id,
                ContentHash = hash,
                UpdatedAt = now,
                SentAt = now,
            });
            return Outcome.Sent;
        }

        // New-post mode: the latest post becomes the thread the end message replies to.
        delivery.TelegramMessageId = message.Id;
        delivery.ContentHash = hash;
        delivery.UpdatedAt = now;
        delivery.SentAt = now;
        return Outcome.Sent;
    }

    // A new alert thread removes what earlier, already closed threads of the chat left behind: the alert and its end notice.
    private async Task CleanupClosedAsync(Channel channel, DateTimeOffset now, CancellationToken ct)
    {
        var since = now - TelegramDeleteWindow;
        var saved = await db.Deliveries
            .Where(d => d.ChatId == channel.ChatId && d.IsClosed && d.UpdatedAt >= since
                && (d.TelegramMessageId > 0 || d.EndMessageId != null))
            .ToListAsync(ct);
        var pending = db.Deliveries.Local.Where(d => d.ChatId == channel.ChatId && d.IsClosed
            && (d.TelegramMessageId > 0 || d.EndMessageId != null));
        foreach (var closed in saved.Concat(pending).Distinct().ToList())
        {
            await CleanupAsync(channel, closed, now, ct);
        }
    }

    private async Task CleanupAsync(Channel channel, Delivery closed, DateTimeOffset now, CancellationToken ct)
    {
        if (closed.EndMessageId is { } end)
        {
            await DeleteMessageAsync(channel, end, closed.UpdatedAt, now, ct);
        }
        if (closed.TelegramMessageId > 0)
        {
            await DeleteMessageAsync(channel, closed.TelegramMessageId, closed.SentAt ?? closed.UpdatedAt, now, ct);
        }
        // Attempted once: rights or the 48-hour window will not improve on a retry.
        closed.TelegramMessageId = 0;
        closed.EndMessageId = null;
    }

    private async Task DeleteMessageAsync(Channel channel, int messageId, DateTimeOffset sentAt, DateTimeOffset now, CancellationToken ct)
    {
        if (now - sentAt > TelegramDeleteWindow)
        {
            logger.LogWarning(
                "Could not delete previous alert {MessageId} in chat {ChatId}: Telegram's 48-hour deletion window has passed",
                messageId, channel.ChatId);
            return;
        }

        try
        {
            await bot.DeleteMessage(channel.ChatId, messageId, ct);
            await mirror.DeleteAsync(channel.ChatId, messageId, ct);
        }
        catch (ApiRequestException ex) when (IsDeleteMissing(ex))
        {
            await mirror.DeleteAsync(channel.ChatId, messageId, ct);
        }
        catch (ApiRequestException ex)
        {
            logger.LogWarning(
                "Could not delete previous alert {MessageId} in chat {ChatId}: {Error}",
                messageId, channel.ChatId, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(
                ex,
                "Could not delete previous alert {MessageId} in chat {ChatId} due to a Telegram API connection error",
                messageId, channel.ChatId);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                ex,
                "Could not delete previous alert {MessageId} in chat {ChatId} because the Telegram API request timed out",
                messageId, channel.ChatId);
        }
    }

    private async Task<Outcome> SendEndAsync(
        Channel channel,
        RegionSettings region,
        Delivery delivery,
        IReadOnlyList<string> partNames,
        string? popupText,
        DateTimeOffset now,
        CancellationToken ct,
        bool isGroup = false, string? groupId = null, bool stabilizationActive = false, AlertSource source = AlertSource.None)
    {
        var text = Render(channel, region, AlertKind.Ended, partNames, popupText, now, isGroup, source, groupId: groupId,
            stabilizationActive: stabilizationActive);
        var style = channel.SlotEndStyle(MessageSlots.Ended);

        if (style == EndStyle.Edit)
        {
            try
            {
                var edited = await WithMarkupFallback(
                    text,
                    t => bot.EditMessageText(
                        delivery.ChatId,
                        delivery.TelegramMessageId,
                        t,
                        parseMode: ParseMode.Html,
                        linkPreviewOptions: NoPreview,
                        cancellationToken: ct),
                    channel);
                await mirror.SyncAsync(edited, ct, disableNotification: true);
                return Outcome.Closed;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                return Outcome.Closed;
            }
            catch (ApiRequestException ex) when (
                ex.ErrorCode == 400
                && (ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("can't be edited", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogInformation("Chat {ChatId}: the original alert cannot be edited, posting the end notice as a reply", channel.ChatId);
            }
        }

        var sent = await WithMarkupFallback(
            text,
            t => bot.SendMessage(
                delivery.ChatId,
                t,
                parseMode: ParseMode.Html,
                linkPreviewOptions: NoPreview,
                replyParameters: style == EndStyle.New
                    ? null
                    : new ReplyParameters
                    {
                        MessageId = delivery.TelegramMessageId,
                        AllowSendingWithoutReply = true,
                    },
                disableNotification: QuietHours.IsQuiet(channel, now),
                cancellationToken: ct),
            channel);
        await mirror.SyncAsync(sent, ct, style == EndStyle.New ? null : delivery.TelegramMessageId,
            QuietHours.IsQuiet(channel, now));
        delivery.EndMessageId = sent.Id;
        return Outcome.Closed;
    }

    // The message type decides which of the chat's templates is used.
    public static string SlotFor(AlertKind kind, bool isGroup) =>
        kind == AlertKind.Ended ? MessageSlots.Ended
        : isGroup ? MessageSlots.Local
        : kind == AlertKind.Accident ? MessageSlots.Regional
        : MessageSlots.Emergency;

    private static string Render(
        Channel channel,
        RegionSettings region,
        AlertKind kind,
        IReadOnlyList<string> partNames,
        string? popupText,
        DateTimeOffset now,
        bool isGroup = false,
        AlertSource source = AlertSource.None, string? groupId = null, IReadOnlyList<string>? affectedPartIds = null,
        bool stabilizationActive = false)
    {
        var slot = SlotFor(kind, isGroup);
        var values = AlertText.Values(kind, region.Name, partNames, region.SiteUrl, now, popupText, stabilizationActive)
            .ToDictionary(p => p.Key, p => p.Value);
        if (isGroup) source = AlertSource.Yasno;
        values["message"] = AlertSourceText.Tag(values["message"], source);
        var matching = channel.Addresses.Where(a =>
        {
            if (isGroup) return a.Groups.Any(g => g.GroupId == (groupId ?? channel.GroupId));
            if (partNames.Count == 0) return true;
            IReadOnlyList<string> scope = a.PartIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? channel.PartIdList;
            return scope.Count == 0 || (affectedPartIds is { Count: > 0 }
                ? scope.Any(id => affectedPartIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                : region.DisplayNames(scope).Any(partNames.Contains));
        }).ToArray();
        channel.AddTemplateContext(values, matching, isGroup ? [groupId ?? channel.GroupId ?? ""] : null);

        var rendered = MessageTemplate.Render(channel.TemplateFor(slot), values, MessageSlots.Find(slot)!.DefaultTemplate);
        if (!isGroup && channel.IsCombined() && partNames.Count > 0)
        {
            var names = matching.Select(a => a.Name).Where(n => n != "").Distinct().ToArray();
            if (names.Length > 0) rendered += "\n📍 " + System.Net.WebUtility.HtmlEncode(string.Join(", ", names));
        }
        rendered = AlertSourceText.EnsureAttribution(rendered, source);
        return channel.IsMultiRegion ? $"📍 {System.Net.WebUtility.HtmlEncode(region.Name)}\n{rendered}" : rendered;
    }

    // Names only the districts the alert itself mentions: parts that are merely implied by another one
    // (a bank's city districts) would make the text needlessly long.
    // The end notice is about the chat's own districts, not about whatever the replacing popup names.
    private static List<string> OwnPartNames(RegionSettings region, Channel channel, PopupClassification result)
    {
        var own = channel.PopupDistrictIds();
        return own.Count == 0
            ? PartNames(region, result)
            : region.DisplayNames(own);
    }

    private static List<string> PartNames(RegionSettings region, PopupClassification result)
    {
        var implied = result.PartIds
            .SelectMany(id => region.Parts.FirstOrDefault(p => p.Id == id)?.Children ?? [])
            .ToHashSet(StringComparer.Ordinal);
        return region.DisplayNames(result.PartIds.Where(id => !implied.Contains(id)));
    }

    // A saved template is validated, but if Telegram still rejects its markup the alert must go out.
    private async Task<T> WithMarkupFallback<T>(string text, Func<string, Task<T>> call, Channel channel)
    {
        try
        {
            return await call(text);
        }
        catch (ApiRequestException ex) when (
            ex.ErrorCode == 400
            && ex.Message.Contains("parse entities", StringComparison.OrdinalIgnoreCase)
            && (!string.IsNullOrWhiteSpace(channel.Template) || !string.IsNullOrWhiteSpace(channel.Templates)))
        {
            logger.LogWarning("Chat {ChatId}: custom template rejected by Telegram, sending it without tags", channel.ChatId);
            // The text is already entity-escaped by the renderer, so only the tags are dropped here.
            var plain = Regex.Replace(text, "<[^>]*>", "")
                .Replace("<", "&lt;", StringComparison.Ordinal)
                .Replace(">", "&gt;", StringComparison.Ordinal);
            plain = Regex.Replace(plain, "&(?!(amp|lt|gt|quot|#\\d+|#x[0-9a-fA-F]+);)", "&amp;");
            return await call(plain);
        }
    }

    // No chosen districts, or an alert that names none, reaches everybody in the region; otherwise any overlap counts.
    private static bool Matches(Channel channel, PopupClassification result)
    {
        var parts = channel.PopupDistrictIds();
        return parts.Count == 0 || result.PartIds.Count == 0
            || result.PartIds.Any(id => parts.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    private static string RenderHash(Channel channel, string sourceHash, string text) => !channel.IsCombined() && !channel.IsMultiRegion ? sourceHash
        : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourceHash + text)));

    // Deactivates chats that blocked the bot or no longer exist.
    private async Task<Outcome> TrySendAsync(Channel channel, Func<Task<Outcome>> action, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await action();
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 429 && attempt == 0)
            {
                var wait = Math.Min(ex.Parameters?.RetryAfter ?? 1, MaxRetryAfterSeconds);
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
            }
            catch (ApiRequestException ex) when (IsGone(ex))
            {
                logger.LogWarning("Deactivating chat {ChatId}: {Error}", channel.ChatId, ex.Message);
                channel.IsActive = false;
                var saved = db.Channels.Local.FirstOrDefault(c => c.ChatId == channel.ChatId);
                if (saved is not null) saved.IsActive = false;
                return Outcome.Failed;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                return Outcome.Unchanged;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Send failed for chat {ChatId}", channel.ChatId);
                return Outcome.Failed;
            }
        }

        return Outcome.Failed;
    }

    private static bool IsGone(ApiRequestException ex) =>
        ex.ErrorCode == 403
        || (ex.ErrorCode == 400 && ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase));

    private static bool IsDeleteMissing(ApiRequestException ex) => ex.ErrorCode == 400
        && ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase);
}
