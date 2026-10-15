using System.Text.RegularExpressions;
using DtekBot.Api.Checks;
using DtekBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace DtekBot.Api.Bot;

// The bot has no buttons or commands of its own any more: everything is configured in the Mini App.
// What remains here is the welcome message, reacting to channel membership and to «Світло зникло» posts.
public sealed class BotUpdateHandler(
    ITelegramBotClient bot,
    BotDbContext db,
    OutageAssessmentService assessment,
    IConfiguration config,
    TimeProvider time,
    ILogger<BotUpdateHandler> logger)
{
    private static readonly Regex PowerOutPattern = new(@"світло\s+зникло", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        if (update.MyChatMember is { } membership)
        {
            await HandleMembershipAsync(membership, ct);
            return;
        }

        if (update.CallbackQuery is { } callback)
        {
            // Buttons from messages sent by older versions of the bot.
            await bot.AnswerCallbackQuery(
                callback.Id,
                "Бот тепер працює через редактор. Надішліть /start і відкрийте його.",
                showAlert: true,
                cancellationToken: ct);
            return;
        }

        var message = update.Message ?? update.ChannelPost;
        var text = message?.Text?.Trim();
        if (message is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        // A power-monitor app posts «Світло зникло» to a channel the bot administers.
        if (update.ChannelPost is { } post && !text.StartsWith('/') && PowerOutPattern.IsMatch(text))
        {
            await HandlePowerOutPostAsync(post, ct);
            return;
        }

        // Only private chats talk to the bot; whatever is written there, the answer is how to open the editor.
        if (message.Chat.Type == ChatType.Private)
        {
            await SendWelcomeAsync(message.Chat.Id, ct);
        }
    }

    // Reacts to a "power is out" post. The assessment goes from the cheapest explanation to the DTEK address lookup.
    private async Task HandlePowerOutPostAsync(Message post, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == post.Chat.Id && c.IsActive && c.OutageTrigger, ct);
        if (channel is null)
        {
            return;
        }

        await bot.SendMessage(
            post.Chat.Id,
            await assessment.AssessAsync(channel, ct),
            replyParameters: new ReplyParameters { MessageId = post.Id, AllowSendingWithoutReply = true },
            cancellationToken: ct);
    }

    // The single entry point into the editor from a chat; the menu button next to the input field opens it too.
    private async Task SendWelcomeAsync(long chatId, CancellationToken ct)
    {
        var text = string.Join(
            "\n\n",
            "Вітаю! Я повідомляю про екстрені відключення електроенергії за даними сайтів ДТЕК.",
            "Усе налаштовується в редакторі: підключіть свій канал, оберіть регіон, райони й адресу.",
            "Щоб підключити канал, додайте мене до нього адміністратором із правом публікувати повідомлення.");

        var baseUrl = config["App:PublicUrl"]?.TrimEnd('/');
        InlineKeyboardMarkup? keyboard = string.IsNullOrEmpty(baseUrl)
            ? null
            : new InlineKeyboardMarkup(InlineKeyboardButton.WithWebApp("🧩 Відкрити редактор", new WebAppInfo(baseUrl + "/lite/")));

        await bot.SendMessage(chatId, text, replyMarkup: keyboard, cancellationToken: ct);
    }

    // The bot was added to or removed from a channel. Adding it as an administrator connects the channel to the
    // person who did it, so nobody has to type a channel name.
    private async Task HandleMembershipAsync(ChatMemberUpdated change, CancellationToken ct)
    {
        var chat = change.Chat;
        if (chat.Type != ChatType.Channel)
        {
            return;
        }

        var status = change.NewChatMember.Status;
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chat.Id, ct);

        if (status is ChatMemberStatus.Left or ChatMemberStatus.Kicked)
        {
            if (channel is not null)
            {
                channel.IsActive = false;
                channel.OutageTrigger = false;
                channel.ScheduleNotify = false;
                await db.SaveChangesAsync(ct);
            }

            return;
        }

        if (status != ChatMemberStatus.Administrator)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (channel is null)
        {
            channel = new Channel { ChatId = chat.Id, IsActive = false, CreatedAt = now };
            db.Channels.Add(channel);
        }

        channel.Title = chat.Title ?? chat.Username ?? channel.Title;

        var adder = change.From;
        if (!adder.IsBot && !await db.ChannelAdmins.AnyAsync(a => a.ChatId == chat.Id && a.UserId == adder.Id, ct))
        {
            db.ChannelAdmins.Add(new ChannelAdmin { ChatId = chat.Id, UserId = adder.Id, LinkedAt = now });
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Bot became an administrator of channel {ChatId}", chat.Id);

        if (adder.IsBot)
        {
            return;
        }

        var lines = new List<string>
        {
            $"✅ Канал «{channel.Title ?? chat.Id.ToString()}» підключено.",
            "Відкрийте редактор (кнопка меню біля поля вводу або /start), щоб обрати регіон і адресу.",
        };
        if (change.NewChatMember is ChatMemberAdministrator { CanPostMessages: not true })
        {
            lines.Add("⚠️ Надайте боту право публікувати повідомлення, інакше він не зможе писати в канал.");
        }

        try
        {
            await bot.SendMessage(adder.Id, string.Join("\n\n", lines), cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
        {
            // The person may never have started the bot; the channel still shows up in the editor.
            logger.LogInformation("Could not notify user {UserId} about channel {ChatId}: {Message}", adder.Id, chat.Id, ex.Message);
        }
    }
}
