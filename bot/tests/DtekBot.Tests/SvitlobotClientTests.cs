using System.Net;
using DtekBot.Api.Svitlobot;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace DtekBot.Tests;

public sealed class SvitlobotClientTests
{
    private const string Secret = "SECRET-key&1";

    private readonly FakeSvitlobotHandler _handler = new();

    private SvitlobotClient Client(ILogger<SvitlobotClient>? logger = null) =>
        new(new HttpClient(_handler) { BaseAddress = new Uri("https://api.svitlobot.in.ua") }, logger ?? NullLogger<SvitlobotClient>.Instance);

    [Fact]
    public async Task GetChannel_ParsesTemplatesAndTheMatrixMarker()
    {
        _handler.Responder = _ => FakeSvitlobotHandler.Text("12;&;Графік 1;&;x;&&&;13;&;Графік 2;&&&;[[0,0],[1,1]]");

        var result = await Client().GetChannelAsync(Secret, default);

        Assert.True(result.Ok);
        Assert.Equal([(12, "Графік 1"), (13, "Графік 2")], result.Value!.Templates.Select(t => (t.Id, t.Name)));
        Assert.True(result.Value.HasSchedule);
    }

    [Fact]
    public async Task GetChannel_RealFormat_TemplatesAreOnePerLine()
    {
        // Captured from the live service (names and ids as returned, no schedule set yet).
        _handler.Responder = _ => FakeSvitlobotHandler.Text(
            "19;&;Група 1.1;&;0\n20;&;Група 1.2;&;0\n21;&;Група 2.1;&;0\n;&&&;no data");

        var result = await Client().GetChannelAsync(Secret, default);

        Assert.True(result.Ok);
        Assert.Equal([(19, "Група 1.1"), (20, "Група 1.2"), (21, "Група 2.1")], result.Value!.Templates.Select(t => (t.Id, t.Name)));
        Assert.False(result.Value.HasSchedule);
    }

    [Fact]
    public async Task GetChannel_NoData_MeansNoSchedule()
    {
        _handler.Responder = _ => FakeSvitlobotHandler.Text("5;&;Один;&&&;no data");

        var result = await Client().GetChannelAsync(Secret, default);

        Assert.False(result.Value!.HasSchedule);
        Assert.Equal(5, Assert.Single(result.Value.Templates).Id);
    }

    [Fact]
    public async Task GetChannel_OnlyNoData_HasNoTemplates()
    {
        _handler.Responder = _ => FakeSvitlobotHandler.Text("no data");

        var result = await Client().GetChannelAsync(Secret, default);

        Assert.True(result.Ok);
        Assert.Empty(result.Value!.Templates);
        Assert.False(result.Value.HasSchedule);
    }

    [Theory]
    [InlineData("7", "Шаблон 7")]
    [InlineData("7;&;", "Шаблон 7")]
    [InlineData("7;&;100;&;Нічний;&;3", "Нічний")]
    [InlineData("7;&;  Ранок  ", "Ранок")]
    public async Task GetChannel_ToleratesMissingOrOddTemplateFields(string template, string expectedName)
    {
        _handler.Responder = _ => FakeSvitlobotHandler.Text(template + ";&&&;no data");

        var result = await Client().GetChannelAsync(Secret, default);

        var parsed = Assert.Single(result.Value!.Templates);
        Assert.Equal(7, parsed.Id);
        Assert.Equal(expectedName, parsed.Name);
    }

    [Fact]
    public async Task GetChannel_SkipsTemplatesWithoutANumericId_AndDuplicates()
    {
        _handler.Responder = _ => FakeSvitlobotHandler.Text("abc;&;Bad;&&&;4;&;A;&&&;4;&;B;&&&;;&&&;no data");

        var result = await Client().GetChannelAsync(Secret, default);

        var only = Assert.Single(result.Value!.Templates);
        Assert.Equal((4, "A"), (only.Id, only.Name));
    }

