using System.Net.Http.Json;
using DtekBot.Api.Data;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class GroupScheduleImageTests
{
    private const long ChatId = -94001;
    private static List<ChatAddress> Addresses() =>
    [
        new() { RegionId = "kyiv", Name = "Дім", City = "Київ", Groups = [new() { GroupId = "45.1" }, new() { GroupId = "45.2" }] },
        new() { RegionId = "kyiv", Name = "Офіс", City = "Київ", Groups = [new() { GroupId = "45.1" }] },
        new() { RegionId = "kyiv-oblast", Name = "Дача", City = "Бровари", Groups = [new() { GroupId = "45.1" }] },
    ];
    private static ScheduleSnapshot Snapshot(string file = "kyiv") => RealSnapshot() with
    {
        DataFile = file,
        Groups = new Dictionary<string, GroupSchedule>
        {
            ["45.1"] = new("45.1", [new(Kyiv(15, 11), Kyiv(15, 14))]),
            ["45.2"] = new("45.2", [new(Kyiv(15, 15), Kyiv(15, 17))]),
        },
    };

    [Theory]
    [InlineData(ScheduleImage.Grid, PostMode.Edit)]
    [InlineData(ScheduleImage.Card, PostMode.Edit)]
    [InlineData(ScheduleImage.Grid, PostMode.New)]
    [InlineData(ScheduleImage.Card, PostMode.New)]
    [InlineData(ScheduleImage.Grid, PostMode.ReplaceDelete)]
    [InlineData(ScheduleImage.Card, PostMode.ReplaceDelete)]
    public async Task Images_KeepDistinctGroupsRegionsAndDeliveryModes(ScheduleImage image, PostMode mode)
    {
        using var f = new DispatcherFixture();
        f.AddChannel(ChatId, c =>
        {
            c.Addresses = Addresses(); c.MirrorPrimary();
            c.SchedulePost = true; c.SchedulePostImage = image; c.SchedulePostMode = mode;
            c.QuietHoursEnabled = true; c.QuietHoursStartMinute = 0; c.QuietHoursEndMinute = 1439;
        });
        f.Db.ChannelAdmins.Add(new() { ChatId = ChatId, UserId = 94009 });
        await f.Db.SaveChangesAsync();
        f.Bot.SetStatus(94009, ChatMemberStatus.Administrator);
        var provider = new FakeScheduleProvider();
        provider.Snapshots["kyiv"] = Snapshot();
        provider.Snapshots["kyiv-oblast"] = Snapshot("kyiv-region");
        var clock = new TestClock(Kyiv(15, 12));
        var notifier = new ScheduleNotifier(f.Db, f.Bot, provider, f.Options, clock,
            NullLogger<ScheduleNotifier>.Instance, f.CreateMirror(time: clock));

        Assert.Equal(3, (await notifier.TickAsync(default)).Sent);
        var photos = f.Bot.Requests.OfType<SendPhotoRequest>().ToArray();
        Assert.Equal(3, photos.Length);
        Assert.All(photos, p => Assert.True(p.DisableNotification));
        var suffix = image == ScheduleImage.Card ? "summary.png" : "emergency.png";
        Assert.Contains(photos, p => ((InputFileUrl)p.Photo).Url.ToString().Contains($"images/kyiv/gpv-45-2-{suffix}"));
        Assert.Contains(photos, p => ((InputFileUrl)p.Photo).Url.ToString().Contains($"images/kyiv-region/gpv-45-1-{suffix}"));
        Assert.DoesNotContain(photos.Single(p => p.Caption!.Contains("Черга 45.2")).Caption!, "Офіс");
        var posts = await f.Db.SchedulePostThreads.AsNoTracking().ToListAsync();
        Assert.Equal(3, posts.Count);
        Assert.Equal(3, posts.Select(p => p.MessageId).Distinct().Count());
        Assert.All(posts, p => Assert.True(p.IsPhoto));
        Assert.Equal(3, f.Bot.Requests.OfType<CopyMessageRequest>().Count());
        f.Db.ChangeTracker.Clear();
        Assert.Equal(3, (await notifier.TickAsync(default)).Skipped);
        Assert.Equal(3, f.Bot.Requests.OfType<SendPhotoRequest>().Count());

        provider.Snapshots["kyiv"] = Snapshot() with
        {
            Groups = new Dictionary<string, GroupSchedule>(Snapshot().Groups)
            {
                ["45.2"] = new("45.2", [new(Kyiv(15, 15), Kyiv(15, 18))]),
            },
        };
        var update = await notifier.TickAsync(default);
        if (mode == PostMode.Edit)
        {
            Assert.Equal(1, update.Edited);
            Assert.Equal(posts.Single(p => p.RegionId == "kyiv" && p.GroupId == "45.2").MessageId,
                Assert.Single(f.Bot.Requests.OfType<EditMessageCaptionRequest>()).MessageId);
        }
        else
        {
            Assert.Equal(1, update.Sent);
            Assert.Equal(4, f.Bot.Requests.OfType<SendPhotoRequest>().Count());
        }
        Assert.Equal(mode == PostMode.ReplaceDelete ? 1 : 0,
            f.Bot.Requests.OfType<DeleteMessageRequest>().Count(r => r.ChatId.Identifier == ChatId));
        Assert.Equal(3, (await notifier.TickAsync(default)).Skipped);
    }

    [Fact]
    public async Task FailedGroup_RetriesWithoutDuplicatingOtherGroups()
    {
        using var f = new DispatcherFixture();
        f.AddChannel(ChatId, c =>
        {
            c.Addresses = Addresses(); c.MirrorPrimary(); c.SchedulePost = true; c.SchedulePostImage = ScheduleImage.Card;
        });
        var provider = new FakeScheduleProvider();
        provider.Snapshots["kyiv"] = Snapshot();
        provider.Snapshots["kyiv-oblast"] = Snapshot("kyiv-region");
        var notifier = new ScheduleNotifier(f.Db, f.Bot, provider, f.Options, new TestClock(Kyiv(15, 12)),
            NullLogger<ScheduleNotifier>.Instance, f.CreateMirror());
        f.Bot.Failure = r => r is SendPhotoRequest p && ((InputFileUrl)p.Photo).Url.ToString().Contains("gpv-45-2-")
            ? new HttpRequestException("offline") : null;
        var first = await notifier.TickAsync(default);
        Assert.Equal(2, first.Sent);
        Assert.Equal(1, first.Failed);
        Assert.Equal(2, await f.Db.SchedulePostThreads.CountAsync());
        f.Bot.Failure = null;
        var retry = await notifier.TickAsync(default);
        Assert.Equal(1, retry.Sent);
        Assert.Equal(2, retry.Skipped);
        Assert.Equal(3, await f.Db.SchedulePostThreads.CountAsync());
    }

    [Fact]
    public async Task SwitchingImageAndText_TracksAndReplacesOnlyTheOldLayout()
    {
        using var f = new DispatcherFixture();
        var channel = f.AddChannel(ChatId, c =>
        {
            c.Addresses = Addresses().Take(2).ToList(); c.MirrorPrimary();
            c.SchedulePost = true; c.SchedulePostMode = PostMode.Edit; c.SchedulePostImage = ScheduleImage.None;
        });
        var provider = new FakeScheduleProvider(); provider.Snapshots["kyiv"] = Snapshot();
        var notifier = new ScheduleNotifier(f.Db, f.Bot, provider, f.Options, new TestClock(Kyiv(15, 12)),
            NullLogger<ScheduleNotifier>.Instance, f.CreateMirror());
        Assert.Equal(1, (await notifier.TickAsync(default)).Sent);
        var digestId = (await f.Db.SchedulePostThreads.SingleAsync()).MessageId;
        channel.SchedulePostImage = ScheduleImage.Grid;
        Assert.Equal(2, (await notifier.TickAsync(default)).Sent);
        Assert.Equal(2, await f.Db.SchedulePostThreads.CountAsync());
        Assert.Equal(digestId, Assert.Single(f.Bot.Requests.OfType<DeleteMessageRequest>()).MessageId);
        channel.SchedulePostImage = ScheduleImage.None;
        Assert.Equal(1, (await notifier.TickAsync(default)).Sent);
        Assert.Equal("", (await f.Db.SchedulePostThreads.SingleAsync()).GroupId);
        Assert.Equal(3, f.Bot.Requests.OfType<DeleteMessageRequest>().Count());
        Assert.Equal(1, (await notifier.TickAsync(default)).Skipped);
    }

    [Fact]
    public async Task PreviewAndTestSend_UseSeparateSavedGroupPicturesWithoutAutomaticSends()
    {
        using var api = new ApiFactory();
        using var client = api.ClientFor(94002);
        api.Schedules.Snapshots["kyiv"] = Snapshot();
        api.Schedules.Snapshots["kyiv-oblast"] = Snapshot("kyiv-region");
        api.WithDb(db => db.Channels.Add(new()
        {
            ChatId = 94002, RegionId = "kyiv", Addresses = Addresses(), SchedulePostImage = ScheduleImage.Card,
        }));
        var chat = Assert.Single((await client.GetFromJsonAsync<List<ChatSummaryDto>>("/api/chats", ApiJson.Options))!);
        var response = await client.PostAsJsonAsync("/api/preview", new PreviewRequest(null, AlertKind.Emergency,
            "kyiv", null, Slot: MessageSlots.Schedule, Addresses: chat.Settings.Addresses, ImageKind: ScheduleImage.Card), ApiJson.Options);
        response.EnsureSuccessStatusCode();
        var preview = (await response.Content.ReadFromJsonAsync<PreviewResponse>(ApiJson.Options))!;
        Assert.Equal(2, preview.Messages!.Count);
        Assert.All(preview.Messages, m => Assert.Contains("summary.png", m.ImageUrl!));
        Assert.Equal(preview.Messages[0].Html, preview.Html);
        Assert.Empty(api.Bot.Sent);
        var sent = await client.PostAsJsonAsync("/api/chats/94002/templates/schedule/send", new SendTestRequest(null), ApiJson.Options);
        sent.EnsureSuccessStatusCode();
        var photos = api.Bot.Requests.OfType<SendPhotoRequest>().ToArray();
        Assert.Equal(3, photos.Length);
        Assert.Equal("🧪 <b>Тест</b>\n\n" + preview.Messages[0].Html, photos[0].Caption);
        Assert.Contains(photos, p => ((InputFileUrl)p.Photo).Url.ToString().Contains("images/kyiv-region/"));
        Assert.Empty(api.Bot.Requests.OfType<CopyMessageRequest>());
        api.WithDb(db => Assert.Empty(db.SchedulePostThreads));
    }

    [Fact]
    public void Migration_ExpandsKeyAndPreservesTrackedMessages()
    {
        using var db = new BotDbContext(new DbContextOptionsBuilder<BotDbContext>()
            .UseSqlServer("Server=localhost;Database=dtek_migration_test;TrustServerCertificate=True").Options);
        var migration = Assert.Single(db.Database.GetMigrations(), id => id.EndsWith("_GroupScheduleImages", StringComparison.Ordinal));
        var script = db.GetService<IMigrator>().GenerateScript(
            fromMigration: "20261101150040_MultiRegionAddresses", toMigration: migration);
        Assert.Contains("PRIMARY KEY ([ChatId], [RegionId], [GroupId])", script);
        Assert.Contains("UPDATE [SchedulePostThreads] SET [GroupId] = '' WHERE [MessageIds] IS NOT NULL", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("DELETE FROM", script);
    }
}
