using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DtekBot.Api.Bot;
using DtekBot.Api.Checks;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DtekBot.Tests;

public sealed class PopupPipelineTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DirectAndCorrelatedBrowserObservations_UseTheSameApiInterpreter(bool browser, bool quotaFailure)
    {
        using var root = new ApiFactory();
        using var model = new ModelHandler(quotaFailure);
        using var source = new SourceHandler(browser);
        HttpClient? callback = null;
        using var factory = root.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Llm:ApiKey"] = "mock-pipeline-key",
                ["Llm:BaseUrl"] = "https://provider.test/v1",
            }));
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<DtekSettings>(s => s.Regions = [s.Regions.Single(r => r.Id == "kyiv")]);
                services.AddHttpClient(PopupInterpreter.ClientName).ConfigurePrimaryHttpMessageHandler(() => model);
                services.AddHttpClient(DtekPopupCollector.ClientName).ConfigurePrimaryHttpMessageHandler(() => source);
                services.AddSingleton<IWorkflowTrigger>(new Trigger(async (payload, ct) =>
                {
                    var json = JsonSerializer.SerializeToElement(payload);
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest");
                    request.Headers.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
                    request.Content = JsonContent.Create(new
                    {
                        regionId = "kyiv", popupText = PopupInterpretationTests.MixedKyiv,
                        collectionId = json.GetProperty("collectionId").GetString(), observedAt = root.Clock.Now,
                    });
                    using var response = await callback!.SendAsync(request, ct);
                    response.EnsureSuccessStatusCode();
                    var result = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
                    Assert.True(result.GetProperty("collected").GetBoolean());
                    Assert.Equal(0, model.Calls);
                    return true;
                }));
            });
        });
        using var client = factory.CreateClient();
        callback = client;
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DtekBot.Api.Data.BotDbContext>();
        db.Channels.Add(new() { ChatId = 1, RegionId = "kyiv", PartIdList = ["obolonskyi"], IsActive = true });
        db.Channels.Add(new() { ChatId = 2, RegionId = "kyiv", PartIdList = ["darnytskyi"], IsActive = true });
        await db.SaveChangesAsync();
        await scope.ServiceProvider.GetRequiredService<MonitoringCycle>().RunAsync(default);
        Assert.Equal(1, model.Calls);
        var stored = Assert.Single(db.RegionStates);
        Assert.Equal(quotaFailure ? "deterministic" : "llm", stored.PopupParser);
        var parsed = PopupInterpretation.Read(stored.InterpretationJson!)!;
        Assert.Equal(7, parsed.States.Single(s => s.Kind == "emergency").Districts.Count);
        Assert.Equal(3, parsed.States.Single(s => s.Kind == "stabilization").Districts.Count);
        Assert.Single(root.Bot.SentTo(1));
        Assert.Empty(root.Bot.SentTo(2));
    }

    private sealed class ModelHandler(bool quotaFailure) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var parsed = RealSettings.Classifier().Interpret("kyiv", PopupInterpretationTests.MixedKyiv);
            return Task.FromResult(quotaFailure ? new(HttpStatusCode.TooManyRequests)
                : PopupInterpretationTests.ToolResponse(parsed));
        }
    }

    private sealed class SourceHandler(bool browser) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(browser ? "<html>Imperva challenge</html>"
                    : "<meta name='csrf-token' content='test'><script>DisconSchedule={};</script>"
                    + "<div class='m-attention__container modal__container--firstPopup'><div class='m-attention__text'>"
                    + WebUtility.HtmlEncode(PopupInterpretationTests.MixedKyiv) + "</div></div>"),
            });
    }

    private sealed class Trigger(Func<object?, CancellationToken, Task<bool>> callback) : IWorkflowTrigger
    {
        public Task<bool> TriggerAsync(string eventType, CancellationToken ct, object? payload = null)
        {
            Assert.Equal("check-regions", eventType);
            return callback(payload, ct);
        }
    }
}
