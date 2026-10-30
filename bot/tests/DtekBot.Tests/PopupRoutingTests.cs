using System.Net.Http.Json;
using System.Text.Json;
using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Grid;
using DtekBot.Api.Checks;
using DtekBot.Api.MiniApp;
using DtekBot.Api.Parsing;
using Microsoft.Extensions.DependencyInjection;

namespace DtekBot.Tests;

public sealed class PopupRoutingTests
{
    [Theory]
    [InlineData("правому", 7)]
    [InlineData("лівому", 3)]
    [InlineData("", 10)]
    public async Task Ingest_BankOrCityEmergency_ReachesExactlyAffectedDistricts(string bank, int count)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        AddDistrictChats(factory, "kyiv");
        var text = bank.Length == 0 ? "У Києві введені екстрені відключення."
            : $"На {bank} березі Києва введені екстрені відключення.";
        var result = await Ingest(client, factory, "kyiv", text);
        Assert.Equal("parsed", result.GetProperty("interpretation").GetProperty("status").GetString());
        var states = result.GetProperty("interpretation").GetProperty("states");
        Assert.Equal(count, states[0].GetProperty("districts").GetArrayLength());
        Assert.Equal(count, factory.WithDb(db => db.Deliveries.Count(d => d.ChatId < 1000 && !d.IsClosed)));
        Assert.Single(factory.Bot.SentTo(1000));
        Assert.Empty(factory.Bot.SentTo(2000));
        var sent = factory.Bot.Requests.Count;
        await Ingest(client, factory, "kyiv", text);
        Assert.Equal(sent, factory.Bot.Requests.Count);
    }

    [Theory]
    [MemberData(nameof(PopupInterpretationTests.Regions), MemberType = typeof(PopupInterpretationTests))]
    public async Task EveryRegion_IngestRoutesMixedStatesToMatchingDistrictsOnly(string regionId)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        var region = RealSettings.Load().Regions.Single(r => r.Id == regionId);
        var emergency = region.Parts.First(p => p.Children.Count == 0);
        var stable = region.Parts.Last(p => p.Children.Count == 0 && p.Id != emergency.Id);
        AddDistrictChats(factory, regionId);
        await Ingest(client, factory, regionId,
            $"{emergency.Name}: екстрені відключення. {stable.Name}: діють графіки стабілізаційних відключень.");
        var deliveries = factory.WithDb(db => db.Deliveries.Where(d => !d.IsClosed).Select(d => d.ChatId).ToArray());
        Assert.Equal(2, deliveries.Length);
        Assert.Contains(1000, deliveries);
        var index = region.Parts.FindIndex(p => p.Id == emergency.Id);
        Assert.Contains(index + 1, deliveries);
        Assert.Empty(factory.Bot.SentTo(region.Parts.FindIndex(p => p.Id == stable.Id) + 1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedNotice_ClosesLeftBankWithSchedulesWording_UnlessUkrenergoStillActive(bool gridActive)
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        AddDistrictChats(factory, "kyiv");
        await Ingest(client, factory, "kyiv", "У Києві введені екстрені відключення.");
        if (gridActive)
            factory.WithDb(db => db.GridStates.Add(new GridState
            {
                RegionId = "kyiv", Status = "emergency_outages", Title = "Екстрені відключення",
                FeedGeneratedAt = factory.Clock.Now, UpdatedAt = factory.Clock.Now,
            }));
        await Ingest(client, factory, "kyiv", PopupInterpretationTests.MixedKyiv);
        var region = RealSettings.Load().Regions.Single(r => r.Id == "kyiv");
        foreach (var id in region.RetiredParts["left-bank"])
        {
            var chat = region.Parts.FindIndex(p => p.Id == id) + 1;
            var delivery = factory.WithDb(db => db.Deliveries.Single(d => d.ChatId == chat));
            Assert.Equal(!gridActive, delivery.IsClosed);
            if (!gridActive)
            {
                var end = factory.Bot.SentTo(chat).Last().Text;
                Assert.Contains("графік", end, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("відновлено електропостачання", end, StringComparison.OrdinalIgnoreCase);
            }
        }
        Assert.Equal(gridActive ? 11 : 8, factory.WithDb(db => db.Deliveries.Count(d => !d.IsClosed)));
    }

    [Fact]
    public async Task StoredMixedStates_AreUsedByReadOnlyStatus_AndSurviveStaleOrLateObservations()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        AddDistrictChats(factory, "kyiv");
        await Ingest(client, factory, "kyiv", PopupInterpretationTests.MixedKyiv);
        var before = factory.WithDb(db => db.RegionStates.Single().InterpretationJson);
        using var scope = factory.Services.CreateScope();
        var status = scope.ServiceProvider.GetRequiredService<ChatStatusService>();
        var left = await status.BuildAsync(null, default, "kyiv", ["darnytskyi"]);
        var right = await status.BuildAsync(null, default, "kyiv", ["obolonskyi"]);
        Assert.Contains("стабілізаційні", left[0]);
        Assert.Contains("екстрені", right[0]);
        var requests = factory.Bot.Requests.Count;
        await Ingest(client, factory, "kyiv", "", factory.Clock.Now.AddMinutes(-1));
        await Ingest(client, factory, "kyiv", "", collectionId: "late-cycle");
        Assert.Equal(before, factory.WithDb(db => db.RegionStates.Single().InterpretationJson));
        Assert.Equal(requests, factory.Bot.Requests.Count);
    }

    [Fact]
    public async Task NonemptyIrrelevantNotice_IsUnknownRegionwide_EmptyVerifiedPopupClosesIt()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        AddDistrictChats(factory, "kyiv");
        var result = await Ingest(client, factory, "kyiv", "Шановні клієнти! Дякуємо за розуміння.");
        Assert.Equal("Unknown", result.GetProperty("kind").GetString());
        Assert.Equal("no_information", result.GetProperty("interpretation").GetProperty("status").GetString());
        Assert.Equal(11, factory.WithDb(db => db.Deliveries.Count(d => !d.IsClosed)));
        await Ingest(client, factory, "kyiv", "");
        Assert.Equal(0, factory.WithDb(db => db.Deliveries.Count(d => !d.IsClosed)));
    }

    [Fact]
    public async Task LegacyGeographyHint_CannotOverrideApiInterpretation()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest");
        request.Headers.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        request.Content = JsonContent.Create(new
        {
            regionId = "kyiv", popupText = PopupInterpretationTests.MixedKyiv,
            structured = new { scope = "districts", districts = new[] { "darnytskyi" } },
        });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var state = factory.WithDb(db => db.RegionStates.Single());
        Assert.Equal(7, state.PartIdList!.Count);
        Assert.DoesNotContain("darnytskyi", state.PartIdList);
    }

    [Fact]
    public async Task SingleAddressDistrict_ControlsStatusAndExplanationInsteadOfLegacyScalarDistrict()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();
        factory.WithDb(db => db.Channels.Add(new Channel
        {
            ChatId = 1, RegionId = "kyiv", PartIdList = ["obolonskyi"], IsActive = true,
            Addresses = [new ChatAddress { Name = "Дім", RegionId = "kyiv", PartIds = "darnytskyi" }],
        }));
        await Ingest(client, factory, "kyiv", PopupInterpretationTests.MixedKyiv);
        Assert.Empty(factory.Bot.SentTo(1));
        using var scope = factory.Services.CreateScope();
        var channel = scope.ServiceProvider.GetRequiredService<BotDbContext>().Channels.Single(c => c.ChatId == 1);
        var lines = await scope.ServiceProvider.GetRequiredService<ChatStatusService>().BuildAsync(channel, default);
        Assert.Contains("стабілізаційні", lines[0]);
        var explanation = await scope.ServiceProvider.GetRequiredService<OutageAssessmentService>().AssessAsync(channel, default);
        Assert.DoesNotContain("У вашому районі", explanation);
        Assert.Empty(factory.Bot.SentTo(1));
    }

    private static void AddDistrictChats(ApiFactory factory, string regionId)
    {
        var region = RealSettings.Load().Regions.Single(r => r.Id == regionId);
        factory.WithDb(db =>
        {
            for (var i = 0; i < region.Parts.Count; i++)
                db.Channels.Add(new Channel { ChatId = i + 1, RegionId = regionId, PartIdList = [region.Parts[i].Id], IsActive = true });
            db.Channels.Add(new Channel { ChatId = 1000, RegionId = regionId, IsActive = true });
            db.Channels.Add(new Channel { ChatId = 2000, RegionId = regionId == "kyiv" ? "kyiv-oblast" : "kyiv", IsActive = true });
        });
    }

    private static async Task<JsonElement> Ingest(HttpClient client, ApiFactory factory, string region, string text,
        DateTimeOffset? observedAt = null, string? collectionId = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/ingest");
        request.Headers.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        request.Content = JsonContent.Create(new { regionId = region, popupText = text, observedAt = observedAt ?? factory.Clock.Now, collectionId });
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).Clone();
    }
}
