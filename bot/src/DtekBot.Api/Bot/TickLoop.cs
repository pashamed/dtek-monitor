using DtekBot.Api.Grid;
using DtekBot.Api.Schedules;

namespace DtekBot.Api.Bot;

// Posts schedules (and the Ukrenergo status) soon after they arrive instead of waiting for the next workflow run:
// GitHub's cron is routinely hours late. It runs only while the application is alive; the workflow and any outside
// pinger keep calling the same ticks, which are safe to repeat because they only compare stored state with the sources.
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
        await RunAsync(TickGate.Grid, "grid", (sp, token) => sp.GetRequiredService<GridTickService>().TickAsync(token), ct);
        await RunAsync(TickGate.Schedules, "schedules", (sp, token) => sp.GetRequiredService<ScheduleNotifier>().TickAsync(token), ct);
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
