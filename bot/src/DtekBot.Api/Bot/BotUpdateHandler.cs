using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace DtekBot.Api.Bot;

public sealed class BotUpdateHandler(
    ITelegramBotClient bot,
    BotDbContext db,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time,
    ILogger<BotUpdateHandler> logger)
{
    public async Task HandleAsync(Update update, CancellationToken ct)
    {
        if (update.CallbackQuery is { } callback)
        {
            await HandleCallbackAsync(callback, ct);
            return;
        }

        var message = update.Message ?? update.ChannelPost;
        var text = message?.Text?.Trim();
        if (message is null || string.IsNullOrEmpty(text) || !text.StartsWith('/'))
        {
            return;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].Split('@')[0].ToLowerInvariant();
        var chatId = message.Chat.Id;

        if (command is "/start" or "/menu" or "/help")
        {
            var (menuText, menuKeyboard) = await MainMenuAsync(chatId, ct);
            await bot.SendMessage(chatId, menuText, replyMarkup: menuKeyboard, cancellationToken: ct);
            return;
        }

        var reply = command switch
        {
            "/region" => await SubscribeAsync(chatId, parts.Skip(1).ToArray(), ct),
            "/stop" => await UnsubscribeAsync(chatId, ct),
            _ => null,
        };

        if (reply is not null)
        {
            await bot.SendMessage(chatId, reply, cancellationToken: ct);
        }
    }

    // Callback data: menu | regions | r:<region> | p:<region>:<part|all> | status | stop
    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var message = callback.Message;
        if (message is null || callback.Data is null)
        {
            await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
            return;
        }

        var chatId = message.Chat.Id;
        var data = callback.Data.Split(':');
        string text;
        InlineKeyboardMarkup keyboard;
        string? toast = null;

        switch (data[0])
        {
            case "regions":
                (text, keyboard) = RegionList();
                break;

            case "r" when data.Length == 2:
                (text, keyboard, toast) = await RegionChosenAsync(chatId, data[1], ct);
                break;

            case "p" when data.Length == 3:
                var partArg = data[2] == "all" ? Array.Empty<string>() : new[] { data[2] };
                toast = await SubscribeAsync(chatId, new[] { data[1] }.Concat(partArg).ToArray(), ct);
                (text, keyboard) = await MainMenuAsync(chatId, ct);
                break;

            case "status":
                (text, keyboard) = await StatusAsync(chatId, ct);
                break;

            case "stop":
                toast = await UnsubscribeAsync(chatId, ct);
                (text, keyboard) = await MainMenuAsync(chatId, ct);
                break;

            default:
                (text, keyboard) = await MainMenuAsync(chatId, ct);
                break;
        }

        await bot.AnswerCallbackQuery(callback.Id, toast, cancellationToken: ct);
        try
        {
            await bot.EditMessageText(chatId, message.Id, text, replyMarkup: keyboard, cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private async Task<(string, InlineKeyboardMarkup)> MainMenuAsync(long chatId, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
        var current = channel is { IsActive: true } ? "Підписка: " + Describe(channel) : "Підписки немає.";

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("🌍 Обрати регіон", "regions") },
            new[] { InlineKeyboardButton.WithCallbackData("📊 Поточний стан", "status") },
        };
        if (channel is { IsActive: true })
        {
            rows.Add([InlineKeyboardButton.WithCallbackData("🔕 Відписатися", "stop")]);
        }

        return ("Вітаю! Я повідомляю про екстрені відключення електроенергії за даними сайтів ДТЕК.\n\n" + current,
            new InlineKeyboardMarkup(rows));
    }

    private (string, InlineKeyboardMarkup) RegionList()
    {
        var rows = options.CurrentValue.Regions
            .Select(r => new[] { InlineKeyboardButton.WithCallbackData(r.Name, $"r:{r.Id}") })
            .Append([InlineKeyboardButton.WithCallbackData("⬅️ Назад", "menu")]);
        return ("Оберіть регіон:", new InlineKeyboardMarkup(rows));
    }

    private async Task<(string, InlineKeyboardMarkup, string?)> RegionChosenAsync(
        long chatId, string regionId, CancellationToken ct)
    {
        var region = options.CurrentValue.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            var (t, k) = RegionList();
            return (t, k, "Невідомий регіон.");
        }

        if (region.Parts.Count == 0)
        {
            var toast = await SubscribeAsync(chatId, [region.Id], ct);
            var (t, k) = await MainMenuAsync(chatId, ct);
            return (t, k, toast);
        }

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("Весь регіон", $"p:{region.Id}:all") },
        };
        rows.AddRange(region.Parts.Select(p =>
            new[] { InlineKeyboardButton.WithCallbackData(p.Name, $"p:{region.Id}:{p.Id}") }));
        rows.Add([InlineKeyboardButton.WithCallbackData("⬅️ Назад", "regions")]);
        return ($"{region.Name}: оберіть частину.", new InlineKeyboardMarkup(rows), null);
    }

    private async Task<(string, InlineKeyboardMarkup)> StatusAsync(long chatId, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId && c.IsActive, ct);
        var back = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Назад", "menu"));
        if (channel is null)
        {
            return ("Спершу оберіть регіон.", back);
        }

        var state = await db.RegionStates.FirstOrDefaultAsync(s => s.RegionId == channel.RegionId, ct);
        var status = state?.Kind switch
        {
            Parsing.PopupKind.Emergency => "🚨 Діють екстрені відключення.",
            Parsing.PopupKind.Accident => "⚠️ Повідомлено про аварію в енергосистемі.",
            Parsing.PopupKind.Unknown => "ℹ️ На сайті є невизначене повідомлення.",
            Parsing.PopupKind.Stabilization => "Діють стабілізаційні відключення (за графіками).",
            null => "Даних ще немає.",
            _ => "Екстрених відключень немає.",
        };

        var checkedAt = state is null ? "" : $"\nПерервірено: {state.ObservedAt.ToOffset(TimeSpan.FromHours(3)):dd.MM HH:mm} (Київ, приблизно)";
        return ($"{Describe(channel)}\n{status}{checkedAt}", back);
    }

    private string Describe(Channel channel)
    {
        var region = options.CurrentValue.Regions.FirstOrDefault(r => r.Id == channel.RegionId);
        var name = region?.Name ?? channel.RegionId;
        var part = region?.Parts.FirstOrDefault(p => p.Id == channel.PartId)?.Name ?? channel.PartId;
        return part is null ? name : $"{name} ({part})";
    }

    private async Task<string> SubscribeAsync(long chatId, string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            return "Вкажіть код регіону, наприклад: /region kyiv-oblast";
        }

        var region = options.CurrentValue.Regions.FirstOrDefault(r =>
            string.Equals(r.Id, args[0], StringComparison.OrdinalIgnoreCase));
        if (region is null)
        {
            return "Невідомий регіон. Надішліть /start, щоб побачити список.";
        }

        string? partId = null;
        if (args.Length > 1)
        {
            partId = region.Parts.FirstOrDefault(p =>
                string.Equals(p.Id, args[1], StringComparison.OrdinalIgnoreCase))?.Id;
            if (partId is null)
            {
                return "Невідома частина регіону. Залиште її порожньою для всього регіону.";
            }
        }

        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
        if (channel is null)
        {
            channel = new Channel { ChatId = chatId, CreatedAt = time.GetUtcNow() };
            db.Channels.Add(channel);
        }

        channel.RegionId = region.Id;
        channel.PartId = partId;
        channel.IsActive = true;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Chat {ChatId} subscribed to {Region}/{Part}", chatId, region.Id, partId);
        return $"Підписано: {region.Name}" + (partId is null ? "" : $" ({partId})");
    }

    private async Task<string> UnsubscribeAsync(long chatId, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
        if (channel is null || !channel.IsActive)
        {
            return "Підписки немає.";
        }

        channel.IsActive = false;
        await db.SaveChangesAsync(ct);
        return "Відписано.";
    }
}
