using DtekBot.Api.Groups;

namespace DtekBot.Tests;

public sealed class FakeGroupStatusCollector : IGroupStatusCollector
{
    public IReadOnlyList<GroupObservation> Observations { get; set; } = [];
    public Task<IReadOnlyList<GroupObservation>> CollectAsync(CancellationToken ct) => Task.FromResult(Observations);
}
