namespace DtekBot.Api.Bot;

// Keeps two ticks (the Ukrenergo status and the schedule messages) from running twice at once: the workflow, an outside
// pinger and the built-in loop may all call them, and overlapping runs could post the same message twice.
public static class TickGate
{
    public static readonly SemaphoreSlim Grid = new(1, 1);
    public static readonly SemaphoreSlim Schedules = new(1, 1);
}
