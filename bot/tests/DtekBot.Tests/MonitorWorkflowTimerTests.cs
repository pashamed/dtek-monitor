using DtekBot.Api.Checks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DtekBot.Tests;

public class MonitorWorkflowTimerTests
{
    private static MonitorWorkflowTimer Create(IWorkflowTrigger? trigger, Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        if (trigger is not null) services.AddSingleton(trigger);
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        return new MonitorWorkflowTimer(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            config, NullLogger<MonitorWorkflowTimer>.Instance);
    }

    [Fact]
    public async Task ATick_DispatchesTheEmergencyOnlyMonitorEvent()
    {
        var trigger = new FakeWorkflowTrigger();

        await Create(trigger).RunOnceAsync(CancellationToken.None);

        Assert.Equal([MonitorWorkflowTimer.WorkflowEvent], trigger.Events);
        Assert.Equal("check-emergency", MonitorWorkflowTimer.WorkflowEvent);
    }

    [Fact]
    public async Task AFailedOrBrokenDispatch_IsSwallowed()
    {
        await Create(new FakeWorkflowTrigger { Result = false }).RunOnceAsync(CancellationToken.None);
        // No trigger registered: resolving it throws inside the tick.
        await Create(null).RunOnceAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("0", "owner/repo", "token")]
    [InlineData("15", null, "token")]
    [InlineData("15", "owner/repo", null)]
    public async Task WhenOffOrNotConfigured_NothingIsDispatched(string minutes, string? repository, string? token)
    {
        var trigger = new FakeWorkflowTrigger();
        var timer = Create(trigger, new()
        {
            ["GitHub:MonitorIntervalMinutes"] = minutes,
            ["GitHub:Repository"] = repository,
            ["GitHub:DispatchToken"] = token,
        });

        await timer.StartAsync(CancellationToken.None);
        await timer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.StopAsync(CancellationToken.None);

        Assert.Empty(trigger.Events);
    }
}
