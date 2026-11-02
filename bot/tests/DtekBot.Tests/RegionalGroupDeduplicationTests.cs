using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;

namespace DtekBot.Tests;

public sealed class RegionalGroupDeduplicationTests : IDisposable
{
    private const long Chat = 95001;
    private const string Group = "45.1";
    private readonly DispatcherFixture f = new();
    public void Dispose() => f.Dispose();

    private Channel Subscribe(Action<Channel>? configure = null) => f.AddChannel(Chat, c =>
    {
        c.GroupId = Group;
        c.NotifyOnEnd = true;
        configure?.Invoke(c);
    });

    private Task<DispatchSummary> GroupAsync(bool active = true) =>
        f.Dispatcher.DispatchGroupAsync(f.Kyiv, Group, active, default);

    private static ChatAddress Address(string part, string group = Group, string region = "kyiv") =>
        new() { RegionId = region, Name = part, PartIds = part, Groups = [new() { GroupId = group }] };

    [Fact]
    public async Task DeliveredRegionalEmergency_SuppressesRepeatedGroupPostsAcrossScopes()
    {
        Subscribe();
        await f.DispatchAsync(DispatcherFixture.EmergencyText);
        f.Db.ChangeTracker.Clear();
        f.Clock.Now = f.Clock.Now.AddMinutes(10);

        Assert.Equal(new(0, 0, 0, 0), await GroupAsync());
        Assert.Equal(new(0, 0, 0, 0), await GroupAsync());
        Assert.Single(f.Bot.Sent);
        var delivery = Assert.Single(f.Deliveries(Chat));
        Assert.Equal("kyiv", delivery.RegionId);
        Assert.Equal("[]", delivery.EmergencyPartsJson);
        Assert.False(delivery.IsClosed);
        Assert.Equal(new(0, 0, 0, 0), await GroupAsync(false));
        Assert.Single(f.Bot.Sent);
    }

    [Theory]
    [InlineData("holosiivskyi", true)]
    [InlineData("darnytskyi", false)]
    [InlineData("", false)]
    public async Task DistrictEmergency_SuppressesOnlyFullyCoveredGroupAddresses(string secondPart, bool suppressed)
    {
        Subscribe(c => c.Addresses = [Address("holosiivskyi"), Address(secondPart)]);
        await f.Dispatcher.DispatchAsync(f.Kyiv,
            new(PopupKind.Emergency, ["holosiivskyi"], "district emergency", "district-emergency"), null, default);

        Assert.Equal(suppressed ? 0 : 1, (await GroupAsync()).Sent);
        Assert.Equal(suppressed ? 1 : 2, f.Bot.Sent.Count());
    }

    [Fact]
    public async Task MixedBankNotice_DoesNotSuppressLeftBankGroupOrAnotherRegion()
    {
        Subscribe(c => c.Addresses =
        [
            Address("holosiivskyi", "45.2"),
            Address("darnytskyi"),
            Address("bucha", Group, "kyiv-oblast"),
        ]);
        var interpretation = new PopupInterpretation("parsed",
        [
            new("emergency", "districts", ["holosiivskyi"]),
            new("stabilization", "districts", ["darnytskyi"]),
        ]);
        var result = interpretation.Apply(new(PopupKind.Emergency, [], "mixed banks", "mixed-banks"));
        await f.Dispatcher.DispatchAsync(f.Kyiv, result, null, default);
        Assert.Equal(0, (await f.Dispatcher.DispatchGroupAsync(f.Kyiv, "45.2", true, default)).Sent);
        Assert.Equal(1, (await GroupAsync()).Sent);
        Assert.Equal(1, (await f.Dispatcher.DispatchGroupAsync(
            f.Settings.Regions.Single(r => r.Id == "kyiv-oblast"), Group, true, default)).Sent);
        Assert.Equal(3, f.Bot.Sent.Count());
    }

    [Theory]
    [InlineData("disabled")]
    [InlineData("failed")]
    [InlineData("unknown")]
    [InlineData("accident")]
    public async Task UndeliveredOrNonEmergencyRegionalNotice_DoesNotHideGroupEmergency(string reason)
    {
        Subscribe(c =>
        {
            if (reason == "disabled")
                c.SlotSettingMap = new Dictionary<string, SlotSettingDto> { [MessageSlots.Emergency] = new(false, false) };
        });
        if (reason == "failed") f.Bot.Failure = _ => new HttpRequestException("offline");
        await f.DispatchAsync(reason switch
        {
            "unknown" => DispatcherFixture.UnknownText,
            "accident" => DispatcherFixture.AccidentRightBankText,
            _ => DispatcherFixture.EmergencyText,
        });
        f.Bot.Failure = null;
        Assert.Equal(1, (await GroupAsync()).Sent);
        Assert.Contains("черга 45.1", f.Bot.Sent.Last().Text);
    }

