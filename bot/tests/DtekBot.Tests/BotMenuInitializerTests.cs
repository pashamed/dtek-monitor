using DtekBot.Api.Bot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;

namespace DtekBot.Tests;

public class BotMenuInitializerTests
{
    private readonly FakeTelegramBotClient _bot = new();

    private BotMenuInitializer Create(string? token = "123:token", string? url = "https://example.test")
    {
        var values = new Dictionary<string, string?>();
        if (token is not null)
        {
            values["Bot:Token"] = token;
        }

        if (url is not null)
        {
            values["App:PublicUrl"] = url;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new BotMenuInitializer(_bot, config, NullLogger<BotMenuInitializer>.Instance);
    }

    private static async Task RunAsync(BotMenuInitializer initializer, CancellationToken ct = default)
    {
        await initializer.StartAsync(ct);
        if (initializer.ExecuteTask is { } task)
        {
            await task;
        }

        await initializer.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WithTokenAndUrl_SetsTheMenuButtonToTheLiteEditor()
    {
        await RunAsync(Create());

        var request = Assert.Single(_bot.Requests.OfType<SetChatMenuButtonRequest>());
        Assert.Null(request.ChatId);
        var button = Assert.IsType<MenuButtonWebApp>(request.MenuButton);
        Assert.Equal("Редактор", button.Text);
        Assert.Equal("https://example.test/lite/", button.WebApp.Url);
    }

    [Fact]
    public async Task WithTokenAndUrl_SetsExactlyOneCommand_Start()
    {
        await RunAsync(Create());

        var request = Assert.Single(_bot.Requests.OfType<SetMyCommandsRequest>());
        var command = Assert.Single(request.Commands);
        Assert.Equal("start", command.Command);
        Assert.Equal("Відкрити редактор", command.Description);
        Assert.Null(request.Scope);
        Assert.Null(request.LanguageCode);
    }

    [Fact]
    public async Task WithTokenAndUrl_MakesExactlyTwoCalls_MenuButtonFirst()
    {
        await RunAsync(Create());

        Assert.Equal(2, _bot.Requests.Count);
        Assert.IsType<SetChatMenuButtonRequest>(_bot.Requests[0]);
        Assert.IsType<SetMyCommandsRequest>(_bot.Requests[1]);
    }

    [Theory]
    [InlineData("https://example.test/")]
    [InlineData("https://example.test///")]
    public async Task TrailingSlashesOfThePublicUrl_AreTrimmed(string url)
    {
        await RunAsync(Create(url: url));

        var button = Assert.IsType<MenuButtonWebApp>(Assert.Single(_bot.Requests.OfType<SetChatMenuButtonRequest>()).MenuButton);
        Assert.Equal("https://example.test/lite/", button.WebApp.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task WithoutToken_NothingIsCalled(string? token)
    {
        await RunAsync(Create(token: token));

        Assert.Empty(_bot.Requests);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public async Task WithoutPublicUrl_NothingIsCalled(string? url)
    {
        await RunAsync(Create(url: url));

        Assert.Empty(_bot.Requests);
    }

    [Fact]
    public async Task WithNeitherTokenNorUrl_NothingIsCalled()
    {
        await RunAsync(Create(token: null, url: null));

        Assert.Empty(_bot.Requests);
    }

    [Fact]
    public async Task ApiRequestException_OnTheMenuButton_IsSwallowed_AndCommandsAreNotAttempted()
    {
        _bot.Failure = r => r is SetChatMenuButtonRequest ? new ApiRequestException("Bad Request: wrong url", 400) : null;

        await RunAsync(Create());

        Assert.Single(_bot.Requests);
    }

    [Fact]
    public async Task ApiRequestException_OnTheCommands_IsSwallowed()
    {
        _bot.Failure = r => r is SetMyCommandsRequest ? new ApiRequestException("Too Many Requests", 429) : null;

        await RunAsync(Create());

        Assert.Equal(2, _bot.Requests.Count);
    }

    [Fact]
    public async Task UnauthorizedToken_IsSwallowed()
    {
        _bot.Failure = _ => new ApiRequestException("Unauthorized", 401);

        await RunAsync(Create());

        Assert.Single(_bot.Requests);
    }

    [Fact]
    public async Task NetworkError_IsSwallowed()
    {
        _bot.Failure = _ => new HttpRequestException("network down");

        await RunAsync(Create());

        Assert.Single(_bot.Requests);
    }

    [Fact]
    public async Task ExceptionsNeverFaultTheService()
    {
        _bot.Failure = _ => new ApiRequestException("boom", 500);
        var initializer = Create();

        await initializer.StartAsync(CancellationToken.None);

        Assert.NotNull(initializer.ExecuteTask);
        await initializer.ExecuteTask;
        Assert.False(initializer.ExecuteTask.IsFaulted);
        Assert.True(initializer.ExecuteTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task CancellationToken_IsPassedToBothCalls()
    {
        using var cts = new CancellationTokenSource();

        await RunAsync(Create(), cts.Token);

        Assert.Equal(2, _bot.RequestTokens.Count);
        Assert.All(_bot.RequestTokens, t => Assert.True(t.CanBeCanceled));
    }

    [Fact]
    public async Task CancelledToken_IsRespected_AndPropagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var initializer = Create();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await initializer.StartAsync(cts.Token);
            await (initializer.ExecuteTask ?? Task.CompletedTask);
        });
    }

    [Fact]
    public async Task CancellationDuringTheMenuButtonCall_StopsBeforeTheCommands()
    {
        using var cts = new CancellationTokenSource();
        _bot.Failure = r =>
        {
            if (r is SetChatMenuButtonRequest)
            {
                cts.Cancel();
            }

            return null;
        };
        var initializer = Create();

        // The first request succeeded; the second one observes the cancelled token and never completes.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await initializer.StartAsync(cts.Token);
            await (initializer.ExecuteTask ?? Task.CompletedTask);
        });

        Assert.Equal(2, _bot.Requests.Count);
    }
}
