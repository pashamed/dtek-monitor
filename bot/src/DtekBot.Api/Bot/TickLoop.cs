using System.Diagnostics;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Bot;

// Collects sources in parallel before evaluating alerts and schedules. IIS must keep the application alive.
public sealed class TickLoop(
    IServiceScopeFactory scopes,
    IConfiguration config,
    IOptionsMonitor<DtekSettings> options,
    ILogger<TickLoop> logger,
    BackgroundStatus? status = null) : BackgroundService
{
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(1);
    private long _totalCycles;
    private long _heartbeatCycles;
    private long _lastHeartbeat = Stopwatch.GetTimestamp();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Ticks:Enabled", true))
        {
            status?.SetTicks("off: Ticks:Enabled=false");
            logger.LogWarning("Built-in ticks are off (Ticks:Enabled=false)");
            return;
        }

        var timers = options.CurrentValue.Timers;
        var interval = timers.MonitoringInterval;
        status?.SetTicks($"running: every {interval.TotalMinutes:0} min");
        logger.LogInformation("Built-in ticks started: first cycle in {Seconds} s, then every {Minutes} min",
            timers.MonitoringStartDelay.TotalSeconds, interval.TotalMinutes);

        try
        {
            await Task.Delay(timers.MonitoringStartDelay, stoppingToken);
            using var timer = new PeriodicTimer(interval);
            do
            {
                await RunOnceAsync(stoppingToken);
                interval = ApplyInterval(timer, interval);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            status?.SetTicks("stopped");
            logger.LogInformation("Built-in ticks stopped");
        }
    }

    // dtek-settings.json reloads on change, so a new interval applies from the next tick.
    private TimeSpan ApplyInterval(PeriodicTimer timer, TimeSpan current)
    {
        var wanted = options.CurrentValue.Timers.MonitoringInterval;
        if (wanted == current) return current;
        timer.Period = wanted;
        status?.SetTicks($"running: every {wanted.TotalMinutes:0} min");
        logger.LogInformation("Monitoring interval changed to {Minutes} min", wanted.TotalMinutes);
        return wanted;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!await TickGate.Grid.WaitAsync(0, ct)) return;
        try
        {
            var started = Stopwatch.GetTimestamp();
            if (await RunAsync(TickGate.Schedules, "monitoring", (sp, token) =>
                sp.GetRequiredService<MonitoringCycle>().RunAsync(token), ct))
            {
                RecordCycle(Stopwatch.GetElapsedTime(started));
            }
        }
        finally
        {
            TickGate.Grid.Release();
        }
    }

    // The first cycle and an hourly count confirm the loop is alive without a log line every few minutes.
    private void RecordCycle(TimeSpan duration)
    {
        status?.CycleFinished();
        _heartbeatCycles++;
        if (++_totalCycles == 1)
        {
            logger.LogInformation("First monitoring cycle finished in {Milliseconds} ms", (long)duration.TotalMilliseconds);
            return;
        }

        if (Stopwatch.GetElapsedTime(_lastHeartbeat) < HeartbeatInterval) return;
        logger.LogInformation("Monitoring cycles alive: {Cycles} finished in the last hour, last took {Milliseconds} ms",
            _heartbeatCycles, (long)duration.TotalMilliseconds);
        _heartbeatCycles = 0;
        _lastHeartbeat = Stopwatch.GetTimestamp();
    }

    private async Task<bool> RunAsync(SemaphoreSlim gate, string name, Func<IServiceProvider, CancellationToken, Task> tick, CancellationToken ct)
    {
        // Someone else is already running this tick: no need to queue behind it.
        if (!await gate.WaitAsync(0, ct))
        {
            return false;
        }

        try
        {
            using var scope = scopes.CreateScope();
            await tick(scope.ServiceProvider, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Built-in {Tick} tick failed", name);
            return false;
        }
        finally
        {
            gate.Release();
        }
    }
}
