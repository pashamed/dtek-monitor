using System.Net;
using System.Text.Json;
using DtekBot.Api.Checks;
using DtekBot.Api.Parsing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DtekBot.Tests;

public sealed class DtekPopupCollectorTests
{
    private const string Page = """
        <html><head><meta name="csrf-token" content="test"></head><body>
        <script>DisconSchedule = {};</script>
        <div class="modal__container--firstPopup m-attention__container">
          <div class="modal__title">Шановні клієнти!</div>
          <div class="m-attention__text"><p>Екстрені <b>відключення</b><br>Графіки &amp; світло</p></div>
        </div></body></html>
        """;

    [Fact]
    public void Parser_ExtractsOnlyPopupText_WithLineBreaksAndEntities()
    {
        Assert.True(DtekPopupCollector.TryReadPopup(Page, out var text));
        Assert.Equal("Шановні клієнти!\n\nЕкстрені відключення\nГрафіки & світло", text);
    }

    [Theory]
    [InlineData("<html><iframe src='/_Incapsula_Resource'></iframe></html>")]
    [InlineData("<html>Access denied</html>")]
    [InlineData("<meta name='csrf-token' content='test'>")]
    public void ChallengeOrIncompletePage_CannotClearAlert(string html) =>
        Assert.False(DtekPopupCollector.TryReadPopup(html, out _));

    [Fact]
    public void RealPageWithoutPopup_IsConfirmedClear()
    {
        Assert.True(DtekPopupCollector.TryReadPopup(
            "<meta name='csrf-token' content='test'><script>DisconSchedule={};</script>", out var text));
        Assert.Null(text);
    }

    private sealed class Handler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Host.Contains("oem") ? Page : "<html>Imperva challenge</html>"),
            });
    }

    private sealed class Factory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler());
    }

    private sealed class Trigger(Func<object?, bool> callback) : IWorkflowTrigger
    {
        public Task<bool> TriggerAsync(string eventType, CancellationToken ct, object? payload = null)
        {
            Assert.Equal("check-regions", eventType);
            return Task.FromResult(callback(payload));
        }
    }

    [Fact]
    public async Task BrowserFallback_IsCorrelated_AndOnlyChecksMissingRegions()
    {
        using var fixture = new DispatcherFixture();
        DtekPopupCollector? collector = null;
        string? collectionId = null;
        var trigger = new Trigger(payload =>
        {
            var json = JsonSerializer.SerializeToElement(payload);
            collectionId = json.GetProperty("collectionId").GetString()!;
            var missing = json.GetProperty("regionIds").EnumerateArray().Select(r => r.GetString()!).ToArray();
            Assert.DoesNotContain("odesa", missing);
            Assert.Contains("kyiv", missing);
            foreach (var id in missing)
            {
                var observation = new DtekPopupObservation(id, "Екстрені відключення", DateTimeOffset.UtcNow);
                Assert.False(collector!.Accept("other-cycle", observation));
                Assert.True(collector.Accept(collectionId, observation));
                Assert.True(collector.Accept(collectionId, observation)); // A repeated callback must not block.
            }
            return true;
        });
        using var services = new ServiceCollection().AddSingleton<IWorkflowTrigger>(trigger).BuildServiceProvider();
        collector = new(new Factory(), services.GetRequiredService<IServiceScopeFactory>(), fixture.Options,
            TimeProvider.System, NullLogger<DtekPopupCollector>.Instance);
        var result = await collector.CollectAsync(CancellationToken.None);
        Assert.Equal(fixture.Options.CurrentValue.Regions.Count(r => !string.IsNullOrEmpty(r.SiteUrl)), result.Count);
        Assert.Contains(result, r => r.RegionId == "odesa" && r.PopupText!.Contains("Графіки"));
        Assert.False(collector.Accept(collectionId, result[0]));
    }

    [Fact]
    public async Task FailedDispatch_LeavesMissingRegionsUnavailable_NotClear()
    {
        using var fixture = new DispatcherFixture();
        using var services = new ServiceCollection().AddSingleton<IWorkflowTrigger>(new Trigger(_ => false)).BuildServiceProvider();
        var collector = new DtekPopupCollector(new Factory(), services.GetRequiredService<IServiceScopeFactory>(), fixture.Options,
            TimeProvider.System, NullLogger<DtekPopupCollector>.Instance);
        var result = await collector.CollectAsync(CancellationToken.None);
        Assert.Equal("odesa", Assert.Single(result).RegionId);
    }

    [Fact]
    public async Task BrowserTimeout_IsBounded_AndLateReplyIsNotAcceptedIntoNextCycle()
    {
        using var fixture = new DispatcherFixture();
        string? collectionId = null;
        var trigger = new Trigger(payload =>
        {
            collectionId = JsonSerializer.SerializeToElement(payload).GetProperty("collectionId").GetString();
            return true;
        });
        using var services = new ServiceCollection().AddSingleton<IWorkflowTrigger>(trigger).BuildServiceProvider();
        fixture.Settings.Timers.BrowserWaitSeconds = 1;
        var collector = new DtekPopupCollector(new Factory(), services.GetRequiredService<IServiceScopeFactory>(), fixture.Options,
            TimeProvider.System, NullLogger<DtekPopupCollector>.Instance);
        var result = await collector.CollectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("odesa", Assert.Single(result).RegionId);
        Assert.False(collector.Accept(collectionId, new("kyiv", null, DateTimeOffset.UtcNow)));
    }
}
