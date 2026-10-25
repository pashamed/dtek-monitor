namespace DtekBot.Api;

// In-memory state of the background timers, reported by /health so a redeploy can be verified without server logs.
public sealed class BackgroundStatus(TimeProvider time)
{
    private readonly Lock _lock = new();
    private string _ticks = "not started";
    private DateTimeOffset? _lastCycleAt;
    private long _cycles;
    private string _monitorWorkflow = "not started";
    private DateTimeOffset? _lastDispatchAt;
    private bool? _lastDispatchAccepted;

    public DateTimeOffset StartedAt { get; } = time.GetUtcNow();

    public void SetTicks(string state)
    {
        lock (_lock) _ticks = state;
    }

    public long CycleFinished()
    {
        lock (_lock)
        {
            _lastCycleAt = time.GetUtcNow();
            return ++_cycles;
        }
    }

    public void SetMonitorWorkflow(string state)
    {
        lock (_lock) _monitorWorkflow = state;
    }

    public void Dispatched(bool accepted)
    {
        lock (_lock)
        {
            _lastDispatchAt = time.GetUtcNow();
            _lastDispatchAccepted = accepted;
        }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                startedAt = StartedAt,
                ticks = new { state = _ticks, lastCycleAt = _lastCycleAt, cycles = _cycles },
                monitorWorkflow = new
                {
                    state = _monitorWorkflow,
                    lastDispatchAt = _lastDispatchAt,
                    lastDispatchAccepted = _lastDispatchAccepted,
                },
            };
        }
    }
}
