using System.Net;
using System.Text;
using DtekBot.Api.Groups;
using Microsoft.Extensions.Logging.Abstractions;

namespace DtekBot.Tests;

public class YasnoClientTests
{
    private const string Base = "https://app.yasno.ua/api/blackout-service/public/shutdowns/addresses/v2/";
    private const string DragomanovaEscaped = "%D0%94%D1%80%D0%B0%D0%B3%D0%BE%D0%BC%D0%B0%D0%BD%D0%BE%D0%B2%D0%B0";

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return respond(request, cancellationToken);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static (YasnoClient Client, FakeHandler Handler) Create(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
    {
        var handler = new FakeHandler(respond);
        var client = new YasnoClient(
            new HttpClient(handler) { BaseAddress = new Uri(YasnoClient.BaseUrl) },
            NullLogger<YasnoClient>.Instance);
        return (client, handler);
    }

    private static (YasnoClient Client, FakeHandler Handler) Create(Func<HttpResponseMessage> respond) =>
        Create((_, _) => Task.FromResult(respond()));

    [Fact]
    public void BaseUrl_IsTheYasnoAddressesApi_WithTrailingSlash()
    {
        Assert.Equal(Base, YasnoClient.BaseUrl);
    }

    // ---- URLs ----

    [Fact]
    public async Task SearchStreets_BuildsExpectedUrl_WithEscapedUkrainianQuery()
    {
        var (client, handler) = Create(() => Json("[]"));

        await client.SearchStreetsAsync(25, 902, "Драгоманова", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal($"{Base}streets?regionId=25&dsoId=902&query={DragomanovaEscaped}", request.RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task SearchStreets_EscapesReservedCharacters()
    {
        var (client, handler) = Create(() => Json("[]"));

        await client.SearchStreetsAsync(1, 2, "a b&c=d#e", CancellationToken.None);

        Assert.EndsWith("&query=a%20b%26c%3Dd%23e", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task SearchHouses_BuildsExpectedUrl()
    {
        var (client, handler) = Create(() => Json("[]"));

        await client.SearchHousesAsync(25, 902, 283, "12А", CancellationToken.None);

        Assert.Equal(
            $"{Base}houses?regionId=25&dsoId=902&streetId=283&query=12%D0%90",
            Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task SearchHouses_EmptyQuery_StillSendsEmptyQueryParameter()
    {
        var (client, handler) = Create(() => Json("[]"));

        await client.SearchHousesAsync(25, 902, 283, "", CancellationToken.None);

        Assert.EndsWith("&query=", Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetGroup_BuildsExpectedUrl()
    {
        var (client, handler) = Create(() => Json("""{"group":45,"subgroup":1}"""));

        await client.GetGroupAsync(25, 902, 283, 12634, CancellationToken.None);

        Assert.Equal(
            $"{Base}group?regionId=25&dsoId=902&streetId=283&houseId=12634",
            Assert.Single(handler.Requests).RequestUri!.AbsoluteUri);
    }

    // ---- parsing ----

    [Fact]
    public async Task SearchStreets_ParsesRealResponseShape()
    {
        var (client, _) = Create(() => Json("""[{"id":283,"value":"вул. Драгоманова"}]"""));

        var streets = await client.SearchStreetsAsync(25, 902, "Драгоманова", CancellationToken.None);

        var street = Assert.Single(streets);
        Assert.Equal(283, street.Id);
        Assert.Equal("вул. Драгоманова", street.Value);
    }

    [Fact]
    public async Task SearchHouses_ParsesRealResponseShape()
    {
        var (client, _) = Create(() => Json("""[{"id":12634,"value":"12"},{"id":12635,"value":"12А"}]"""));

        var houses = await client.SearchHousesAsync(25, 902, 283, "12", CancellationToken.None);

        Assert.Equal(
            [new YasnoItem(12634, "12"), new YasnoItem(12635, "12А")],
            houses);
    }

    [Fact]
    public async Task SearchStreets_IgnoresUnknownFields()
    {
        var (client, _) = Create(() => Json("""[{"id":1,"value":"x","extra":{"a":1}}]"""));

        Assert.Single(await client.SearchStreetsAsync(1, 1, "x", CancellationToken.None));
    }

    [Fact]
    public async Task SearchStreets_EmptyArray_ReturnsEmptyList()
    {
        var (client, _) = Create(() => Json("[]"));

        Assert.Empty(await client.SearchStreetsAsync(1, 1, "x", CancellationToken.None));
    }

    [Fact]
    public async Task SearchStreets_JsonNull_ReturnsEmptyList()
    {
        var (client, _) = Create(() => Json("null"));

        Assert.Empty(await client.SearchStreetsAsync(1, 1, "x", CancellationToken.None));
    }

    [Fact]
    public async Task GetGroup_ParsesRealResponseShape_AndFormatsId()
    {
        var (client, _) = Create(() => Json("""{"group":45,"subgroup":1}"""));

        var group = await client.GetGroupAsync(25, 902, 283, 12634, CancellationToken.None);

        Assert.NotNull(group);
        Assert.Equal(45, group.Group);
        Assert.Equal(1, group.Subgroup);
        Assert.Equal("45.1", group.Id);
    }

    [Theory]
    [InlineData(1, 2, "1.2")]
    [InlineData(60, 0, "60.0")]
    public void YasnoGroup_Id_IsGroupDotSubgroup(int group, int subgroup, string expected)
    {
        Assert.Equal(expected, new YasnoGroup(group, subgroup).Id);
    }

    // ---- failures never throw ----

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task HttpErrorStatus_ReturnsEmptyOrNull(HttpStatusCode status)
    {
        var (client, _) = Create(() => Json("""[{"id":1,"value":"x"}]""", status));

        Assert.Empty(await client.SearchStreetsAsync(1, 1, "x", CancellationToken.None));
        Assert.Empty(await client.SearchHousesAsync(1, 1, 1, "x", CancellationToken.None));
        Assert.Null(await client.GetGroupAsync(1, 1, 1, 1, CancellationToken.None));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("<html>oops</html>")]
    [InlineData("""[{"id":"abc","value":1}]""")]
    [InlineData("""[1,2,3]""")]
    public async Task MalformedOrUnexpectedJson_ReturnsEmptyOrNull(string body)
    {
        var (client, _) = Create(() => Json(body));

        Assert.Empty(await client.SearchStreetsAsync(1, 1, "x", CancellationToken.None));
        Assert.Empty(await client.SearchHousesAsync(1, 1, 1, "x", CancellationToken.None));
        Assert.Null(await client.GetGroupAsync(1, 1, 1, 1, CancellationToken.None));
    }

    [Fact]
    public async Task ErrorObjectWithoutErrorLists_ReturnsEmpty()
    {
        var (client, _) = Create(() => Json("""{"error":"x"}"""));

        Assert.Empty(await client.SearchStreetsAsync(1, 1, "x", CancellationToken.None));
        Assert.Empty(await client.SearchHousesAsync(1, 1, 1, "x", CancellationToken.None));
    }

    [Fact]
    public async Task GroupResponseWithoutGroupFields_ReturnsNull()
    {
        var (client, _) = Create(() => Json("""{"error":"x"}"""));

        Assert.Null(await client.GetGroupAsync(1, 1, 1, 1, CancellationToken.None));
    }

    [Fact]
    public async Task GroupResponseOfWrongShape_ReturnsNull()
    {
        var (client, _) = Create(() => Json("""[{"id":1,"value":"x"}]"""));

        Assert.Null(await client.GetGroupAsync(1, 1, 1, 1, CancellationToken.None));
    }

    [Fact]
    public async Task EmptyBody_ReturnsEmptyOrNull()
    {
        var (client, _) = Create(() => Json(""));

        Assert.Empty(await client.SearchStreetsAsync(1, 1, "x", CancellationToken.None));
        Assert.Null(await client.GetGroupAsync(1, 1, 1, 1, CancellationToken.None));
    }

    [Fact]
    public async Task HttpRequestException_ReturnsEmptyOrNull()
    {
        var (client, _) = Create((_, _) => throw new HttpRequestException("connection refused"));

        Assert.Empty(await client.SearchStreetsAsync(1, 1, "x", CancellationToken.None));
        Assert.Empty(await client.SearchHousesAsync(1, 1, 1, "x", CancellationToken.None));
        Assert.Null(await client.GetGroupAsync(1, 1, 1, 1, CancellationToken.None));
    }

    [Fact]
    public async Task RequestTimeout_NotCausedByCallerToken_ReturnsEmptyOrNull()
    {
        var (client, _) = Create((_, _) => throw new TaskCanceledException("timeout"));

        Assert.Empty(await client.SearchStreetsAsync(1, 1, "x", CancellationToken.None));
        Assert.Null(await client.GetGroupAsync(1, 1, 1, 1, CancellationToken.None));
    }

    [Fact]
    public async Task HtmlErrorPageWithSuccessStatus_ReturnsEmptyOrNull()
    {
        var (client, _) = Create(() => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>blocked</html>", Encoding.UTF8, "text/html"),
        });

        Assert.Empty(await client.SearchStreetsAsync(1, 1, "x", CancellationToken.None));
        Assert.Null(await client.GetGroupAsync(1, 1, 1, 1, CancellationToken.None));
    }

    // ---- cancellation ----

    [Fact]
    public async Task AlreadyCancelledToken_Propagates()
    {
        var (client, _) = Create(() => Json("[]"));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SearchStreetsAsync(1, 1, "x", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SearchHousesAsync(1, 1, 1, "x", cts.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetGroupAsync(1, 1, 1, 1, cts.Token));
    }

    [Fact]
    public async Task TokenCancelledDuringRequest_Propagates()
    {
        using var cts = new CancellationTokenSource();
        var (client, _) = Create(async (_, ct) =>
        {
            await cts.CancelAsync();
            await Task.Delay(Timeout.Infinite, ct);
            return Json("[]");
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetGroupAsync(1, 1, 1, 1, cts.Token));
    }
}
