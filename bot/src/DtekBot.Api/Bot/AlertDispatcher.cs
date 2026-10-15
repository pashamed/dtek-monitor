using System.Text.RegularExpressions;
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
    ILogger<AlertDispatcher> logger)
{
    private const int MaxRetryAfterSeconds = 10;
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
        CancellationToken ct)
    {
        int sent = 0, edited = 0, closed = 0, failed = 0;
        var now = time.GetUtcNow();

        var channels = await db.Channels
            .Where(c => c.RegionId == region.Id && c.IsActive)
            .ToListAsync(ct);
        var openDeliveries = await db.Deliveries
            .Where(d => d.RegionId == region.Id && !d.IsClosed)
            .ToListAsync(ct);

        var kind = result.Kind == PopupKind.Emergency ? AlertKind.Emergency
            : result.Kind == PopupKind.Accident ? AlertKind.Accident
            : AlertKind.Unknown;

        // Threads whose chat left the region or unsubscribed are closed without a message.
        var activeChats = channels.Select(c => c.ChatId).ToHashSet();
        foreach (var stale in openDeliveries.Where(d => !activeChats.Contains(d.ChatId)))
        {
            stale.IsClosed = true;
            stale.UpdatedAt = now;
        }

        foreach (var channel in channels)
        {
            var delivery = openDeliveries.FirstOrDefault(d => d.ChatId == channel.ChatId);
            var eligible = result.IsAlert
                && !channel.IsPaused
                && Matches(channel, result)
                && PassesFilter(channel, result.Kind);

            Outcome outcome;
            if (eligible)
            {
                var text = Render(channel, region, kind, result, popupText, now);
                outcome = await TrySendAsync(
                    channel,
                    () => SendOrEditAsync(channel, region, delivery, text, result.Hash, now, ct),
                    ct);
            }
            else if (delivery is not null)
            {
                var announce = !channel.IsPaused && channel.NotifyOnEnd;
                outcome = announce
                    ? await TrySendAsync(
                        channel,
                        () => SendEndAsync(channel, region, delivery, result, popupText, now, ct),
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

    private async Task<Outcome> SendOrEditAsync(
        Channel channel,
        RegionSettings region,
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

        if (delivery is not null && channel.EditInPlace)
        {
            await WithMarkupFallback(
                text,
                t => bot.EditMessageText(
                    channel.ChatId,
                    delivery.TelegramMessageId,
                    t,
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: NoPreview,
                    cancellationToken: ct),
                channel);
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
                cancellationToken: ct),
            channel);

        if (delivery is null)
        {
            db.Deliveries.Add(new Delivery
            {
                ChatId = channel.ChatId,
                RegionId = region.Id,
                TelegramMessageId = message.Id,
                ContentHash = hash,
                UpdatedAt = now,
            });
            return Outcome.Sent;
        }

        // New-post mode: the latest post becomes the thread the end message replies to.
        delivery.TelegramMessageId = message.Id;
        delivery.ContentHash = hash;
        delivery.UpdatedAt = now;
        return Outcome.Sent;
    }

    private async Task<Outcome> SendEndAsync(
        Channel channel,
        RegionSettings region,
        Delivery delivery,
        PopupClassification result,
        string? popupText,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var text = Render(channel, region, AlertKind.Ended, result, popupText, now);
        await WithMarkupFallback(
            text,
            t => bot.SendMessage(
                delivery.ChatId,
                t,
                parseMode: ParseMode.Html,
                linkPreviewOptions: NoPreview,
                replyParameters: new ReplyParameters
                {
                    MessageId = delivery.TelegramMessageId,
                    AllowSendingWithoutReply = true,
                },
                cancellationToken: ct),
            channel);
        return Outcome.Closed;
    }

    private static string Render(
        Channel channel,
        RegionSettings region,
        AlertKind kind,
        PopupClassification result,
        string? popupText,
        DateTimeOffset now)
    {
        var partNames = result.PartIds
            .Select(id => region.Parts.FirstOrDefault(p => p.Id == id)?.Name ?? id)
            .ToList();
        var values = AlertText.Values(kind, region.Name, partNames, region.SiteUrl, now, popupText);
        return MessageTemplate.Render(channel.Template, values);
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
            && !string.IsNullOrWhiteSpace(channel.Template))
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

    private static bool Matches(Channel channel, PopupClassification result) =>
        channel.PartId is null
        || result.PartIds.Count == 0
        || result.PartIds.Contains(channel.PartId, StringComparer.OrdinalIgnoreCase);

    private static bool PassesFilter(Channel channel, PopupKind kind) =>
        channel.Filter == AlertFilter.All || kind is PopupKind.Emergency or PopupKind.Accident;

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
}
