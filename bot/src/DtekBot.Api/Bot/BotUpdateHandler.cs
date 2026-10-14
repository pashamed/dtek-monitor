using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;

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
        var message = update.Message ?? update.ChannelPost;
        var text = message?.Text?.Trim();
        if (message is null || string.IsNullOrEmpty(text) || !text.StartsWith('/'))
        {
            return;
        }

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var command = parts[0].Split('@')[0].ToLowerInvariant();
        var chatId = message.Chat.Id;

        var reply = command switch
        {
            "/start" or "/help" => HelpText(),
            "/region" => await SubscribeAsync(chatId, parts.Skip(1).ToArray(), ct),
            "/stop" => await UnsubscribeAsync(chatId, ct),
            _ => null,
        };

        if (reply is not null)
        {
            await bot.SendMessage(chatId, reply, cancellationToken: ct);
        }
    }

    private string HelpText()
    {
        var regions = string.Join("\n", options.CurrentValue.Regions.Select(r => $"• {r.Id} — {r.Name}"));
        return "Вітаю! Оберіть регіон командою /region <код> [частина].\n\n"
            + regions
            + "\n\n/stop — відписатися.";
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
