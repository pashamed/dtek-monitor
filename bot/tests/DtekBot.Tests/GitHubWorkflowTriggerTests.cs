using System.Net;
using System.Text;
using DtekBot.Api.Checks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DtekBot.Tests;

public class GitHubWorkflowTriggerTests
{
    private const string Token = "ghp_SuperSecretToken1234567890";
    private const string Repo = "owner/dtek-monitor";

    private sealed class CapturedRequest
    {
        public HttpMethod? Method { get; init; }
        public Uri? Uri { get; init; }
        public string? AuthScheme { get; init; }
        public string? AuthParameter { get; init; }
        public string? Body { get; init; }
        public string? ContentType { get; init; }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new CapturedRequest
            {
                Method = request.Method,
                Uri = request.RequestUri,
                AuthScheme = request.Headers.Authorization?.Scheme,
                AuthParameter = request.Headers.Authorization?.Parameter,
                Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken),
                ContentType = request.Content?.Headers.ContentType?.MediaType,
            });
            return await respond(request, cancellationToken);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(formatter(state, exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                Entries.AddRange(pairs.Select(p => $"{p.Key}={p.Value}"));
            }

            if (exception is not null)
            {
                Entries.Add(exception.ToString());
            }
        }
    }

    private static IConfiguration Config(string? repository = Repo, string? token = Token) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:Repository"] = repository,
                ["GitHub:DispatchToken"] = token,
            })
            .Build();

    private static (GitHubWorkflowTrigger Trigger, FakeHandler Handler, CapturingLogger<GitHubWorkflowTrigger> Log) Create(
        Func<HttpResponseMessage> respond,
        IConfiguration? config = null) =>
        Create((_, _) => Task.FromResult(respond()), config);

    private static (GitHubWorkflowTrigger Trigger, FakeHandler Handler, CapturingLogger<GitHubWorkflowTrigger> Log) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        IConfiguration? config = null)
    {
        var handler = new FakeHandler(respond);
        var log = new CapturingLogger<GitHubWorkflowTrigger>();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        return (new GitHubWorkflowTrigger(http, config ?? Config(), log), handler, log);
    }

    [Fact]
    public async Task Trigger_PostsRepositoryDispatch_WithBearerToken_AndJsonBody()
    {
        var (trigger, handler, _) = Create(() => new HttpResponseMessage(HttpStatusCode.NoContent));

        var ok = await trigger.TriggerAsync("check-addresses", CancellationToken.None);

        Assert.True(ok);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://api.github.com/repos/owner/dtek-monitor/dispatches", request.Uri!.AbsoluteUri);
        Assert.Equal("Bearer", request.AuthScheme);
        Assert.Equal(Token, request.AuthParameter);
        Assert.Equal("""{"event_type":"check-addresses"}""", request.Body);
        Assert.Equal("application/json", request.ContentType);
    }

    [Fact]
    public async Task Trigger_UsesTheGivenEventType()
    {
        var (trigger, handler, _) = Create(() => new HttpResponseMessage(HttpStatusCode.NoContent));

        await trigger.TriggerAsync("other-event", CancellationToken.None);

        Assert.Equal("""{"event_type":"other-event"}""", Assert.Single(handler.Requests).Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Trigger_ReturnsFalse_OnRejection(HttpStatusCode status)
    {
        var (trigger, _, _) = Create(() => new HttpResponseMessage(status)
        {
            Content = new StringContent("""{"message":"Bad credentials"}""", Encoding.UTF8, "application/json"),
        });

        Assert.False(await trigger.TriggerAsync("check-addresses", CancellationToken.None));
    }

    [Theory]
    [InlineData(null, Token)]
    [InlineData("", Token)]
    [InlineData("  ", Token)]
    [InlineData(Repo, null)]
    [InlineData(Repo, "")]
    [InlineData(Repo, "  ")]
    [InlineData(null, null)]
    public async Task Trigger_WithoutRepositoryOrToken_ReturnsFalse_WithoutAnyHttpCall(string? repository, string? token)
    {
        var (trigger, handler, log) = Create(
            () => new HttpResponseMessage(HttpStatusCode.NoContent), Config(repository, token));

        var ok = await trigger.TriggerAsync("check-addresses", CancellationToken.None);

        Assert.False(ok);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(log.Entries, e => e.Contains(Token));
    }

    [Fact]
    public async Task Trigger_OnHttpRequestException_ReturnsFalse()
    {
        var (trigger, _, _) = Create((_, _) => throw new HttpRequestException("connection refused"));

        Assert.False(await trigger.TriggerAsync("check-addresses", CancellationToken.None));
    }

    [Fact]
    public async Task Trigger_OnTimeout_ReturnsFalse()
    {
        var (trigger, _, _) = Create((_, _) => throw new TaskCanceledException("The request timed out"));

        Assert.False(await trigger.TriggerAsync("check-addresses", CancellationToken.None));
    }

    [Fact]
    public async Task Trigger_WithAlreadyCancelledToken_Propagates()
    {
        var (trigger, _, _) = Create(() => new HttpResponseMessage(HttpStatusCode.NoContent));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trigger.TriggerAsync("check-addresses", cts.Token));
    }

    [Fact]
    public async Task Trigger_CancelledDuringRequest_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var (trigger, _, _) = Create(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => trigger.TriggerAsync("check-addresses", cts.Token));
    }

    [Fact]
    public async Task Trigger_NeverLogsTheToken()
    {
        var scenarios = new List<Func<HttpResponseMessage>>
        {
            () => new HttpResponseMessage(HttpStatusCode.NoContent),
            () => new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent($$"""{"message":"Bad credentials {{Token}}"}"""),
            },
            () => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Headers = { { "X-Echo-Authorization", "Bearer " + Token } },
            },
            () => new HttpResponseMessage(HttpStatusCode.NotFound),
            () => new HttpResponseMessage(HttpStatusCode.UnprocessableEntity),
        };

        foreach (var scenario in scenarios)
        {
            var (trigger, _, log) = Create(scenario);
            await trigger.TriggerAsync("check-addresses", CancellationToken.None);
            Assert.DoesNotContain(log.Entries, e => e.Contains(Token));
        }

        var (failing, _, failingLog) = Create((_, _) => throw new HttpRequestException("connection refused"));
        await failing.TriggerAsync("check-addresses", CancellationToken.None);
        Assert.DoesNotContain(failingLog.Entries, e => e.Contains(Token));
    }

    [Fact]
    public async Task Trigger_LogsStatusOnRejection()
    {
        var (trigger, _, log) = Create(() => new HttpResponseMessage(HttpStatusCode.Unauthorized));

        await trigger.TriggerAsync("check-addresses", CancellationToken.None);

        Assert.Contains(log.Entries, e => e.Contains("401"));
    }
}
