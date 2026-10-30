using DtekBot.Api.Data;
using DtekBot.Api.MiniApp;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DtekBot.Api.Bot;

public sealed class PrivateNotificationMirror(
    ITelegramBotClient bot,
    BotDbContext db,
    ChatAccessService access,
    TimeProvider time,
    ILogger<PrivateNotificationMirror> logger)
{
    // Source message zero is the private source-label message, not a Telegram message.
    private const int SourceLabel = 0;

    public async Task SyncAsync(Message message, CancellationToken ct, int? replyToMessageId = null,
        bool disableNotification = false)
    {
        var sourceChatId = message.Chat.Id;
        if (sourceChatId >= 0) return;
        var links = await db.ChannelAdmins.Where(a => a.ChatId == sourceChatId).ToListAsync(ct);
        foreach (var link in links)
        {
            if (!await access.IsAdminAsync(sourceChatId, link.UserId, ct)) continue;
            PrivateNotificationCopy? copy = null;
            try
            {
                copy = await FindAsync(sourceChatId, message.Id, link.UserId, ct);
                if (copy is { MessageId: > 0 })
                {
                    try
                    {
                        await EditAsync(copy, message, ct);
                        continue;
                    }
                    catch (ApiRequestException ex) when (IsNotModified(ex))
                    {
                        continue;
                    }
                    catch (ApiRequestException ex) when (IsMissing(ex))
                    {
                        var oldMessageId = copy.MessageId;
                        if (ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase))
                        {
                            copy.MessageId = 0;
                        }
                        else
                        {
                            await MarkEditPendingAsync(copy, ct);
                            logger.LogInformation("Private copy {MessageId} for user {UserId} will be replaced",
                                oldMessageId, link.UserId);
                            continue;
                        }

                        logger.LogInformation("Private copy {MessageId} for user {UserId} needs refreshing",
                            oldMessageId, link.UserId);
                    }
                    catch (Exception ex) when (ex is ApiRequestException or HttpRequestException
                        || ex is OperationCanceledException && !ct.IsCancellationRequested)
                    {
                        if (ex is ApiRequestException api && !IsRetryable(api))
                        {
                            logger.LogWarning(ex, "Private notification edit is not retryable for source {ChatId}, message {MessageId}, user {UserId}",
                                sourceChatId, message.Id, link.UserId);
                            continue;
                        }

                        await MarkEditPendingAsync(copy, ct);
                        throw;
                    }
                }

                if (copy is { MessageId: < 0 })
                {
                    try
                    {
                        await bot.DeleteMessage(link.UserId, -copy.MessageId, ct);
                    }
                    catch (ApiRequestException ex) when (IsMissingDelete(ex))
                    {
                    }

                    copy.MessageId = 0;
                }

                copy ??= new PrivateNotificationCopy
                {
                    SourceChatId = sourceChatId,
                    SourceMessageId = message.Id,
                    UserId = link.UserId,
                };
                if (db.Entry(copy).State == EntityState.Detached)
                    db.PrivateNotificationCopies.Add(copy);
                copy.MessageId = await CopyToPrivateAsync(sourceChatId, message.Id, link.UserId,
                    replyToMessageId ?? message.ReplyToMessage?.Id, disableNotification, ct);
            }
            catch (Exception ex) when (ex is ApiRequestException or HttpRequestException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (copy is { MessageId: 0 } && ex is ApiRequestException api && !IsRetryable(api))
                    db.PrivateNotificationCopies.Remove(copy);
                logger.LogWarning(ex, "Private notification copy failed for source {ChatId}, message {MessageId}, user {UserId}",
                    sourceChatId, message.Id, link.UserId);
            }
        }
    }

    // Zero message IDs mark pending copies, negative IDs pending deletes, and negative source IDs pending replacements.
    public async Task RetryPendingAsync(CancellationToken ct)
    {
        var pending = await db.PrivateNotificationCopies
            .Where(c => c.MessageId <= 0 || c.SourceMessageId < 0)
            .ToListAsync(ct);
        pending.AddRange(db.PrivateNotificationCopies.Local.Where(c =>
            (c.MessageId <= 0 || c.SourceMessageId < 0) && !pending.Contains(c)).ToArray());

        foreach (var copy in pending)
        {
            try
            {
                if (copy.SourceMessageId < 0)
                {
                    await RetryReplacementAsync(copy, ct);
                    continue;
                }

                if (copy.MessageId < 0)
                {
                    await bot.DeleteMessage(copy.UserId, -copy.MessageId, ct);
                    db.PrivateNotificationCopies.Remove(copy);
                    continue;
                }

                if (!await db.ChannelAdmins.AnyAsync(a =>
                    a.ChatId == copy.SourceChatId && a.UserId == copy.UserId, ct))
                {
                    db.PrivateNotificationCopies.Remove(copy);
                    continue;
                }

                if (!await access.IsAdminAsync(copy.SourceChatId, copy.UserId, ct))
                    continue;

                copy.MessageId = await CopyToPrivateAsync(copy.SourceChatId, copy.SourceMessageId, copy.UserId,
                    replyToMessageId: null, disableNotification: true, ct);
            }
            catch (ApiRequestException ex) when (IsMissingDelete(ex))
            {
                db.PrivateNotificationCopies.Remove(copy);
            }
            catch (Exception ex) when (ex is ApiRequestException or HttpRequestException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                if (copy.MessageId == 0 && ex is ApiRequestException api && !IsRetryable(api))
                    db.PrivateNotificationCopies.Remove(copy);
                logger.LogWarning(ex, "Pending private notification operation failed for source {ChatId}, message {MessageId}, user {UserId}",
                    copy.SourceChatId, copy.SourceMessageId, copy.UserId);
            }
        }
    }

    public async Task DeleteAsync(long sourceChatId, int sourceMessageId, CancellationToken ct)
    {
        if (sourceChatId >= 0) return;
        var copies = await db.PrivateNotificationCopies
            .Where(c => c.SourceChatId == sourceChatId
                && (c.SourceMessageId == sourceMessageId || c.SourceMessageId == -sourceMessageId)).ToListAsync(ct);
        copies.AddRange(db.PrivateNotificationCopies.Local.Where(c =>
            c.SourceChatId == sourceChatId
                && (c.SourceMessageId == sourceMessageId || c.SourceMessageId == -sourceMessageId)
                && !copies.Contains(c)).ToArray());
        foreach (var copy in copies)
        {
            if (copy.SourceMessageId < 0)
            {
                db.PrivateNotificationCopies.Remove(copy);
                continue;
            }

            if (copy.MessageId == 0)
            {
                db.PrivateNotificationCopies.Remove(copy);
                continue;
            }

            try
            {
                await bot.DeleteMessage(copy.UserId, Math.Abs(copy.MessageId), ct);
                db.PrivateNotificationCopies.Remove(copy);
            }
            catch (ApiRequestException ex) when (IsMissingDelete(ex))
            {
                db.PrivateNotificationCopies.Remove(copy);
            }
            catch (Exception ex) when (ex is ApiRequestException or HttpRequestException
                || ex is OperationCanceledException && !ct.IsCancellationRequested)
            {
                copy.MessageId = -Math.Abs(copy.MessageId);
                logger.LogWarning(ex, "Private notification deletion failed for source {ChatId}, message {MessageId}, user {UserId}",
                    sourceChatId, sourceMessageId, copy.UserId);
            }
        }
    }

    private Task<PrivateNotificationCopy?> FindAsync(long chatId, int messageId, long userId, CancellationToken ct) =>
        db.PrivateNotificationCopies.FindAsync([chatId, messageId, userId], ct).AsTask();

    private async Task MarkEditPendingAsync(PrivateNotificationCopy copy, CancellationToken ct)
    {
        if (copy.SourceMessageId <= 0) return;
        var sourceId = -copy.SourceMessageId;
        if (await FindAsync(copy.SourceChatId, sourceId, copy.UserId, ct) is null)
            db.PrivateNotificationCopies.Add(new PrivateNotificationCopy
            {
                SourceChatId = copy.SourceChatId,
                SourceMessageId = sourceId,
                UserId = copy.UserId,
                MessageId = copy.MessageId,
            });
    }

    private async Task RetryReplacementAsync(PrivateNotificationCopy pending, CancellationToken ct)
    {
        var sourceMessageId = -pending.SourceMessageId;
        var original = await FindAsync(pending.SourceChatId, sourceMessageId, pending.UserId, ct);
        if (original is null)
        {
            db.PrivateNotificationCopies.Remove(pending);
            return;
        }

        if (!await db.ChannelAdmins.AnyAsync(a =>
            a.ChatId == pending.SourceChatId && a.UserId == pending.UserId, ct)
            || !await access.IsAdminAsync(pending.SourceChatId, pending.UserId, ct))
        {
            db.PrivateNotificationCopies.Remove(pending);
            return;
        }

        if (original.MessageId > 0)
        {
            try
            {
                await bot.DeleteMessage(pending.UserId, original.MessageId, ct);
            }
            catch (ApiRequestException ex) when (IsMissingDelete(ex))
            {
            }
        }
        else if (original.MessageId < 0)
        {
            try
            {
                await bot.DeleteMessage(pending.UserId, -original.MessageId, ct);
            }
            catch (ApiRequestException ex) when (IsMissingDelete(ex))
            {
            }

            db.PrivateNotificationCopies.Remove(pending);
            db.PrivateNotificationCopies.Remove(original);
            return;
        }

        original.MessageId = 0;
        db.PrivateNotificationCopies.Remove(pending);
        try
        {
            original.MessageId = await CopyToPrivateAsync(pending.SourceChatId, sourceMessageId,
                pending.UserId, replyToMessageId: null, disableNotification: true, ct);
        }
        catch (Exception ex) when (ex is ApiRequestException or HttpRequestException
            || ex is OperationCanceledException && !ct.IsCancellationRequested)
        {
            if (ex is ApiRequestException api && !IsRetryable(api))
                db.PrivateNotificationCopies.Remove(original);
            logger.LogWarning(ex, "Private notification replacement failed for source {ChatId}, message {MessageId}, user {UserId}",
                pending.SourceChatId, sourceMessageId, pending.UserId);
        }
    }

    private async Task<int> CopyToPrivateAsync(long sourceChatId, int sourceMessageId, long userId,
        int? replyToMessageId, bool disableNotification, CancellationToken ct)
    {
        var personal = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == userId, ct);
        var silent = disableNotification || personal is not null && QuietHours.IsQuiet(personal, time.GetUtcNow());
        var label = await FindAsync(sourceChatId, SourceLabel, userId, ct);
        if (label is null)
        {
            var source = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == sourceChatId, ct);
            if (source is null)
                throw new InvalidOperationException($"Source channel {sourceChatId} is no longer available.");
            var header = await WithRetry(() => bot.SendMessage(userId,
                $"Копії з «{source.Title ?? sourceChatId.ToString()}» ({sourceChatId}).\nТут надходитимуть повідомлення цього чату.",
                disableNotification: silent, cancellationToken: ct), ct);
            label = new PrivateNotificationCopy
            {
                SourceChatId = sourceChatId,
                SourceMessageId = SourceLabel,
                UserId = userId,
                MessageId = header.Id,
            };
            db.PrivateNotificationCopies.Add(label);
        }

        var reply = replyToMessageId is { } id
            ? await FindAsync(sourceChatId, id, userId, ct)
            : null;
        var copied = await WithRetry(() => bot.CopyMessage(userId, sourceChatId, sourceMessageId,
            disableNotification: silent,
            replyParameters: new ReplyParameters
            {
                MessageId = reply?.MessageId ?? label.MessageId,
                AllowSendingWithoutReply = true,
            },
            cancellationToken: ct), ct);
        return copied.Id;
    }

    private async Task EditAsync(PrivateNotificationCopy copy, Message source, CancellationToken ct)
    {
        if (source.Photo is { Length: > 0 } photos)
        {
            await WithRetry(() => bot.EditMessageMedia(copy.UserId, copy.MessageId,
                new InputMediaPhoto(InputFile.FromFileId(photos[^1].FileId))
                {
                    Caption = source.Caption, CaptionEntities = source.CaptionEntities,
                }, cancellationToken: ct), ct);
        }
        else if (source.Text is { } text)
        {
            await WithRetry(() => bot.EditMessageText(copy.UserId, copy.MessageId, text, entities: source.Entities,
                linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct), ct);
        }
        else
        {
            throw new InvalidOperationException("Automatic notification must contain text or a photo.");
        }
    }

    private static async Task<T> WithRetry<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(ex.Parameters?.RetryAfter ?? 1, 0, 10)), ct);
            return await action();
        }
    }

    private static bool IsNotModified(ApiRequestException ex) =>
        ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);

    private static bool IsMissing(ApiRequestException ex) => ex.ErrorCode == 400
        && (ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("can't be edited", StringComparison.OrdinalIgnoreCase));

    private static bool IsMissingDelete(ApiRequestException ex) => ex.ErrorCode == 400
        && ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase);

    private static bool IsRetryable(ApiRequestException ex) => ex.ErrorCode == 429 || ex.ErrorCode >= 500;
}
