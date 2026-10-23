namespace DtekBot.Api.Checks;

// GitHub's own cron is best-effort and often skips hours, so the backend dispatches the personal monitor workflow
// (src/monitor.js) on a steady interval. The workflow's cron stays only as a backup.
public sealed class MonitorWorkflowTimer(
    IServiceScopeFactory scopes,
    IConfiguration config,
    TimeProvider time,
    ILogger<MonitorWorkflowTimer> logger) : BackgroundService
{
    // Regional popup check only; the per-address check stays with manual runs and check-dtek.
    public const string WorkflowEvent = "check-emergency";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = config.GetValue("GitHub:MonitorIntervalMinutes", 15);
        if (minutes <= 0)
        {
            logger.LogInformation("Monitor workflow timer is off (GitHub:MonitorIntervalMinutes=0)");
            return;
        }

        if (string.IsNullOrWhiteSpace(config["GitHub:Repository"]) || string.IsNullOrWhiteSpace(config["GitHub:DispatchToken"]))
        {
            logger.LogInformation("Monitor workflow timer is off: GitHub:Repository or GitHub:DispatchToken is not set");
            return;
        }

        minutes = Math.Clamp(minutes, 5, 60);
        logger.LogInformation("Monitor workflow dispatched every {Minutes} min", minutes);

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
            // Shutting down.
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            if (!await scope.ServiceProvider.GetRequiredService<IWorkflowTrigger>().TriggerAsync(WorkflowEvent, ct))
            {
                logger.LogWarning("Monitor workflow dispatch failed; retrying on the next tick");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Monitor workflow dispatch failed");
        }
    }
}
