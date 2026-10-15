using DtekBot.Api.Checks;

namespace DtekBot.Tests;

// Hand-written controllable clock (Microsoft.Extensions.TimeProvider.Testing is not referenced).
public sealed class TestClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

public sealed class FakeWorkflowTrigger : IWorkflowTrigger
{
    public List<string> Events { get; } = [];

    public bool Result { get; set; } = true;

    public Task<bool> TriggerAsync(string eventType, CancellationToken ct)
    {
        Events.Add(eventType);
        return Task.FromResult(Result);
    }
}