    [Theory]
    [InlineData("error", SvitlobotFailure.Rejected)]
    [InlineData("parse error", SvitlobotFailure.Rejected)]
    [InlineData("invalid data", SvitlobotFailure.Rejected)]
    [InlineData("Channel \"" + Secret + "\" is not found! Insert correct key from chat bot!", SvitlobotFailure.NotFound)]
    public async Task ErrorAnswers_AreMapped_AndNeverEchoTheKey(string answer, SvitlobotFailure expected)
    {
        _handler.Responder = _ => FakeSvitlobotHandler.Text(answer);
        var client = Client();

        var results = new SvitlobotResult[]
        {
            await client.GetChannelAsync(Secret, default),
            await client.SetTemplateAsync(Secret, 1, default),
            await client.SetTimetableAsync(Secret, "0", default),
            await client.RemoveAsync(Secret, default),
        };

        Assert.All(results, r =>
        {
            Assert.False(r.Ok);
            Assert.Equal(expected, r.Failure);
            Assert.False(string.IsNullOrWhiteSpace(r.Message));
            Assert.DoesNotContain(Secret, r.Message);
            Assert.Contains(r.Message!, c => c is >= 'А' and <= 'я' or 'і' or 'ї' or 'є');
        });
    }

    [Fact]
    public async Task HttpErrorStatus_AndNetworkErrors_AreUnavailable()
    {
        _handler.Responder = _ => FakeSvitlobotHandler.Text("ok", HttpStatusCode.InternalServerError);
        Assert.Equal(SvitlobotFailure.Unavailable, (await Client().RemoveAsync(Secret, default)).Failure);

        _handler.Responder = _ => throw new HttpRequestException("boom " + Secret);
        var result = await Client().RemoveAsync(Secret, default);
        Assert.Equal(SvitlobotFailure.Unavailable, result.Failure);
        Assert.DoesNotContain(Secret, result.Message);
    }

    [Fact]
    public async Task WriteCall_WithAnUnexpectedAnswer_IsUnavailable()
    {
        _handler.Responder = _ => FakeSvitlobotHandler.Text("<html>maintenance</html>");

        Assert.Equal(SvitlobotFailure.Unavailable, (await Client().SetTemplateAsync(Secret, 1, default)).Failure);
    }

    [Fact]
    public async Task Timeout_IsUnavailable_ButCallerCancellationPropagates()
    {
        _handler.Responder = _ => throw new TaskCanceledException();
        Assert.Equal(SvitlobotFailure.Unavailable, (await Client().RemoveAsync(Secret, default)).Failure);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _handler.Responder = _ => throw new TaskCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client().RemoveAsync(Secret, cts.Token));
    }

    [Fact]
    public async Task Urls_AreEncoded_AndUseTheDocumentedMethods()
    {
        var client = Client();
        const string data = "000000001100000000000000;0";

        await client.GetChannelAsync(Secret, default);
        await client.SetTemplateAsync(Secret, 12, default);
        await client.SetTimetableAsync(Secret, data, default);
        await client.RemoveAsync(Secret, default);

        var urls = _handler.Requests.Select(r => r.PathAndQuery).ToList();
        Assert.Equal(
        [
            "/website/getChannelTimetable?channel_key=SECRET-key%261",
            "/website/setChannelTimetable?channel_key=SECRET-key%261&timetable_id=12",
            "/website/timetableEditEvent?channel_key=SECRET-key%261&timetableData=000000001100000000000000%3B0",
            "/website/removeChannelTimetable?channel_key=SECRET-key%261",
        ], urls);
        Assert.All(_handler.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
    }

    [Fact]
    public async Task Logs_NeverContainTheKeyOrAUrl()
    {
        var logger = new ListLogger();
        _handler.Responder = _ => FakeSvitlobotHandler.Text("Channel \"" + Secret + "\" is not found!");
        var client = Client(logger);
        await client.GetChannelAsync(Secret, default);
        _handler.Responder = _ => throw new HttpRequestException("failed for " + Secret);
        await client.SetTimetableAsync(Secret, "0", default);
        _handler.Responder = _ => FakeSvitlobotHandler.Text("weird " + Secret);
        await client.SetTemplateAsync(Secret, 1, default);

        Assert.NotEmpty(logger.Lines);
        Assert.All(logger.Lines, line =>
        {
            Assert.DoesNotContain(Secret, line);
            Assert.DoesNotContain("channel_key", line);
            Assert.DoesNotContain("http://", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("https://", line, StringComparison.OrdinalIgnoreCase);
        });
    }

    private sealed class ListLogger : ILogger<SvitlobotClient>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception) + exception);
    }
}
