using System.Net;
using DtekBot.Api.Data;
using DtekBot.Api.MiniApp;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace DtekBot.Api.Bot;

public sealed class BotUpdateHandler(
    ITelegramBotClient bot,
    BotDbContext db,
    ChatAccessService access,
    IOptionsMonitor<DtekSettings> options,
    IConfiguration config,
    TimeProvider time,
    ILogger<BotUpdateHandler> logger)
{
    private const string NoAccess = "Керувати ботом можуть лише адміністратори чату.";

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

        var firstToken = text.Split([' ', '\n'], 2, StringSplitOptions.RemoveEmptyEntries)[0];
        var command = firstToken.Split('@')[0].ToLowerInvariant();
        var arguments = text[firstToken.Length..].Trim();
        var chat = message.Chat;

        if (command is "/start" or "/menu" or "/help")
        {
            var (menuText, menuKeyboard) = await MainMenuAsync(chat, ct);
            await bot.SendMessage(chat.Id, menuText, replyMarkup: menuKeyboard, cancellationToken: ct);
            return;
        }

        if (command is not ("/region" or "/stop" or "/template"))
        {
            return;
        }

        if (!await IsAuthorizedAsync(chat, message.From?.Id, ct))
        {
            await bot.SendMessage(chat.Id, NoAccess, cancellationToken: ct);
            return;
        }

        var reply = command switch
        {
            "/region" => await SubscribeAsync(chat, message.From?.Id, arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), ct),
            "/stop" => await UnsubscribeAsync(chat.Id, ct),
            _ => await TemplateCommandAsync(chat.Id, arguments, ct),
        };

        await bot.SendMessage(chat.Id, reply, parseMode: ParseMode.Html, linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, cancellationToken: ct);
    }

    // Private chats are always allowed; groups and channels need an admin (channel posts carry no sender and are admin-only).
    private async Task<bool> IsAuthorizedAsync(Chat chat, long? userId, CancellationToken ct)
    {
        if (chat.Type == ChatType.Private || userId is null)
        {
            return true;
        }

        return await access.IsAdminAsync(chat.Id, userId.Value, ct);
    }

    // Callback data: menu | regions | r:<region> | p:<region>:<part|all> | status | stop | cfg | cfg:filter|end|edit|pause
    private async Task HandleCallbackAsync(CallbackQuery callback, CancellationToken ct)
    {
        var message = callback.Message;
        if (message is null || callback.Data is null)
        {
            await bot.AnswerCallbackQuery(callback.Id, cancellationToken: ct);
            return;
        }

        var chat = message.Chat;
        var data = callback.Data.Split(':');
        var isRead = data[0] is "menu" or "regions" or "status";
        if (!isRead && !await IsAuthorizedAsync(chat, callback.From.Id, ct))
        {
            await bot.AnswerCallbackQuery(callback.Id, NoAccess, showAlert: true, cancellationToken: ct);
            return;
        }

        string text;
        InlineKeyboardMarkup keyboard;
        string? toast = null;

        switch (data[0])
        {
            case "regions":
                (text, keyboard) = RegionList();
                break;

            case "r" when data.Length == 2:
                (text, keyboard, toast) = await RegionChosenAsync(chat, callback.From.Id, data[1], ct);
                break;

            case "p" when data.Length == 3:
                var partArg = data[2] == "all" ? Array.Empty<string>() : new[] { data[2] };
                toast = await SubscribeAsync(chat, callback.From.Id, new[] { data[1] }.Concat(partArg).ToArray(), ct);
                (text, keyboard) = await MainMenuAsync(chat, ct);
                break;

            case "status":
                (text, keyboard) = await StatusAsync(chat.Id, ct);
                break;

            case "stop":
                toast = await UnsubscribeAsync(chat.Id, ct);
                (text, keyboard) = await MainMenuAsync(chat, ct);
                break;

            case "cfg":
                if (data.Length == 2)
                {
                    toast = await ToggleAsync(chat.Id, data[1], ct);
                }

                (text, keyboard) = await SettingsAsync(chat.Id, ct);
                break;

            default:
                (text, keyboard) = await MainMenuAsync(chat, ct);
                break;
        }

        await bot.AnswerCallbackQuery(callback.Id, toast, cancellationToken: ct);
        try
        {
            await bot.EditMessageText(chat.Id, message.Id, text, replyMarkup: keyboard, cancellationToken: ct);
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            when (ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private async Task<(string, InlineKeyboardMarkup)> MainMenuAsync(Chat chat, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chat.Id, ct);
        var subscribed = channel is { IsActive: true };
        var current = subscribed ? "Підписка: " + Describe(channel!) : "Підписки немає.";

        var rows = new List<InlineKeyboardButton[]>
        {
            new[] { InlineKeyboardButton.WithCallbackData("🌍 Обрати регіон", "regions") },
            new[] { InlineKeyboardButton.WithCallbackData("📊 Поточний стан", "status") },
            new[] { InlineKeyboardButton.WithCallbackData("⚙️ Налаштування", "cfg") },
        };

        // Web App buttons are rejected by Telegram outside private chats.
        var baseUrl = config["App:PublicUrl"]?.TrimEnd('/');
        if (chat.Type == ChatType.Private && !string.IsNullOrEmpty(baseUrl))
        {
            rows.Add([InlineKeyboardButton.WithWebApp("🧩 Редактор повідомлень (WASM)", new WebAppInfo(baseUrl + "/app/"))]);
            rows.Add([InlineKeyboardButton.WithWebApp("🧩 Редактор повідомлень (lite)", new WebAppInfo(baseUrl + "/lite/"))]);
        }

        if (subscribed)
        {
            rows.Add([InlineKeyboardButton.WithCallbackData("🔕 Відписатися", "stop")]);
        }

        return ("Вітаю! Я повідомляю про екстрені відключення електроенергії за даними сайтів ДТЕК.\n\n" + current,
            new InlineKeyboardMarkup(rows));
    }

    private async Task<(string, InlineKeyboardMarkup)> SettingsAsync(long chatId, CancellationToken ct)
    {
        var back = new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("⬅️ Назад", "menu"));
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId && c.IsActive, ct);
        if (channel is null)
        {
            return ("Спершу оберіть регіон.", back);
        }

        var text = string.Join(
            "\n",
            "⚙️ Налаштування",
            "",
            "Підписка: " + Describe(channel),
            "Сповіщення: " + (channel.Filter == AlertFilter.All ? "усі" : "лише екстрені та аварії"),
            "Про завершення: " + (channel.NotifyOnEnd ? "так" : "ні"),
            "Оновлення: " + (channel.EditInPlace ? "редагувати те саме повідомлення" : "щоразу нове повідомлення"),
            "Стан: " + (channel.IsPaused ? "⏸ на паузі" : "▶️ активні"),
            "Шаблон: " + (string.IsNullOrWhiteSpace(channel.Template) ? "стандартний" : "свій (/template)"));

        var keyboard = new InlineKeyboardMarkup(new[]
        {
            new[] { InlineKeyboardButton.WithCallbackData(channel.Filter == AlertFilter.All ? "Лише екстрені та аварії" : "Усі сповіщення", "cfg:filter") },
            new[] { InlineKeyboardButton.WithCallbackData(channel.NotifyOnEnd ? "Не писати про завершення" : "Писати про завершення", "cfg:end") },
            new[] { InlineKeyboardButton.WithCallbackData(channel.EditInPlace ? "Нові повідомлення замість редагування" : "Редагувати те саме повідомлення", "cfg:edit") },
            new[] { InlineKeyboardButton.WithCallbackData(channel.IsPaused ? "▶️ Відновити" : "⏸ Пауза", "cfg:pause") },
            new[] { InlineKeyboardButton.WithCallbackData("⬅️ Назад", "menu") },
        });

        return (text, keyboard);
    }

    private async Task<string?> ToggleAsync(long chatId, string setting, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId && c.IsActive, ct);
        if (channel is null)
        {
            return "Спершу оберіть регіон.";
        }

        string toast;
        switch (setting)
        {
            case "filter":
                channel.Filter = channel.Filter == AlertFilter.All ? AlertFilter.EmergencyOnly : AlertFilter.All;
                toast = channel.Filter == AlertFilter.All ? "Усі сповіщення" : "Лише екстрені та аварії";
                break;
            case "end":
                channel.NotifyOnEnd = !channel.NotifyOnEnd;
                toast = channel.NotifyOnEnd ? "Писатиму про завершення" : "Не писатиму про завершення";
                break;
            case "edit":
                channel.EditInPlace = !channel.EditInPlace;
                toast = channel.EditInPlace ? "Редагуватиму повідомлення" : "Надсилатиму нові повідомлення";
                break;
            case "pause":
                channel.IsPaused = !channel.IsPaused;
                toast = channel.IsPaused ? "Сповіщення призупинено" : "Сповіщення відновлено";
                break;
            default:
                return null;
        }

        await db.SaveChangesAsync(ct);
        return toast;
    }

    private (string, InlineKeyboardMarkup) RegionList()
    {
        var rows = options.CurrentValue.Regions
            .Select(r => new[] { InlineKeyboardButton.WithCallbackData(r.Name, $"r:{r.Id}") })
            .Append([InlineKeyboardButton.WithCallbackData("⬅️ Назад", "menu")]);
        return ("Оберіть регіон:", new InlineKeyboardMarkup(rows));
    }

    private async Task<(string, InlineKeyboardMarkup, string?)> RegionChosenAsync(
        Chat chat, long userId, string regionId, CancellationToken ct)
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
            var toast = await SubscribeAsync(chat, userId, [region.Id], ct);
            var (t, k) = await MainMenuAsync(chat, ct);
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
            PopupKind.Emergency => "🚨 Діють екстрені відключення.",
            PopupKind.Accident => "⚠️ Повідомлено про аварію в енергосистемі.",
            PopupKind.Unknown => "ℹ️ На сайті є невизначене повідомлення.",
            PopupKind.Stabilization => "Діють стабілізаційні відключення (за графіками).",
            null => "Даних ще немає.",
            _ => "Екстрених відключень немає.",
        };

        var checkedAt = state is null ? "" : $"\nПерервірено: {KyivTime.Format(state.ObservedAt)} (Київ)";
        return ($"{Describe(channel)}\n{status}{checkedAt}", back);
    }

    private string Describe(Channel channel)
    {
        var region = options.CurrentValue.Regions.FirstOrDefault(r => r.Id == channel.RegionId);
        var name = region?.Name ?? channel.RegionId;
        var part = region?.Parts.FirstOrDefault(p => p.Id == channel.PartId)?.Name ?? channel.PartId;
        return part is null ? name : $"{name} ({part})";
    }

    private async Task<string> TemplateCommandAsync(long chatId, string arguments, CancellationToken ct)
    {
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId && c.IsActive, ct);
        if (channel is null)
        {
            return "Спершу оберіть регіон: /region kyiv";
        }

        if (arguments.Length == 0)
        {
            var current = string.IsNullOrWhiteSpace(channel.Template) ? MessageTemplate.Default : channel.Template;
            var names = string.Join("\n", MessageTemplate.Placeholders.Select(p => $"<code>{{{p.Name}}}</code> — {WebUtility.HtmlEncode(p.Description)}"));
            return $"Поточний шаблон:\n<pre>{WebUtility.HtmlEncode(current)}</pre>\n"
                + "Змінити: /template ваш текст (дозволені теги &lt;b&gt;, &lt;i&gt;, &lt;a href=\"…\"&gt;), скинути: /template reset.\n\n"
                + "Параметри:\n" + names;
        }

        if (arguments.Equals("reset", StringComparison.OrdinalIgnoreCase))
        {
            channel.Template = null;
            await db.SaveChangesAsync(ct);
            return "Шаблон скинуто до стандартного.";
        }

        var errors = MessageTemplate.Validate(arguments);
        if (errors.Count > 0)
        {
            return "Шаблон не збережено:\n" + string.Join("\n", errors.Select(e => "• " + WebUtility.HtmlEncode(e)));
        }

        channel.Template = arguments;
        await db.SaveChangesAsync(ct);

        var region = options.CurrentValue.Regions.FirstOrDefault(r => r.Id == channel.RegionId) ?? options.CurrentValue.Regions.First();
        var preview = MessageTemplate.RenderPreviewHtml(arguments, AlertKind.Emergency, region.Name, [], region.SiteUrl);
        return "Збережено. Так виглядатиме сповіщення:\n\n" + preview;
    }

    private async Task<string> SubscribeAsync(Chat chat, long? userId, string[] args, CancellationToken ct)
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

        var chatId = chat.Id;
        var channel = await db.Channels.FirstOrDefaultAsync(c => c.ChatId == chatId, ct);
        if (channel is null)
        {
            channel = new Channel { ChatId = chatId, CreatedAt = time.GetUtcNow() };
            db.Channels.Add(channel);
        }

        channel.RegionId = region.Id;
        channel.PartId = partId;
        channel.IsActive = true;
        channel.Title = chat.Title ?? chat.Username ?? channel.Title;

        // Lets the user manage this group from the Mini App later.
        if (chat.Type is ChatType.Group or ChatType.Supergroup && userId is { } adminId
            && !await db.ChannelAdmins.AnyAsync(a => a.ChatId == chatId && a.UserId == adminId, ct))
        {
            db.ChannelAdmins.Add(new ChannelAdmin { ChatId = chatId, UserId = adminId, LinkedAt = time.GetUtcNow() });
        }

        await db.SaveChangesAsync(ct);

        logger.LogInformation("Chat {ChatId} subscribed to {Region}/{Part}", chatId, region.Id, partId);
        var partName = region.Parts.FirstOrDefault(p => p.Id == partId)?.Name;
        return $"Підписано: {region.Name}" + (partName is null ? "" : $" ({partName})");
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
