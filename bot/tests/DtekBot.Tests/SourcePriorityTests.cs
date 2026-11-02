using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace DtekBot.Tests;

public sealed class SourcePriorityTests
{
    public static IEnumerable<object[]> Regions() =>
        RealSettings.Load().Regions.Select(r => new object[] { r.Id });

    [Theory]
    [MemberData(nameof(Regions))]
    public async Task PartialDtekClearance_PreservesGridCoverageForOtherAddressesAndRegions(string regionId)
    {
        using var f = new DispatcherFixture();
        var region = f.Settings.Regions.Single(r => r.Id == regionId);
        var parts = region.Parts.Where(p => p.Children.Count == 0).Take(2).Select(p => p.Id).ToArray();
        Assert.Equal(2, parts.Length);
        var otherRegion = f.Settings.Regions.First(r => r.Id != regionId);
        f.AddChannel(1, c => c.PartIdList = [parts[0]], regionId);
        f.AddChannel(2, c => c.PartIdList = [parts[1]], regionId);
        f.AddChannel(3, c => c.Addresses =
        [
            new() { RegionId = regionId, Name = "A", PartIds = parts[0] },
            new() { RegionId = regionId, Name = "B", PartIds = parts[1] },
        ], regionId);
        f.AddChannel(4, regionId: otherRegion.Id);
        f.Db.RegionStates.Add(new()
        {
            RegionId = regionId, Kind = PopupKind.Stabilization, PartIdList = [parts[0]],
            InterpretationJson = new PopupInterpretation("parsed", [new("stabilization", "districts", [parts[0]])]).ToJson(),
            Hash = "explicit-clearance", ObservedAt = f.Clock.Now, UpdatedAt = f.Clock.Now,
        });
        foreach (var id in new[] { regionId, otherRegion.Id })
            f.Db.GridStates.Add(new() { RegionId = id, Status = "emergency_outages", UpdatedAt = f.Clock.Now });
        await f.Db.SaveChangesAsync();
        var coordinator = new RegionAlertCoordinator(f.Db, f.Dispatcher, f.Classifier, f.Options,
            NullLogger<RegionAlertCoordinator>.Instance);

        await coordinator.RecomputeAsync(regionId, default);
        await coordinator.RecomputeAsync(otherRegion.Id, default);
        f.Db.ChangeTracker.Clear();

        Assert.Empty(f.Bot.SentTo(1));
        foreach (var chat in new long[] { 2, 3, 4 })
            Assert.Contains("(за даними Укренерго)", Assert.Single(f.Bot.SentTo(chat)).Text);
        foreach (var chat in new long[] { 2, 3 })
        {
            var coverage = Assert.Single(f.Deliveries(chat)).EmergencyPartsJson!;
            Assert.DoesNotContain($"\"{parts[0]}\"", coverage);
            Assert.Contains($"\"{parts[1]}\"", coverage);
        }
        await coordinator.RecomputeAsync(regionId, default);
        Assert.Empty(f.Bot.Edits);
        Assert.Empty(f.Bot.SentTo(1));
    }

    [Fact]
    public async Task GroupAlertAndClearance_KeepYasnoAttributionWithCustomTemplates()
    {
        using var f = new DispatcherFixture();
        f.AddChannel(1, c =>
        {
            c.GroupId = "45.1";
            c.NotifyOnEnd = true;
            c.TemplateMap = new Dictionary<string, string>
            {
                [MessageSlots.Local] = "<b>{region}</b>: {status}",
                [MessageSlots.Ended] = "<b>{region}</b>: {status}",
            };
        });

        await f.Dispatcher.DispatchGroupAsync(f.Kyiv, "45.1", true, default);
        await f.Dispatcher.DispatchGroupAsync(f.Kyiv, "45.1", false, default);

        Assert.Equal(2, f.Bot.Sent.Count());
        Assert.All(f.Bot.Sent, message => Assert.Contains("(за даними Yasno)", message.Text));
        Assert.NotNull(f.Bot.Sent.Last().ReplyParameters);
    }
}
