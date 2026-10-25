using DtekBot.Api.Settings;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Checks;

// GitHub's own cron is best-effort and often skips hours, so the backend dispatches the personal monitor workflow
// (src/monitor.js) on a steady interval. The workflow's cron stays only as a backup.
public sealed class MonitorWorkflowTimer(
    IServiceScopeFactory scopes,
    IConfiguration config,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time,
    ILogger<MonitorWorkflowTimer> logger,
    BackgroundStatus? status = null) : BackgroundService
{
    // Regional popup check only; the per-address check stays with manual runs and check-dtek.
    public const string WorkflowEvent = "check-emergency";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timers = options.CurrentValue.Timers;
        if (timers.MonitorWorkflowIntervalMinutes <= 0)
        {
            status?.SetMonitorWorkflow("off: Timers:MonitorWorkflowIntervalMinutes=0");
            logger.LogInformation("Monitor workflow timer is off (Timers:MonitorWorkflowIntervalMinutes=0)");
            return;
        }

        if (string.IsNullOrWhiteSpace(config["GitHub:Repository"]) || string.IsNullOrWhiteSpace(config["GitHub:DispatchToken"]))
        {
            status?.SetMonitorWorkflow("off: GitHub:Repository or GitHub:DispatchToken is not set");
            logger.LogWarning("Monitor workflow timer is off: GitHub:Repository or GitHub:DispatchToken is not set");
            return;
        }

        var interval = timers.MonitorWorkflowInterval;
        status?.SetMonitorWorkflow($"running: every {interval.TotalMinutes:0} min");
        logger.LogInformation("Monitor workflow timer started: first {Event} dispatch in {Seconds} s, then every {Minutes} min",
            WorkflowEvent, timers.MonitorWorkflowStartDelay.TotalSeconds, interval.TotalMinutes);

        try
        {
            await Task.Delay(timers.MonitorWorkflowStartDelay, time, stoppingToken);
            using var timer = new PeriodicTimer(interval, time);
            do
            {
                await RunOnceAsync(stoppingToken);
                interval = ApplyInterval(timer, interval);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            status?.SetMonitorWorkflow("stopped");
            logger.LogInformation("Monitor workflow timer stopped");
        }
    }

    // dtek-settings.json reloads on change, so a new interval applies from the next dispatch. 0 needs a restart.
    private TimeSpan ApplyInterval(PeriodicTimer timer, TimeSpan current)
    {
        var timers = options.CurrentValue.Timers;
        if (timers.MonitorWorkflowIntervalMinutes <= 0 || timers.MonitorWorkflowInterval == current) return current;
        timer.Period = timers.MonitorWorkflowInterval;
        status?.SetMonitorWorkflow($"running: every {timer.Period.TotalMinutes:0} min");
        logger.LogInformation("Monitor workflow interval changed to {Minutes} min", timer.Period.TotalMinutes);
        return timer.Period;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var accepted = await scope.ServiceProvider.GetRequiredService<IWorkflowTrigger>().TriggerAsync(WorkflowEvent, ct);
            status?.Dispatched(accepted);
            if (accepted)
            {
                logger.LogInformation("Monitor workflow dispatched ({Event})", WorkflowEvent);
            }
            else
            {
                logger.LogWarning("Monitor workflow dispatch failed; retrying on the next tick");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            status?.Dispatched(false);
            logger.LogWarning(ex, "Monitor workflow dispatch failed");
        }
    }
}
