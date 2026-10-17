using DtekBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DtekBot.Api.MiniApp;

public enum UnlinkOutcome
{
    NotLinked,
    Unlinked,

    // The caller was the last linked admin, so the chat's subscription and personal data were removed too.
    UnlinkedAndUnsubscribed,
}

public sealed class ChatAccessService(ITelegramBotClient bot, BotDbContext db, ILogger<ChatAccessService> logger)
{
    private static User? _me;

    // Takes a group or channel off the user's list. Works even if the user is no longer an admin there.
    public async Task<UnlinkOutcome> UnlinkAsync(long chatId, long userId, DateTimeOffset now, CancellationToken ct)
    {
        var link = await db.ChannelAdmins.FirstOrDefaultAsync(a => a.ChatId == chatId && a.UserId == userId, ct);
        if (link is null)
        {
            return UnlinkOutcome.NotLinked;
        }

        db.ChannelAdmins.Remove(link);
        var stillManaged = await db.ChannelAdmins.AnyAsync(a => a.ChatId == chatId && a.UserId != userId, ct);
        if (stillManaged)
        {
            await db.SaveChangesAsync(ct);
            return UnlinkOutcome.Unlinked;
        }

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
        if (channel is not null)
        {
            await ChannelReset.ResetAsync(db, channel, ct);
        }

        // Queued address checks for a chat nobody manages any more are dropped without a reply.
        var pending = await db.AddressChecks
            .Where(c => c.ChatId == chatId && c.Status == AddressCheckStatus.Pending)
            .ToListAsync(ct);
        foreach (var check in pending)
        {
            check.Status = AddressCheckStatus.Expired;
            check.CompletedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return UnlinkOutcome.UnlinkedAndUnsubscribed;
    }

    // Asks Telegram, so it also covers admins demoted after they linked a chat.
    public async Task<bool> IsAdminAsync(long chatId, long userId, CancellationToken ct) =>
        (await CheckAdminAsync(chatId, userId, ct)).IsAdmin;

    // Distinguishes "the user is not an admin" from "Telegram would not tell us", so a failing check can be explained.
    // getChatMember is tried first; the administrators list is a second opinion (it does not depend on the member type).
    public async Task<(bool IsAdmin, string? Problem)> CheckAdminAsync(long chatId, long userId, CancellationToken ct)
    {
        string? problem = null;
        try
        {
            var member = await bot.GetChatMember(chatId, userId, ct);
            return (member.Status is ChatMemberStatus.Creator or ChatMemberStatus.Administrator, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problem = ex is Telegram.Bot.Exceptions.ApiRequestException ? ex.Message : ex.GetType().Name;
            logger.LogWarning("getChatMember failed for user {UserId} in chat {ChatId}: {Type}: {Message}", userId, chatId, ex.GetType().Name, ex.Message);
        }

        try
        {
            var admins = await bot.GetChatAdministrators(chatId, cancellationToken: ct);
            return (admins.Any(a => a.User.Id == userId), null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("getChatAdministrators failed for chat {ChatId}: {Type}: {Message}", chatId, ex.GetType().Name, ex.Message);
            return (false, problem ?? ex.Message);
        }
    }

    public async Task<bool> CanManageAsync(long chatId, long userId, CancellationToken ct)
    {
        if (chatId == userId)
        {
            return true;
        }

        var linked = await db.ChannelAdmins.AnyAsync(a => a.ChatId == chatId && a.UserId == userId, ct);
        return linked && await IsAdminAsync(chatId, userId, ct);
    }

    public async Task<bool> BotIsMemberAsync(long chatId, CancellationToken ct)
    {
        try
        {
            _me ??= await bot.GetMe(ct);
            var member = await bot.GetChatMember(chatId, _me.Id, ct);
            return member.Status is not (ChatMemberStatus.Left or ChatMemberStatus.Kicked);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("Bot membership check failed for chat {ChatId}: {Message}", chatId, ex.Message);
            return false;
        }
    }
}
