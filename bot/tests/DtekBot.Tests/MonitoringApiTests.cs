using System.Net;
using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Groups;
using DtekBot.Api.Parsing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DtekBot.Tests;

public sealed class MonitoringApiTests
{
    private sealed class Popups : IDtekPopupCollector
    {
        public Task<IReadOnlyList<DtekPopupObservation>> CollectAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DtekPopupObservation>>([]);
    }

    private sealed class Groups(DateTimeOffset observedAt) : IGroupStatusCollector
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<GroupObservation>> CollectAsync(CancellationToken ct)
        {
            Assert.Equal(0, TickGate.Grid.CurrentCount);
            Assert.Equal(0, TickGate.Schedules.CurrentCount);
            Calls++;
            return Task.FromResult<IReadOnlyList<GroupObservation>>([new("kyiv",
                new Dictionary<string, GroupStatusEntry> { ["45.1"] = new(GroupStatuses.Emergency, "") }, observedAt)]);
        }
    }

    [Fact]
    public async Task BackupTick_RequiresSecret_AndUsesApiCollectionUnderBothGates()
    {
        using var api = new ApiFactory();
        var groups = new Groups(api.Clock.Now);
        using var configured = api.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IDtekPopupCollector>();
            services.AddSingleton<IDtekPopupCollector>(new Popups());
            services.RemoveAll<IGroupStatusCollector>();
            services.AddSingleton<IGroupStatusCollector>(groups);
        }));
        using var client = configured.CreateClient();
        using var rejected = await client.PostAsync("/ingest/monitoring/tick", null);
        Assert.Equal(HttpStatusCode.Unauthorized, rejected.StatusCode);
        Assert.Equal(0, groups.Calls);
        using (var scope = configured.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            db.Channels.Add(new Channel { ChatId = 9001, RegionId = "kyiv", GroupId = "45.1", IsActive = true });
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        using var response = await client.PostAsync("/ingest/monitoring/tick", null);
        response.EnsureSuccessStatusCode();
        Assert.Equal(1, groups.Calls);
        Assert.Single(api.Bot.SentTo(9001));
        using var saved = configured.Services.CreateScope();
        Assert.True(Assert.Single(saved.ServiceProvider.GetRequiredService<BotDbContext>().GroupStates).IsEmergency);
    }
}
