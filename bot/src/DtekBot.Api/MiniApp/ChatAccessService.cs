using DtekBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DtekBot.Api.MiniApp;

public sealed class ChatAccessService(ITelegramBotClient bot, BotDbContext db, ILogger<ChatAccessService> logger)
{
    private static User? _me;

    // Asks Telegram, so it also covers admins demoted after they linked a chat.
    public async Task<bool> IsAdminAsync(long chatId, long userId, CancellationToken ct)
    {
        try
        {
            var member = await bot.GetChatMember(chatId, userId, ct);
            return member.Status is ChatMemberStatus.Creator or ChatMemberStatus.Administrator;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("Admin check failed for user {UserId} in chat {ChatId}: {Message}", userId, chatId, ex.Message);
            return false;
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
