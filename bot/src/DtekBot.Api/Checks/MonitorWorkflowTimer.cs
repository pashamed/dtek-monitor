namespace DtekBot.Api.Checks;

// GitHub's own cron is best-effort and often skips hours, so the backend dispatches the personal monitor workflow
// (src/monitor.js) on a steady interval. The workflow's cron stays only as a backup.
public sealed class MonitorWorkflowTimer(
    IServiceScopeFactory scopes,
    IConfiguration config,
    TimeProvider time,
    ILogger<MonitorWorkflowTimer> logger,
    BackgroundStatus? status = null) : BackgroundService
{
    // Regional popup check only; the per-address check stays with manual runs and check-dtek.
    public const string WorkflowEvent = "check-emergency";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = config.GetValue("GitHub:MonitorIntervalMinutes", 15);
        if (minutes <= 0)
        {
            status?.SetMonitorWorkflow("off: GitHub:MonitorIntervalMinutes=0");
            logger.LogInformation("Monitor workflow timer is off (GitHub:MonitorIntervalMinutes=0)");
            return;
        }

        if (string.IsNullOrWhiteSpace(config["GitHub:Repository"]) || string.IsNullOrWhiteSpace(config["GitHub:DispatchToken"]))
        {
            status?.SetMonitorWorkflow("off: GitHub:Repository or GitHub:DispatchToken is not set");
            logger.LogWarning("Monitor workflow timer is off: GitHub:Repository or GitHub:DispatchToken is not set");
            return;
        }

        minutes = Math.Clamp(minutes, 5, 60);
        status?.SetMonitorWorkflow($"running: every {minutes} min");
        logger.LogInformation("Monitor workflow timer started: first {Event} dispatch in 60 s, then every {Minutes} min",
            WorkflowEvent, minutes);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(60), time, stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes), time);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            status?.SetMonitorWorkflow("stopped");
            logger.LogInformation("Monitor workflow timer stopped");
        }
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
