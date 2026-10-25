using System.Threading.Channels;
using DtekBot.Api.Checks;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DtekBot.Tests;

public class MonitorWorkflowTimerTests
{
    private static MonitorWorkflowTimer Create(IWorkflowTrigger? trigger, Dictionary<string, string?>? settings = null,
        TimeProvider? time = null, int minutes = 15)
    {
        var services = new ServiceCollection();
        if (trigger is not null) services.AddSingleton(trigger);
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? []).Build();
        var options = new StaticOptions(new DtekSettings { Timers = { MonitorWorkflowIntervalMinutes = minutes } });
        return new MonitorWorkflowTimer(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            config, options, time ?? TimeProvider.System, NullLogger<MonitorWorkflowTimer>.Instance);
    }

    private sealed class ControlledTime : TimeProvider
    {
        public Channel<ControlledTimer> Timers { get; } = Channel.CreateUnbounded<ControlledTimer>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ControlledTimer(callback, state, dueTime, period);
            Timers.Writer.TryWrite(timer);
            return timer;
        }
    }

    private sealed class ControlledTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;
        public TaskCompletionSource Changed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Disposed { get; private set; }
        public void Fire()
        {
            if (!Disposed) callback(state);
        }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            (DueTime, Period) = (dueTime, period);
            Changed.TrySetResult();
            return true;
        }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingTrigger : IWorkflowTrigger
    {
        public Channel<string> Events { get; } = Channel.CreateUnbounded<string>();
        public Task<bool> TriggerAsync(string eventType, CancellationToken ct, object? payload = null)
        {
            Events.Writer.TryWrite(eventType);
            return Task.FromResult(false);
        }
    }

    [Fact]
    public async Task StartsAfterOneMinute_ThenRetriesEveryFifteenMinutes_AndStopsCleanly()
    {
        var time = new ControlledTime();
        var trigger = new RecordingTrigger();
        using var timer = Create(trigger, new()
        {
            ["GitHub:Repository"] = "owner/repo",
            ["GitHub:DispatchToken"] = "test-token",
        }, time);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await timer.StartAsync(timeout.Token);
        var startup = await time.Timers.Reader.ReadAsync(timeout.Token);
        Assert.Equal(TimeSpan.FromMinutes(1), startup.DueTime);
        Assert.False(trigger.Events.Reader.TryRead(out _));

        startup.Fire();
        var periodic = await time.Timers.Reader.ReadAsync(timeout.Token);
        Assert.Equal(TimeSpan.FromMinutes(15), periodic.DueTime);
        Assert.Equal(TimeSpan.FromMinutes(15), periodic.Period);
        Assert.Equal("check-emergency", await trigger.Events.Reader.ReadAsync(timeout.Token));
        Assert.False(trigger.Events.Reader.TryRead(out _));

        periodic.Fire();
        Assert.Equal("check-emergency", await trigger.Events.Reader.ReadAsync(timeout.Token));

        await timer.StopAsync(timeout.Token);
        Assert.True(periodic.Disposed);
        periodic.Fire();
        Assert.False(trigger.Events.Reader.TryRead(out _));
    }

    [Fact]
    public async Task AnEditedInterval_AppliesFromTheNextDispatch()
    {
        var time = new ControlledTime();
        var trigger = new RecordingTrigger();
        var settings = new DtekSettings();
        using var timer = new MonitorWorkflowTimer(
            new ServiceCollection().AddSingleton<IWorkflowTrigger>(trigger).BuildServiceProvider()
                .GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["GitHub:Repository"] = "owner/repo",
                ["GitHub:DispatchToken"] = "test-token",
            }).Build(),
            new StaticOptions(settings), time, NullLogger<MonitorWorkflowTimer>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await timer.StartAsync(timeout.Token);
        (await time.Timers.Reader.ReadAsync(timeout.Token)).Fire();
        var periodic = await time.Timers.Reader.ReadAsync(timeout.Token);
        await trigger.Events.Reader.ReadAsync(timeout.Token);

        settings.Timers.MonitorWorkflowIntervalMinutes = 30;
        periodic.Fire();
        await trigger.Events.Reader.ReadAsync(timeout.Token);
        await periodic.Changed.Task.WaitAsync(timeout.Token);

        Assert.Equal(TimeSpan.FromMinutes(30), periodic.Period);
        await timer.StopAsync(timeout.Token);
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
    [InlineData(0, "owner/repo", "token")]
    [InlineData(15, null, "token")]
    [InlineData(15, "owner/repo", null)]
    public async Task WhenOffOrNotConfigured_NothingIsDispatched(int minutes, string? repository, string? token)
    {
        var trigger = new FakeWorkflowTrigger();
        var timer = Create(trigger, new()
        {
            ["GitHub:Repository"] = repository,
            ["GitHub:DispatchToken"] = token,
        }, minutes: minutes);

        await timer.StartAsync(CancellationToken.None);
        await timer.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(5));
        await timer.StopAsync(CancellationToken.None);

        Assert.Empty(trigger.Events);
    }
}
