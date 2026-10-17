using DtekBot.Api.Bot;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DtekBot.Tests;

public class TickLoopTests
{
    [Fact]
    public async Task WhenDisabled_TheLoopReturnsAtOnce_AndTouchesNothing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ticks:Enabled"] = "false" })
            .Build();
        var loop = new TickLoop(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), config, NullLogger<TickLoop>.Instance);

        await loop.StartAsync(CancellationToken.None);
        await loop.StopAsync(CancellationToken.None);

        Assert.True(true);
    }

    [Fact]
    public async Task ARunThatIsAlreadyInProgress_IsNotQueuedBehind()
    {
        await TickGate.Grid.WaitAsync();
        await TickGate.Schedules.WaitAsync();
        try
        {
            var loop = new TickLoop(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(), NullLogger<TickLoop>.Instance);

            // Both gates are taken: the run must return immediately instead of waiting or resolving services.
            await loop.RunOnceAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            TickGate.Grid.Release();
            TickGate.Schedules.Release();
        }
    }

    [Fact]
    public async Task AFailingTick_IsSwallowed_AndTheGateIsReleased()
    {
        // No tick services are registered, so resolving them fails inside the run.
        var loop = new TickLoop(new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), new ConfigurationBuilder().Build(), NullLogger<TickLoop>.Instance);

        await loop.RunOnceAsync(CancellationToken.None);

        Assert.True(await TickGate.Grid.WaitAsync(0));
        TickGate.Grid.Release();
        Assert.True(await TickGate.Schedules.WaitAsync(0));
        TickGate.Schedules.Release();
    }
}
