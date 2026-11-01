using System.Net;
using System.Net.Http.Json;
using DtekBot.Api.Checks;
using DtekBot.Api.Data;
using DtekBot.Api.Data.Migrations;
using DtekBot.Api.Grid;
using DtekBot.Api.MiniApp;
using DtekBot.Api.Schedules;
using DtekBot.Api.Svitlobot;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class MultiRegionChecksTests
{
    private static ChatAddress Address(string regionId, string name, string? group = null) => new()
    {
        RegionId = regionId, Name = name, City = "Settlement", Street = "Same street", House = "1",
        Groups = group is null ? [] : [new() { GroupId = group }],
    };

    [Fact]
    public void Migration_ExpandsThreadKeysWithoutDiscardingSavedData()
    {
        var operations = new MultiRegionAddresses().UpOperations;
        Assert.Equal(2, operations.OfType<AddColumnOperation>().Count());
        Assert.Equal(new[] { "ChatId", "RegionId", "GroupId" },
            operations.OfType<AddPrimaryKeyOperation>().Single(o => o.Table == "ScheduleThreads").Columns);
        Assert.Equal(new[] { "ChatId", "RegionId" },
            operations.OfType<AddPrimaryKeyOperation>().Single(o => o.Table == "SchedulePostThreads").Columns);
        Assert.DoesNotContain(operations, o => o is DropTableOperation or DropColumnOperation or DeleteDataOperation or SqlOperation);
    }

    [Fact]
    public async Task SchedulePersistence_PreservesMessageIdsForSameGroupAcrossRegions()
    {
        using var f = new DispatcherFixture();
        foreach (var region in new[] { "kyiv", "kyiv-oblast" })
        {
            f.Db.ScheduleThreads.Add(new() { ChatId = 92007, RegionId = region, GroupId = "45.1", MessageId = region == "kyiv" ? 101 : 102 });
            f.Db.SchedulePostThreads.Add(new() { ChatId = 92007, RegionId = region, MessageId = region == "kyiv" ? 201 : 202 });
        }
        await f.Db.SaveChangesAsync();
        Assert.Equal(new[] { 101, 102 }, f.Db.ScheduleThreads.OrderBy(t => t.RegionId).Select(t => t.MessageId));
        Assert.Equal(new[] { 201, 202 }, f.Db.SchedulePostThreads.OrderBy(t => t.RegionId).Select(t => t.MessageId));
    }

    [Fact]
    public void RegionProjection_PreservesSavedMainSettingsAndScopesIdenticalGroups()
    {
        var channel = new Channel
        {
            RegionId = "kyiv", PartIds = "darnytskyi", IsPaused = true,
            Addresses = [Address("kyiv", "City", "45.1"), Address("kyiv-oblast", "Oblast", "45.1")],
        };
        channel.Addresses[1].PartIds = "boryspil";
        var city = channel.ForRegion("kyiv");
        var oblast = channel.ForRegion("kyiv-oblast");
        Assert.Equal(new[] { "kyiv", "kyiv-oblast" }, channel.RegionIds());
        Assert.Equal(2, channel.GroupTargets().Count);
        Assert.Equal(new[] { "darnytskyi" }, city.PopupDistrictIds());
        Assert.Equal(new[] { "boryspil" }, oblast.PopupDistrictIds());
        Assert.Single(city.Addresses);
        Assert.Single(oblast.Addresses);
        Assert.True(oblast.IsPaused);
        Assert.True(oblast.IsMultiRegion);
        Assert.Equal("kyiv", channel.RegionId);
        Assert.Equal(2, channel.Addresses.Count);
        Assert.Equal("darnytskyi", channel.PartIds);
        channel.Addresses[1].PartIds = null;
        Assert.Empty(channel.ForRegion("kyiv-oblast").PopupDistrictIds());
    }

    [Fact]
    public async Task Checks_QueueEachAddressUnderItsOwnRegionWithSharedBudget()
    {
        using var f = new DispatcherFixture();
        var channel = f.AddChannel(92000, c =>
            c.Addresses = [Address("kyiv", "City"), Address("kyiv-oblast", "Oblast")]);
        var service = new AddressCheckService(f.Db, f.Bot, new FakeWorkflowTrigger(), f.Options, f.Clock,
            NullLogger<AddressCheckService>.Instance, f.CreateMirror());
        var answers = await service.RequestBatchAsync(channel, channel.Addresses, default);
        Assert.Equal(2, answers.Count);
        Assert.Equal(new[] { "kyiv", "kyiv-oblast" }, f.Db.AddressChecks.OrderBy(c => c.Id).Select(c => c.RegionId));
        Assert.Single(f.Db.AddressChecks, c => c.BudgetCharged);
        Assert.Single(f.Db.AddressChecks.Select(c => c.BatchId).Distinct());
        Assert.All(f.Db.AddressChecks, c => Assert.Contains(" · ", c.Label));
        var pending = await service.GetPendingAsync(default);
        Assert.Equal(2, pending.Count);
        Assert.Equal(new[] { "kyiv", "kyiv-oblast" }, pending.Select(s => s.RegionId).Order());
    }

    [Fact]
    public async Task CachedResultsAndDetectedGroups_DoNotCrossRegionsForIdenticalAddresses()
    {
        using var f = new DispatcherFixture();
        var channel = f.AddChannel(92001, c =>
            c.Addresses = [Address("kyiv", "City"), Address("kyiv-oblast", "Oblast")]);
        f.Db.AddressResults.Add(new()
        {
            RegionId = "kyiv", City = "Settlement", Street = "Same street", House = "1",
            Found = true, CheckedAt = f.Clock.Now,
        });
        await f.Db.SaveChangesAsync();
        var service = new AddressCheckService(f.Db, f.Bot, new FakeWorkflowTrigger(), f.Options, f.Clock,
            NullLogger<AddressCheckService>.Instance, f.CreateMirror());
        await service.RequestBatchAsync(channel, channel.Addresses, default);
        Assert.Equal("kyiv-oblast", Assert.Single(f.Db.AddressChecks).RegionId);
        await service.ProcessResultsAsync(new([
            new("kyiv-oblast", "Settlement", "Same street", new()
            {
                ["1"] = new(true, "", "", "", "", ["3.2"]),
            }),
        ]), default);
        Assert.Empty(channel.Addresses.Single(a => a.RegionId == "kyiv").Groups);
        Assert.Equal("3.2", Assert.Single(channel.Addresses.Single(a => a.RegionId == "kyiv-oblast").Groups).GroupId);
        Assert.Null(channel.GroupId);
        Assert.Equal(AddressCheckStatus.Done, Assert.Single(f.Db.AddressChecks).Status);
    }

    [Fact]
    public async Task PendingCheck_IsNotAppliedAfterAddressChangesRegion()
    {
        using var f = new DispatcherFixture();
        var channel = f.AddChannel(92004, c => c.Addresses = [Address("kyiv", "City")]);
        var service = new AddressCheckService(f.Db, f.Bot, new FakeWorkflowTrigger(), f.Options, f.Clock,
            NullLogger<AddressCheckService>.Instance, f.CreateMirror());
        await service.RequestBatchAsync(channel, channel.Addresses, default);
        channel.Addresses[0].RegionId = "kyiv-oblast";
        await f.Db.SaveChangesAsync();
        var sends = f.Bot.Sent.Count();
        await service.ProcessResultsAsync(new([
            new("kyiv", "Settlement", "Same street", new()
            {
                ["1"] = new(true, "", "", "", "", ["45.1"]),
            }),
        ]), default);
        Assert.Empty(channel.Addresses[0].Groups);
        Assert.Equal(sends, f.Bot.Sent.Count());
        Assert.Equal(AddressCheckStatus.Done, Assert.Single(f.Db.AddressChecks).Status);
    }

    [Fact]
    public void LegacySettings_DoNotOverwriteAnAddressInAnotherRegion()
    {
        var address = Address("kyiv-oblast", "Oblast", "3.2");
        var channel = new Channel { RegionId = "kyiv", Addresses = [address] };
        channel.UpdateLegacyPrimary();
        Assert.Single(channel.Addresses);
        Assert.Equal("kyiv-oblast", address.RegionId);
        channel.AddressStreet = "New street";
        channel.UpdateLegacyPrimary();
        Assert.Equal(2, channel.Addresses.Count);
        Assert.Equal("Same street", address.Street);
        Assert.Equal("New street", channel.Addresses.Single(a => a.RegionId == "kyiv").Street);
    }

    [Fact]
    public async Task Assessment_RegionalEmergencyDoesNotExplainAnotherRegionsAddress()
    {
        using var f = new DispatcherFixture();
        var channel = f.AddChannel(92002, c =>
            c.Addresses = [Address("kyiv", "City"), Address("kyiv-oblast", "Oblast")]);
        f.Db.GridStates.Add(new() { RegionId = "kyiv", Status = GridStatuses.EmergencyOutages, FeedGeneratedAt = f.Clock.Now });
        await f.Db.SaveChangesAsync();
        var provider = new FakeScheduleProvider();
        var checks = new AddressCheckService(f.Db, f.Bot, new FakeWorkflowTrigger(), f.Options, f.Clock,
            NullLogger<AddressCheckService>.Instance, f.CreateMirror());
        var assessment = new OutageAssessmentService(f.Db, checks, provider, f.Options, f.Clock);
        var result = await assessment.AssessAsync(channel, default);
        Assert.Contains("City · м. Київ: Екстрені", result);
        Assert.Contains("Oblast · Київська область: Перевіряю", result);
        Assert.Equal("kyiv-oblast", Assert.Single(f.Db.AddressChecks).RegionId);
    }

    [Fact]
    public async Task Diagnostics_ReadEachRegionsOwnAddressResultsAndGroupStatus()
    {
        using var f = new DispatcherFixture();
        var channel = f.AddChannel(92003, c =>
            c.Addresses = [Address("kyiv", "City", "45.1"), Address("kyiv-oblast", "Oblast", "45.1")]);
        f.Db.GroupStates.Add(new() { RegionId = "kyiv-oblast", GroupId = "45.1", Today = GroupStatuses.Emergency });
        f.Db.AddressResults.Add(new()
        {
            RegionId = "kyiv-oblast", City = "Settlement", Street = "Same street", House = "1",
            Found = true, CheckedAt = f.Clock.Now,
        });
        await f.Db.SaveChangesAsync();
        var status = new ChatStatusService(f.Db, new FakeScheduleProvider(), f.Options, f.Clock);
        var city = await status.BuildAsync(channel, default, "kyiv");
        var oblast = await status.BuildAsync(channel, default, "kyiv-oblast");
        Assert.DoesNotContain(city, s => s.Contains("Yasno"));
        Assert.Contains(oblast, s => s.Contains("Yasno"));
        channel.Addresses.RemoveAll(a => a.RegionId == "kyiv");
        Assert.Contains(await status.BuildAsync(channel, default), s => s.Contains("Yasno"));
        channel.Addresses.Insert(0, Address("kyiv", "City", "45.1"));
        var diagnostics = await status.DiagnosticsAsync(channel, default);
        Assert.Contains(diagnostics.Lines, s => s == "📍 м. Київ");
        Assert.Contains(diagnostics.Lines, s => s == "📍 Київська область");
        Assert.Contains(diagnostics.Lines, s => s.Contains("«City»: успішної перевірки ще немає"));
        Assert.Contains(diagnostics.Lines, s => s.Contains("«Oblast»: запису про відключення немає"));
    }

    [Fact]
    public async Task SvitlobotSelection_UsesAddressRegionEvenWhenGroupNumberMatches()
    {
        using var api = new ApiFactory();
        var user = api.NextId();
        using var client = api.ClientFor(user);
        api.WithDb(db => db.Channels.Add(new()
        {
            ChatId = user, RegionId = "kyiv",
            Addresses = [Address("kyiv", "City", "45.1"), Address("kyiv-oblast", "Oblast", "45.1")],
        }));
        var addressId = api.WithDb(db => db.ChatAddresses.Single(a => a.RegionId == "kyiv-oblast").Id);
        var response = await client.PutAsJsonAsync($"/api/chats/{user}/svitlobot/group",
            new SetSvitlobotGroupRequest("45.1", "kyiv-oblast", addressId), ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<SvitlobotGroupDto>(ApiJson.Options);
        Assert.Equal("kyiv-oblast", result!.RegionId);
        Assert.Equal(addressId, result.AddressId);
        api.WithDb(db =>
        {
            var target = db.Channels.Single().SvitlobotTarget();
            Assert.Equal("kyiv-oblast", target!.RegionId);
            Assert.Equal(addressId, target.AddressId);
        });
        var bad = await client.PutAsJsonAsync($"/api/chats/{user}/svitlobot/group",
            new SetSvitlobotGroupRequest("45.1", "kyiv", addressId), ApiJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task SvitlobotPush_UsesSelectedRegionsWeeklySchedule()
    {
        using var api = new ApiFactory();
        const long user = 92005;
        api.Clock.Now = Kyiv(22, 12);
        using var client = api.ClientFor(user);
        var cityWeek = string.Join(';', Enumerable.Repeat("110000000000000000000000", 7));
        var oblastWeek = string.Join(';', Enumerable.Repeat("000011000000000000000000", 7));
        api.Schedules.Snapshots["kyiv"] = RealSnapshot() with
        {
            WeeklyPresets = new Dictionary<string, string> { ["45.1"] = cityWeek },
            WeeklyPresetsWithMaybe = new Dictionary<string, string> { ["45.1"] = cityWeek },
        };
        api.Schedules.Snapshots["kyiv-oblast"] = RealSnapshot(dataFile: "kyiv-region") with
        {
            WeeklyPresets = new Dictionary<string, string> { ["45.1"] = oblastWeek },
            WeeklyPresetsWithMaybe = new Dictionary<string, string> { ["45.1"] = oblastWeek },
        };
        var protector = new SvitlobotKeyProtector(() => api.SvitlobotEncryptionKey);
        api.WithDb(db => db.Channels.Add(new()
        {
            ChatId = user, RegionId = "kyiv", SvitlobotKey = protector.Protect("mixed-push"),
            SvitlobotGroupId = "45.1", SvitlobotRegionId = "kyiv-oblast",
            Addresses = [Address("kyiv", "City", "45.1"), Address("kyiv-oblast", "Oblast", "45.1")],
        }));
        api.Svitlobot.Responder = _ => FakeSvitlobotHandler.Text("ok");
        var pushed = await client.PostAsJsonAsync($"/api/chats/{user}/svitlobot/push", new SvitlobotPushRequest(), ApiJson.Options);
        Assert.Equal(HttpStatusCode.OK, pushed.StatusCode);
        Assert.Contains(api.Schedules.Calls, id => id == "kyiv-oblast");
        Assert.DoesNotContain(api.Schedules.Calls, id => id == "kyiv");
        Assert.Contains(api.Svitlobot.Requests, request => request.PathAndQuery.Contains(Uri.EscapeDataString(oblastWeek)));
    }

    [Fact]
    public async Task SvitlobotSync_IncludesNonMainAddressGroups()
    {
        using var api = new ApiFactory();
        api.Clock.Now = Kyiv(22, 12);
        var week = string.Join(';', Enumerable.Repeat("000011000000000000000000", 7));
        api.Schedules.Snapshots["kyiv-oblast"] = RealSnapshot(dataFile: "kyiv-region") with
        {
            WeeklyPresets = new Dictionary<string, string> { ["45.1"] = week },
            WeeklyPresetsWithMaybe = new Dictionary<string, string> { ["45.1"] = week },
        };
        var protector = new SvitlobotKeyProtector(() => api.SvitlobotEncryptionKey);
        api.WithDb(db => db.Channels.Add(new()
        {
            ChatId = 92006, RegionId = "kyiv", SvitlobotKey = protector.Protect("mixed-sync"),
            Addresses = [Address("kyiv-oblast", "Oblast", "45.1")],
        }));
        api.Svitlobot.Responder = _ => FakeSvitlobotHandler.Text("ok");
        using var scope = api.Services.CreateScope();
        var sync = scope.ServiceProvider.GetRequiredService<SvitlobotScheduleSyncService>();
        Assert.Equal(1, await sync.TickAsync(default));
        Assert.Contains(api.Schedules.Calls, id => id == "kyiv-oblast");
        Assert.DoesNotContain(api.Schedules.Calls, id => id == "kyiv");
        Assert.Contains(api.Svitlobot.Requests, request => request.PathAndQuery.Contains(Uri.EscapeDataString(week)));
    }
}