    [Fact]
    public async Task RegionalClearance_WithGroupStillEmergency_DoesNotAnnounceAllClear()
    {
        Subscribe();
        await f.DispatchAsync(DispatcherFixture.EmergencyText);
        await GroupAsync();
        f.Db.GroupStates.Add(new() { RegionId = "kyiv", GroupId = Group, Today = GroupStatuses.Emergency, UpdatedAt = f.Clock.Now });
        await f.Db.SaveChangesAsync();

        await f.DispatchAsync(DispatcherFixture.StabilizationText);
        Assert.Single(f.Bot.Sent);
        Assert.True(Assert.Single(f.Deliveries(Chat)).IsClosed);
        Assert.Equal(1, (await GroupAsync()).Sent);
        Assert.Contains("черга 45.1", f.Bot.Sent.Last().Text);
        Assert.DoesNotContain(f.Bot.Sent, m => m.Text.Contains("Запис про відключення знято"));

        await GroupAsync(false);
        Assert.Equal(3, f.Bot.Sent.Count());
        Assert.NotNull(f.Bot.Sent.Last().ReplyParameters);
    }

    [Fact]
    public async Task GroupClearance_WhileRegionEmergency_ClosesItsOldThreadSilently()
    {
        Subscribe();
        await GroupAsync();
        await f.DispatchAsync(DispatcherFixture.EmergencyText);
        Assert.Equal(2, f.Bot.Sent.Count());

        Assert.Equal(1, (await GroupAsync(false)).Closed);
        Assert.Equal(2, f.Bot.Sent.Count());
        Assert.False(f.Deliveries(Chat).Single(d => d.RegionId == "kyiv").IsClosed);
        Assert.True(f.Deliveries(Chat).Single(d => d.RegionId.Contains(":group:")).IsClosed);
    }

    [Fact]
    public async Task CoveredGroupEmergency_RetiresItsOldThreadWithoutAnotherPost()
    {
        Subscribe();
        await GroupAsync();
        await f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.Equal(new(0, 0, 1, 0), await GroupAsync());
        Assert.Equal(2, f.Bot.Sent.Count());
        Assert.True(f.Deliveries(Chat).Single(d => d.RegionId.Contains(":group:")).IsClosed);
        Assert.False(f.Deliveries(Chat).Single(d => d.RegionId == "kyiv").IsClosed);
    }

    [Fact]
    public async Task BothSourcesClear_AnnouncesOnlyRegionalClearance()
    {
        Subscribe();
        await f.DispatchAsync(DispatcherFixture.EmergencyText);
        await GroupAsync();
        f.Db.GroupStates.Add(new() { RegionId = "kyiv", GroupId = Group, Today = "ScheduleApplies", UpdatedAt = f.Clock.Now });
        await f.Db.SaveChangesAsync();

        Assert.Equal(new(0, 0, 1, 0), await f.DispatchAsync(DispatcherFixture.StabilizationText));
        Assert.Equal(new(0, 0, 0, 0), await GroupAsync(false));
        Assert.Equal(2, f.Bot.Sent.Count());
        Assert.NotNull(f.Bot.Sent.Last().ReplyParameters);
    }

    [Fact]
    public async Task RegionalEmergencyBecomesAccident_RemovesSuppression()
    {
        Subscribe();
        await f.DispatchAsync(DispatcherFixture.EmergencyText);
        Assert.Equal(0, (await GroupAsync()).Sent);
        await f.DispatchAsync(DispatcherFixture.AccidentRightBankText);

        Assert.Null(f.Deliveries(Chat).Single(d => d.RegionId == "kyiv").EmergencyPartsJson);
        Assert.Equal(1, (await GroupAsync()).Sent);
    }

    [Fact]
    public async Task LegacyDeliveredRegionalMessage_GainsCoverageWithoutResending()
    {
        Subscribe();
        await f.DispatchAsync(DispatcherFixture.EmergencyText);
        var delivery = await f.Db.Deliveries.SingleAsync();
        delivery.EmergencyPartsJson = null;
        await f.Db.SaveChangesAsync();
        await f.DispatchAsync(DispatcherFixture.EmergencyText);
        Assert.Equal("[]", delivery.EmergencyPartsJson);
        Assert.Equal(0, (await GroupAsync()).Sent);
        Assert.Single(f.Bot.Sent);
    }

    [Fact]
    public async Task ChangedRegionalScope_RemovesOldCoverageOnlyAfterSuccessfulDelivery()
    {
        Subscribe(c => c.Addresses = [Address("holosiivskyi"), Address("darnytskyi")]);
        await f.DispatchAsync(DispatcherFixture.EmergencyText);
        f.Bot.Failure = _ => new HttpRequestException("offline");
        var failed = await f.Dispatcher.DispatchAsync(f.Kyiv,
            new(PopupKind.Emergency, ["holosiivskyi"], "", "narrowed"), null, default);
        Assert.Equal(1, failed.Failed);
        Assert.Equal("[]", f.Deliveries(Chat).Single().EmergencyPartsJson);
        f.Bot.Failure = null;
        Assert.Equal(0, (await GroupAsync()).Sent);

        var narrowed = await f.Dispatcher.DispatchAsync(f.Kyiv,
            new(PopupKind.Emergency, ["holosiivskyi"], "", "narrowed"), null, default);
        Assert.Equal(1, narrowed.Edited);
        Assert.Equal(1, (await GroupAsync()).Sent);
        Assert.Equal(2, f.Bot.Sent.Count());
    }
}
