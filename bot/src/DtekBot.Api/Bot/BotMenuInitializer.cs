using Telegram.Bot;
using Telegram.Bot.Types;

namespace DtekBot.Api.Bot;

// Sets the bot's menu button (next to the input field) to open the Mini App and leaves /start as the only command.
// Best effort: a failure is logged and never stops the app.
public sealed class BotMenuInitializer(
    ITelegramBotClient bot,
    IConfiguration config,
    ILogger<BotMenuInitializer> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var token = config["Bot:Token"];
        var baseUrl = config["App:PublicUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrEmpty(baseUrl))
        {
            return;
        }

        try
        {
            await bot.SetChatMenuButton(
                menuButton: new MenuButtonWebApp
                {
                    Text = "Редактор",
                    WebApp = new WebAppInfo(baseUrl + "/lite/"),
                },
                cancellationToken: stoppingToken);
            await bot.SetMyCommands(
                [new BotCommand { Command = "start", Description = "Відкрити редактор" }],
                cancellationToken: stoppingToken);
            logger.LogInformation("Bot menu button set to {Url}", baseUrl + "/lite/");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Could not set the bot menu button: {Message}", ex.Message);
        }
    }
}
