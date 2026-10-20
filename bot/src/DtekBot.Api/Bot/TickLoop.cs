namespace DtekBot.Api.Bot;

// Collects sources in parallel before evaluating alerts and schedules. IIS must keep the application alive.
public sealed class TickLoop(
    IServiceScopeFactory scopes,
    IConfiguration config,
    ILogger<TickLoop> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Ticks:Enabled", true))
        {
            logger.LogInformation("Built-in ticks are off (Ticks:Enabled=false)");
            return;
        }

        var minutes = Math.Clamp(config.GetValue("Ticks:IntervalMinutes", 5), 1, 60);
        logger.LogInformation("Built-in ticks every {Minutes} min", minutes);

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(minutes));
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (!await TickGate.Grid.WaitAsync(0, ct)) return;
        try
        {
            await RunAsync(TickGate.Schedules, "monitoring", (sp, token) =>
                sp.GetRequiredService<MonitoringCycle>().RunAsync(token), ct);
        }
        finally
        {
            TickGate.Grid.Release();
        }
    }

    private async Task RunAsync(SemaphoreSlim gate, string name, Func<IServiceProvider, CancellationToken, Task> tick, CancellationToken ct)
    {
        // Someone else is already running this tick: no need to queue behind it.
        if (!await gate.WaitAsync(0, ct))
        {
            return;
        }

        try
        {
            using var scope = scopes.CreateScope();
            await tick(scope.ServiceProvider, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Built-in {Tick} tick failed", name);
        }
        finally
        {
            gate.Release();
        }
    }
}
