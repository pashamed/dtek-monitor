using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

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

    public async Task<DispatchSummary> DispatchAsync(
        RegionSettings region,
        PopupClassification result,
        CancellationToken ct)
    {
        int sent = 0, edited = 0, closed = 0, failed = 0;

        var channels = await db.Channels
            .Where(c => c.RegionId == region.Id && c.IsActive)
            .ToListAsync(ct);
        var openDeliveries = await db.Deliveries
            .Where(d => d.RegionId == region.Id && !d.IsClosed)
            .ToListAsync(ct);

        if (result.IsAlert)
        {
            var text = BuildAlertText(region, result);
            foreach (var channel in channels.Where(c => Matches(c, result)))
            {
                var delivery = openDeliveries.FirstOrDefault(d => d.ChatId == channel.ChatId);
                var outcome = await TrySendAsync(channel, async () =>
                {
                    if (delivery is null)
                    {
                        var message = await bot.SendMessage(channel.ChatId, text, cancellationToken: ct);
                        db.Deliveries.Add(new Delivery
                        {
                            ChatId = channel.ChatId,
                            RegionId = region.Id,
                            TelegramMessageId = message.Id,
                            ContentHash = result.Hash,
                            UpdatedAt = time.GetUtcNow(),
                        });
                        return 1;
                    }

                    if (delivery.ContentHash == result.Hash)
                    {
                        return 0;
                    }

                    await bot.EditMessageText(channel.ChatId, delivery.TelegramMessageId, text, cancellationToken: ct);
                    delivery.ContentHash = result.Hash;
                    delivery.UpdatedAt = time.GetUtcNow();
                    return 2;
                }, ct);

                switch (outcome)
                {
                    case 1: sent++; break;
                    case 2: edited++; break;
                    case -1: failed++; break;
                }

                await Task.Delay(SendSpacing, ct);
            }
        }
        else
        {
            var byChat = channels.ToDictionary(c => c.ChatId);
            foreach (var delivery in openDeliveries)
            {
                byChat.TryGetValue(delivery.ChatId, out var channel);
                var outcome = await TrySendAsync(channel, async () =>
                {
                    await bot.SendMessage(
                        delivery.ChatId,
                        $"✅ {region.Name}: екстрені відключення скасовано або повідомлення на сайті зникло.",
                        replyParameters: new ReplyParameters { MessageId = delivery.TelegramMessageId, AllowSendingWithoutReply = true },
                        cancellationToken: ct);
                    return 3;
                }, ct);

                // Close even on failure so a dead chat is not retried forever.
                delivery.IsClosed = true;
                delivery.UpdatedAt = time.GetUtcNow();
                if (outcome == 3) closed++; else failed++;
                await Task.Delay(SendSpacing, ct);
            }
        }

        await db.SaveChangesAsync(ct);
        return new DispatchSummary(sent, edited, closed, failed);
    }

    private static bool Matches(Channel channel, PopupClassification result) =>
        channel.PartId is null
        || result.PartIds.Count == 0
        || result.PartIds.Contains(channel.PartId, StringComparer.OrdinalIgnoreCase);

    private static string BuildAlertText(RegionSettings region, PopupClassification result)
    {
        var parts = result.PartIds
            .Select(id => region.Parts.FirstOrDefault(p => p.Id == id)?.Name ?? id)
            .ToList();
        var where = parts.Count > 0 ? $"{region.Name} ({string.Join(", ", parts)})" : region.Name;

        var head = result.Kind switch
        {
            PopupKind.Emergency =>
                $"🚨 {where}: введені екстрені відключення електроенергії.\nГрафіки відключень не діють. Розумно споживайте електроенергію.",
            PopupKind.Accident =>
                $"⚠️ {where}: аварія в енергосистемі, можливі позачергові відключення.\nСтабілізаційні графіки можуть не діяти.",
            _ => $"ℹ️ {where}: на сайті ДТЕК з'явилося важливе повідомлення.",
        };

        return $"{head}\n\nДеталі: {region.SiteUrl}";
    }

    // Returns the delegate's result, or -1 on failure. Deactivates channels that blocked the bot.
    private async Task<int> TrySendAsync(Channel? channel, Func<Task<int>> action, CancellationToken ct)
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
            catch (ApiRequestException ex) when (ex.ErrorCode is 403 or 400 && IsGone(ex))
            {
                logger.LogWarning("Deactivating chat {ChatId}: {Error}", channel?.ChatId, ex.Message);
                if (channel is not null) channel.IsActive = false;
                return -1;
            }
            catch (ApiRequestException ex) when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
            {
                return 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Send failed for chat {ChatId}", channel?.ChatId);
                return -1;
            }
        }

        return -1;
    }

    private static bool IsGone(ApiRequestException ex) =>
        ex.ErrorCode == 403
        || ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase);
}
