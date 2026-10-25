using System.Text.Json;
using DtekBot.Api;
using DtekBot.Api.Checks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DtekBot.Tests;

public sealed class BackgroundStatusTests
{
    [Fact]
    public async Task HealthReportsWhyTimersAreOff()
    {
        using var factory = new ApiFactory();
        using var client = factory.CreateClient();

        using var json = JsonDocument.Parse(await client.GetStringAsync("/health"));
        var background = json.RootElement.GetProperty("background");

        Assert.Equal("ok", json.RootElement.GetProperty("status").GetString());
        Assert.Equal("off: Ticks:Enabled=false", background.GetProperty("ticks").GetProperty("state").GetString());
        Assert.Equal("off: GitHub:MonitorIntervalMinutes=0",
            background.GetProperty("monitorWorkflow").GetProperty("state").GetString());
        Assert.Equal(0, background.GetProperty("ticks").GetProperty("cycles").GetInt64());
        Assert.Equal(JsonValueKind.Null, background.GetProperty("monitorWorkflow").GetProperty("lastDispatchAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, background.GetProperty("startedAt").ValueKind);
    }

    [Fact]
    public async Task MonitorDispatchIsRecorded()
    {
        var status = new BackgroundStatus(TimeProvider.System);
        var services = new ServiceCollection();
        services.AddSingleton<IWorkflowTrigger>(new FakeWorkflowTrigger { Result = true });
        var timer = new MonitorWorkflowTimer(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(), TimeProvider.System, NullLogger<MonitorWorkflowTimer>.Instance, status);

        await timer.RunOnceAsync(CancellationToken.None);

        var snapshot = JsonSerializer.SerializeToElement(status.Snapshot()).GetProperty("monitorWorkflow");
        Assert.True(snapshot.GetProperty("lastDispatchAccepted").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, snapshot.GetProperty("lastDispatchAt").ValueKind);
    }
}
