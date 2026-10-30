using System.Net.Http.Json;
using DtekBot.Api.Bot;
using DtekBot.Api.Checks;
using DtekBot.Api.Data;
using DtekBot.Api.Groups;
using DtekBot.Api.MiniApp;
using DtekBot.Api.Schedules;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public sealed class PrivateNotificationMirrorTests
{
    private const long User = 91000;
    private const long First = -10091001;
    private const long Second = -10091002;

    [Fact]
    public async Task MultipleChannelsAndGroups_KeepIndependentCopiesAndPrivateSubscription()
    {
        using var api = new ApiFactory();
        using var client = api.CreateClient();
        client.DefaultRequestHeaders.Add("X-Ingest-Secret", ApiFactory.IngestSecret);
        api.Bot.SetStatus(User, ChatMemberStatus.Administrator);
        api.WithDb(db =>
        {
            db.Channels.AddRange(
                new Channel { ChatId = First, Title = "First", RegionId = "kyiv", GroupId = "45.1",
                    Addresses = [new() { Name = "Home", RegionId = "kyiv", Groups = [new() { GroupId = "45.1" }, new() { GroupId = "2.1" }] }] },
                new Channel { ChatId = Second, Title = "Second", RegionId = "kyiv", GroupId = "45.1" },
                new Channel { ChatId = User, RegionId = "kyiv", GroupId = "45.1" });
            db.ChannelAdmins.AddRange(
                new ChannelAdmin { ChatId = First, UserId = User },
                new ChannelAdmin { ChatId = Second, UserId = User });
        });

        await Groups(GroupStatuses.Emergency);
        var copies = api.Bot.Requests.OfType<CopyMessageRequest>().ToArray();
        Assert.Equal(3, copies.Length);
        Assert.Equal(2, copies.Count(c => c.FromChatId.Identifier == First));
        Assert.Single(copies, c => c.FromChatId.Identifier == Second);
        Assert.All(copies, c => Assert.Equal(User, c.ChatId.Identifier));
        Assert.Equal(3, api.Bot.SentTo(User).Count()); // Two source labels and the independent subscription.
        Assert.Contains(api.Bot.SentTo(User), m => m.Text.Contains("First"));
        Assert.Contains(api.Bot.SentTo(User), m => m.Text.Contains("Second"));
        api.WithDb(db =>
        {
            var stored = db.PrivateNotificationCopies.ToArray();
            Assert.Equal(5, stored.Length);
            Assert.Equal(3, stored.Where(c => c.SourceMessageId != 0).Select(c => c.MessageId).Distinct().Count());
            Assert.All(copies, copy =>
            {
                var header = stored.Single(c => c.SourceChatId == copy.FromChatId.Identifier && c.SourceMessageId == 0);
                Assert.Equal(header.MessageId, copy.ReplyParameters!.MessageId);
            });
        });

        var before = api.Bot.Requests.Count;
        await Groups(GroupStatuses.Emergency);
        Assert.Equal(before, api.Bot.Requests.Count);
        before = copies.Length;
        await Groups("");
        copies = api.Bot.Requests.OfType<CopyMessageRequest>().ToArray();
        Assert.Equal(6, copies.Length);
        api.WithDb(db =>
        {
            foreach (var copy in copies.Skip(before))
            {
                var parent = db.PrivateNotificationCopies.Single(c => c.SourceChatId == copy.FromChatId.Identifier
                    && c.MessageId == copy.ReplyParameters!.MessageId && c.UserId == User);
                Assert.Contains(copies.Take(before), original => original.FromChatId.Identifier == parent.SourceChatId
                    && original.MessageId == parent.SourceMessageId);
            }
        });

        async Task Groups(string status)
        {
            var response = await client.PostAsJsonAsync("/ingest/groups",
                new GroupsIngestRequest("kyiv", new() { ["45.1"] = new(status, ""), ["2.1"] = new(status, "") }), ApiJson.Options);
            response.EnsureSuccessStatusCode();
        }
    }

    [Fact]
    public async Task TextEditsRepliesAndDeletes_UseDurableSourceScopedIds()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        Link(f, Second);
        var mirror = f.CreateMirror();
        var original = Text(First, 10, "Initial");
        await mirror.SyncAsync(original, default);
        await mirror.SyncAsync(Text(Second, 10, "Other source"), default);
        await f.Db.SaveChangesAsync();
        var firstCopy = await f.Db.PrivateNotificationCopies.SingleAsync(c => c.SourceChatId == First && c.SourceMessageId == 10);
        var secondCopy = await f.Db.PrivateNotificationCopies.SingleAsync(c => c.SourceChatId == Second && c.SourceMessageId == 10);
        var firstId = firstCopy.MessageId;
        var secondId = secondCopy.MessageId;
        f.Db.ChangeTracker.Clear();
        mirror = f.CreateMirror();
        var changed = Text(First, 10, "Updated");
        changed.Entities = [new MessageEntity { Type = MessageEntityType.Bold, Offset = 0, Length = 7 }];
        await mirror.SyncAsync(changed, default);
        var edit = Assert.Single(f.Bot.Edits);
        Assert.Equal(firstId, edit.MessageId);
        Assert.Equal("Updated", edit.Text);
        Assert.Equal(MessageEntityType.Bold, Assert.Single(edit.Entities!).Type);
        Assert.DoesNotContain(f.Bot.Edits, e => e.MessageId == secondId);

        await mirror.SyncAsync(Text(First, 11, "Ended"), default, replyToMessageId: 10);
        Assert.Equal(firstId, f.Bot.Requests.OfType<CopyMessageRequest>().Last().ReplyParameters!.MessageId);
        await mirror.DeleteAsync(First, 10, default);
        await f.Db.SaveChangesAsync();
        Assert.Equal(firstId, Assert.Single(f.Bot.Deletes).MessageId);
        Assert.DoesNotContain(f.Db.PrivateNotificationCopies, c => c.SourceChatId == First && c.SourceMessageId == 10);
        Assert.Contains(f.Db.PrivateNotificationCopies, c => c.SourceChatId == Second && c.SourceMessageId == 10);
    }

    [Fact]
    public async Task FailedPrivateDeletion_IsRetriedAfterRestart()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var mirror = f.CreateMirror();
        await mirror.SyncAsync(Text(First, 10, "Alert"), default);
        await f.Db.SaveChangesAsync();
        var copyId = (await f.Db.PrivateNotificationCopies.SingleAsync(c => c.SourceMessageId == 10)).MessageId;

        f.Bot.Failure = request => request is DeleteMessageRequest delete && delete.ChatId.Identifier == User
            ? new HttpRequestException("private chat temporarily unavailable") : null;
        await mirror.DeleteAsync(First, 10, default);
        await f.Db.SaveChangesAsync();
        Assert.Equal(-copyId, (await f.Db.PrivateNotificationCopies.SingleAsync(c => c.SourceMessageId == 10)).MessageId);

        f.Db.ChangeTracker.Clear();
        f.Bot.Failure = null;
        await f.CreateMirror().RetryPendingAsync(default);
        await f.Db.SaveChangesAsync();

        Assert.DoesNotContain(f.Db.PrivateNotificationCopies, c => c.SourceMessageId == 10);
        Assert.Equal(2, f.Bot.Deletes.Count(d => d.ChatId.Identifier == User));
    }

    [Fact]
    public async Task MissingPrivateCopy_IsRecreatedWithoutAffectingOtherSources()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var mirror = f.CreateMirror();
        await mirror.SyncAsync(Text(First, 10, "Initial"), default);
        await f.Db.SaveChangesAsync();
        var oldId = (await f.Db.PrivateNotificationCopies.SingleAsync(c => c.SourceMessageId == 10)).MessageId;
        f.Bot.Failure = request => request is EditMessageTextRequest ? FakeTelegramBotClient.MessageToEditNotFound() : null;
        await mirror.SyncAsync(Text(First, 10, "Updated"), default);
        await f.Db.SaveChangesAsync();
        Assert.Equal(2, f.Bot.Requests.OfType<CopyMessageRequest>().Count());
        Assert.NotEqual(oldId, (await f.Db.PrivateNotificationCopies.SingleAsync(c => c.SourceMessageId == 10)).MessageId);
    }

    [Fact]
    public async Task FailedPrivateEdit_IsReplacedOnNextTickAfterRestart()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var mirror = f.CreateMirror();
        await mirror.SyncAsync(Text(First, 10, "Initial"), default);
        await f.Db.SaveChangesAsync();
        var oldId = (await f.Db.PrivateNotificationCopies.SingleAsync(c => c.SourceMessageId == 10)).MessageId;

        f.Bot.Failure = request => request is EditMessageTextRequest
            ? new HttpRequestException("private chat temporarily unavailable") : null;
        await mirror.SyncAsync(Text(First, 10, "Updated"), default);
        await f.Db.SaveChangesAsync();
        Assert.Contains(f.Db.PrivateNotificationCopies, c => c.SourceMessageId == -10);

        f.Db.ChangeTracker.Clear();
        f.Bot.Failure = null;
        await f.CreateMirror().RetryPendingAsync(default);
        await f.Db.SaveChangesAsync();

        Assert.Contains(f.Bot.Deletes, d => d.ChatId.Identifier == User && d.MessageId == oldId);
        Assert.DoesNotContain(f.Db.PrivateNotificationCopies, c => c.SourceMessageId == -10);
        Assert.True(Assert.Single(f.Db.PrivateNotificationCopies, c => c.SourceMessageId == 10).MessageId > oldId);
    }

    [Fact]
    public async Task PhotoUpdates_PreserveCaptionEntitiesAndPhoto()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var mirror = f.CreateMirror();
        var photo = new Message
        {
            Id = 20, Chat = new Chat { Id = First }, Caption = "Schedule",
            Photo = [new() { FileId = "original-file", FileUniqueId = "original", Width = 100, Height = 100 }],
        };
        await mirror.SyncAsync(photo, default);
        photo.Caption = "Changed";
        photo.CaptionEntities = [new() { Type = MessageEntityType.Italic, Offset = 0, Length = 7 }];
        photo.Photo[0].FileId = "changed-file";
        await mirror.SyncAsync(photo, default);
        var edit = Assert.Single(f.Bot.MediaEdits);
        Assert.Equal(User, edit.ChatId!.Identifier);
        Assert.Equal("Changed", edit.Media.Caption);
        Assert.Equal(MessageEntityType.Italic, Assert.Single(edit.Media.CaptionEntities!).Type);
        Assert.Equal("changed-file", Assert.IsType<InputFileId>(edit.Media.Media).Id);
    }

    [Theory]
    [InlineData(ChatMemberStatus.Member)]
    [InlineData(ChatMemberStatus.Left)]
    public async Task FormerAdmins_DoNotReceivePrivateCopies(ChatMemberStatus status)
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        f.Bot.SetStatus(User, status);
        await f.CreateMirror().SyncAsync(Text(First, 1, "Alert"), default);
        Assert.Empty(f.Bot.Requests.OfType<CopyMessageRequest>());
        Assert.Empty(f.Bot.SentTo(User));
    }

    [Fact]
    public async Task UnlinkingOneSource_RemovesOnlyItsMappingsAndStopsCopies()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        Link(f, Second);
        await f.CreateMirror().SyncAsync(Text(First, 1, "Alert"), default);
        await f.CreateMirror().SyncAsync(Text(Second, 1, "Alert"), default);
        await f.Db.SaveChangesAsync();
        var access = new ChatAccessService(f.Bot, f.Db, NullLogger<ChatAccessService>.Instance);
        await access.UnlinkAsync(First, User, f.Clock.Now, default);
        f.Db.ChangeTracker.Clear();
        Assert.DoesNotContain(f.Db.PrivateNotificationCopies, c => c.SourceChatId == First);
        Assert.Contains(f.Db.PrivateNotificationCopies, c => c.SourceChatId == Second);
        var before = f.Bot.Requests.Count;
        await f.CreateMirror().SyncAsync(Text(First, 2, "Later"), default);
        Assert.Equal(before, f.Bot.Requests.Count);
    }

    [Fact]
    public async Task MirrorFailure_DoesNotDeactivateSourceOrPreventOtherRecipients()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        f.Db.ChannelAdmins.Add(new() { ChatId = First, UserId = User + 1 });
        f.Bot.SetStatus(User + 1, ChatMemberStatus.Administrator);
        await f.Db.SaveChangesAsync();
        f.Bot.Failure = request => request is CopyMessageRequest copy && copy.ChatId.Identifier == User
            ? FakeTelegramBotClient.Forbidden() : null;
        var result = await f.Dispatcher.DispatchAsync(f.Kyiv,
            f.Classifier.Classify("kyiv", DispatcherFixture.EmergencyText), DispatcherFixture.EmergencyText, default);
        Assert.Equal(1, result.Sent);
        Assert.Equal(0, result.Failed);
        Assert.True((await f.Db.Channels.SingleAsync(c => c.ChatId == First)).IsActive);
        Assert.Contains(f.Db.PrivateNotificationCopies, c => c.UserId == User + 1 && c.SourceMessageId != 0);
        Assert.DoesNotContain(f.Db.PrivateNotificationCopies, c => c.UserId == User && c.SourceMessageId != 0);
    }

    [Fact]
    public async Task PrivateQuietHours_SilenceCopiesWithoutPausingSource()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        f.AddChannel(User, c =>
        {
            c.IsActive = false;
            c.QuietHoursEnabled = true;
            c.QuietHoursStartMinute = 0;
            c.QuietHoursEndMinute = 1439;
        });
        await f.CreateMirror().SyncAsync(Text(First, 1, "Alert"), default);
        Assert.True(Assert.Single(f.Bot.Requests.OfType<CopyMessageRequest>()).DisableNotification);
        Assert.True(Assert.Single(f.Bot.SentTo(User)).DisableNotification);
    }

    [Theory]
    [InlineData(PostMode.Edit)]
    [InlineData(PostMode.ReplaceDelete)]
    public async Task ScheduleTick_MirrorsMultiGroupPostsAndUpdates(PostMode mode)
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var channel = await f.Db.Channels.SingleAsync(c => c.ChatId == First);
        channel.GroupId = "45.1";
        channel.SchedulePost = true;
        channel.SchedulePostMode = mode;
        channel.SchedulePostImage = ScheduleImage.None;
        channel.Addresses = [new() { Name = "Home", RegionId = "kyiv",
            Groups = [new() { GroupId = "45.1" }, new() { GroupId = "2.1" }] }];
        await f.Db.SaveChangesAsync();
        var provider = new FakeScheduleProvider();
        provider.Snapshots["kyiv"] = RealSnapshot();
        var notifier = new ScheduleNotifier(f.Db, f.Bot, provider, f.Options, f.Clock,
            NullLogger<ScheduleNotifier>.Instance, f.CreateMirror());
        await notifier.TickAsync(default);
        var source = Assert.Single(f.Bot.SentTo(First));
        Assert.Contains("45.1", source.Text);
        Assert.Contains("2.1", source.Text);
        Assert.Single(f.Bot.Requests.OfType<CopyMessageRequest>());
        var initial = f.Bot.Requests.Count;
        await notifier.TickAsync(default);
        Assert.Equal(initial, f.Bot.Requests.Count);
        channel.TemplateMap = new Dictionary<string, string> { [MessageSlots.ScheduleHeading] = "Updated {header}" };
        await f.Db.SaveChangesAsync();
        await notifier.TickAsync(default);
        if (mode == PostMode.Edit)
        {
            Assert.Single(f.Bot.Edits, e => e.ChatId!.Identifier == User);
            Assert.Single(f.Bot.Requests.OfType<CopyMessageRequest>());
        }
        else
        {
            Assert.Equal(2, f.Bot.Requests.OfType<CopyMessageRequest>().Count());
            Assert.Contains(f.Bot.Deletes, d => d.ChatId.Identifier == User);
        }
    }

    [Fact]
    public async Task ScheduleReplacement_DeletesPrivateCopyWhenSourceWasAlreadyDeleted()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var channel = await f.Db.Channels.SingleAsync(c => c.ChatId == First);
        channel.GroupId = "45.1";
        channel.SchedulePost = true;
        channel.SchedulePostMode = PostMode.ReplaceDelete;
        channel.SchedulePostImage = ScheduleImage.None;
        await f.Db.SaveChangesAsync();
        var provider = new FakeScheduleProvider();
        provider.Snapshots["kyiv"] = RealSnapshot();
        var notifier = new ScheduleNotifier(f.Db, f.Bot, provider, f.Options, f.Clock,
            NullLogger<ScheduleNotifier>.Instance, f.CreateMirror());
        await notifier.TickAsync(default);
        var sourceId = (await f.Db.SchedulePostThreads.SingleAsync()).MessageId;
        var originalCopy = await f.Db.PrivateNotificationCopies.SingleAsync(c =>
            c.SourceChatId == First && c.SourceMessageId == sourceId && c.UserId == User);

        channel.TemplateMap = new Dictionary<string, string> { [MessageSlots.ScheduleHeading] = "Updated {header}" };
        await f.Db.SaveChangesAsync();
        f.Bot.Failure = request => request is DeleteMessageRequest delete
            && delete.ChatId.Identifier == First && delete.MessageId == sourceId
            ? new ApiRequestException("Bad Request: message to delete not found", 400) : null;
        await notifier.TickAsync(default);

        Assert.Contains(f.Bot.Deletes, d => d.ChatId.Identifier == User && d.MessageId == originalCopy.MessageId);
        Assert.DoesNotContain(f.Db.PrivateNotificationCopies,
            c => c.SourceChatId == First && c.SourceMessageId == sourceId);
    }

    [Fact]
    public async Task AlertReplacement_DeletesPrivateCopyWhenSourceWasAlreadyDeleted()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var channel = await f.Db.Channels.SingleAsync(c => c.ChatId == First);
        var alert = f.Classifier.Classify("kyiv", DispatcherFixture.EmergencyText);
        await f.Dispatcher.DispatchAsync(f.Kyiv, alert, DispatcherFixture.EmergencyText, default);
        var sourceId = f.Deliveries(First).Single().TelegramMessageId;
        var oldCopy = await f.Db.PrivateNotificationCopies.SingleAsync(c =>
            c.SourceChatId == First && c.SourceMessageId == sourceId && c.UserId == User);

        channel.DeletePreviousAlert = true;
        await f.Db.SaveChangesAsync();
        var changedText = DispatcherFixture.EmergencyText + " Повторне повідомлення.";
        var changedAlert = f.Classifier.Classify("kyiv", changedText);
        f.Bot.Failure = request => request is DeleteMessageRequest delete
            && delete.ChatId.Identifier == First && delete.MessageId == sourceId
            ? new ApiRequestException("Bad Request: message to delete not found", 400) : null;
        await f.Dispatcher.DispatchAsync(f.Kyiv, changedAlert, changedText, default);

        Assert.Contains(f.Bot.Deletes, d => d.ChatId.Identifier == User && d.MessageId == oldCopy.MessageId);
        Assert.DoesNotContain(f.Db.PrivateNotificationCopies,
            c => c.SourceChatId == First && c.SourceMessageId == sourceId);
    }

    [Fact]
    public async Task ScheduleReminders_AreCopiedOnceAndRespectSourceSilence()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var channel = await f.Db.Channels.SingleAsync(c => c.ChatId == First);
        channel.GroupId = "45.1";
        channel.ScheduleReminder = true;
        channel.QuietHoursEnabled = true;
        channel.QuietHoursStartMinute = 10 * 60;
        channel.QuietHoursEndMinute = 11 * 60;
        await f.Db.SaveChangesAsync();
        f.Clock.Now = Kyiv(15, 10, 50);
        var provider = new FakeScheduleProvider();
        provider.Snapshots["kyiv"] = RealSnapshot();
        var notifier = new ScheduleNotifier(f.Db, f.Bot, provider, f.Options, f.Clock,
            NullLogger<ScheduleNotifier>.Instance, f.CreateMirror());
        Assert.Equal(1, (await notifier.TickAsync(default)).Sent);
        Assert.True(Assert.Single(f.Bot.Requests.OfType<CopyMessageRequest>()).DisableNotification);
        var before = f.Bot.Requests.Count;
        f.Clock.Now = Kyiv(15, 10, 55);
        await notifier.TickAsync(default);
        Assert.Equal(before, f.Bot.Requests.Count);
    }

    [Fact]
    public async Task PhotoScheduleEvents_MirrorBothNewPostsAndPhaseEdits()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var channel = await f.Db.Channels.SingleAsync(c => c.ChatId == First);
        channel.GroupId = "45.1";
        channel.ScheduleNotify = true;
        channel.ScheduleImage = ScheduleImage.Grid;
        await f.Db.SaveChangesAsync();
        var provider = new FakeScheduleProvider();
        provider.Snapshots["kyiv"] = RealSnapshot();
        var notifier = new ScheduleNotifier(f.Db, f.Bot, provider, f.Options, f.Clock,
            NullLogger<ScheduleNotifier>.Instance, f.CreateMirror());
        Assert.Equal(1, (await notifier.TickAsync(default)).Sent);
        Assert.Single(f.Bot.Photos);
        Assert.Single(f.Bot.Requests.OfType<CopyMessageRequest>());
        f.Clock.Now = Kyiv(15, 14, 5);
        Assert.Equal(1, (await notifier.TickAsync(default)).Edited);
        Assert.Single(f.Bot.MediaEdits, e => e.ChatId!.Identifier == User);
        Assert.Single(f.Bot.Requests.OfType<CopyMessageRequest>());
    }

    [Fact]
    public async Task SourceSendFailure_DoesNotCreatePrivateCopies()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        f.Bot.Failure = request => request is SendMessageRequest send && send.ChatId.Identifier == First
            ? new HttpRequestException("source unavailable") : null;
        var result = await f.Dispatcher.DispatchAsync(f.Kyiv,
            f.Classifier.Classify("kyiv", DispatcherFixture.EmergencyText), DispatcherFixture.EmergencyText, default);
        Assert.Equal(1, result.Failed);
        Assert.Empty(f.Bot.Requests.OfType<CopyMessageRequest>());
        Assert.Empty(f.Bot.SentTo(User));
    }

    [Fact]
    public async Task RateLimitedCopy_RetriesWithoutRepeatingSourceLabel()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var calls = 0;
        f.Bot.Failure = request => request is CopyMessageRequest && calls++ == 0
            ? new ApiRequestException("Too Many Requests", 429, new ResponseParameters { RetryAfter = 0 })
            : null;
        await f.CreateMirror().SyncAsync(Text(First, 1, "Alert"), default);
        await f.Db.SaveChangesAsync();
        Assert.Equal(2, calls);
        Assert.Single(f.Bot.SentTo(User));
        Assert.Single(f.Db.PrivateNotificationCopies, c => c.SourceMessageId != 0);
    }

    [Fact]
    public async Task FailedPrivateCopy_IsRetriedOnIdenticalTickAfterRestart()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var copyAttempts = 0;
        f.Bot.Failure = request => request is CopyMessageRequest && copyAttempts++ == 0
            ? new HttpRequestException("private chat temporarily unavailable") : null;
        var classification = f.Classifier.Classify("kyiv", DispatcherFixture.EmergencyText);

        var first = await f.Dispatcher.DispatchAsync(
            f.Kyiv, classification, DispatcherFixture.EmergencyText, default);
        Assert.Equal(1, first.Sent);
        Assert.Single(f.Db.PrivateNotificationCopies, c => c.SourceMessageId == 0);
        Assert.Single(f.Db.PrivateNotificationCopies, c => c.SourceMessageId != 0 && c.MessageId == 0);

        f.Db.ChangeTracker.Clear();
        var restartedDispatcher = new AlertDispatcher(
            f.Bot, f.Db, f.Clock, NullLogger<AlertDispatcher>.Instance, f.CreateMirror());
        await restartedDispatcher.DispatchAsync(
            f.Kyiv, classification, DispatcherFixture.EmergencyText, default);

        Assert.Equal(2, copyAttempts);
        Assert.Single(f.Bot.SentTo(First));
        Assert.Equal(2, f.Bot.Requests.OfType<CopyMessageRequest>().Count());
        Assert.True(Assert.Single(f.Db.PrivateNotificationCopies,
            c => c.SourceMessageId != 0).MessageId > 0);
    }

    [Fact]
    public async Task FailedSourceLabel_IsRetriedOnIdenticalTickWithoutResendingSource()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        f.Bot.Failure = request => request is SendMessageRequest send && send.ChatId.Identifier == User
            ? new HttpRequestException("private chat temporarily unavailable") : null;
        var classification = f.Classifier.Classify("kyiv", DispatcherFixture.EmergencyText);

        await f.Dispatcher.DispatchAsync(f.Kyiv, classification, DispatcherFixture.EmergencyText, default);
        Assert.Single(f.Db.PrivateNotificationCopies,
            c => c.SourceMessageId != 0 && c.MessageId == 0);
        Assert.Empty(f.Db.PrivateNotificationCopies.Where(c => c.SourceMessageId == 0));

        f.Db.ChangeTracker.Clear();
        f.Bot.Failure = null;
        await new AlertDispatcher(f.Bot, f.Db, f.Clock, NullLogger<AlertDispatcher>.Instance, f.CreateMirror())
            .DispatchAsync(f.Kyiv, classification, DispatcherFixture.EmergencyText, default);

        Assert.Single(f.Bot.SentTo(First));
        Assert.Equal(2, f.Bot.SentTo(User).Count());
        Assert.Single(f.Bot.Requests.OfType<CopyMessageRequest>());
        Assert.True(Assert.Single(f.Db.PrivateNotificationCopies,
            c => c.SourceMessageId != 0).MessageId > 0);
    }

    [Fact]
    public async Task CallerCancellationDuringPrivateCopy_IsPropagated()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        using var cancellation = new CancellationTokenSource();
        f.Bot.Failure = request =>
        {
            if (request is SendMessageRequest send && send.ChatId.Identifier == User)
                cancellation.Cancel();
            return null;
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            f.CreateMirror().SyncAsync(Text(First, 10, "Alert"), cancellation.Token));

        f.Db.ChangeTracker.Clear();
        Assert.Empty(f.Db.PrivateNotificationCopies.AsNoTracking());
    }

    [Fact]
    public async Task TriggerAssessmentAndAddressResults_AreCopiedButExternalPostsAreNot()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var channel = await f.Db.Channels.SingleAsync(c => c.ChatId == First);
        channel.GroupId = "45.1";
        channel.OutageTrigger = true;
        channel.AddressStreet = "Test street";
        channel.AddressHouse = "1";
        await f.Db.SaveChangesAsync();
        var provider = new FakeScheduleProvider();
        provider.Snapshots["kyiv"] = RealSnapshot();
        var checks = new AddressCheckService(f.Db, f.Bot, new FakeWorkflowTrigger(), f.Options, f.Clock,
            NullLogger<AddressCheckService>.Instance, f.CreateMirror());
        var assessment = new OutageAssessmentService(f.Db, checks, provider, f.Options, f.Clock);
        var handler = new BotUpdateHandler(f.Bot, f.Db, assessment, new ConfigurationBuilder().Build(), f.Clock,
            NullLogger<BotUpdateHandler>.Instance, f.CreateMirror());
        await handler.HandleAsync(new Update { ChannelPost = new Message
        {
            Id = 50, Chat = new Chat { Id = First, Type = ChatType.Channel }, Text = "Світло зникло",
        } }, default);
        var copy = Assert.Single(f.Bot.Requests.OfType<CopyMessageRequest>());
        Assert.NotEqual(50, copy.MessageId);
        Assert.Single(f.Db.PrivateNotificationCopies, c => c.SourceMessageId != 0);

        f.Db.AddressChecks.Add(new AddressCheck
        {
            ChatId = First, RegionId = "kyiv", City = "", Street = "Test street", House = "1",
            Status = AddressCheckStatus.Pending, RequestedAt = f.Clock.Now,
        });
        await f.Db.SaveChangesAsync();
        await checks.ProcessResultsAsync(new AddressResultsRequest([
            new StreetResult("kyiv", "", "Test street", new()
            {
                ["1"] = new HouseResultEntry(true, "", "", "", ""),
            }),
        ]), default);
        Assert.Equal(2, f.Bot.Requests.OfType<CopyMessageRequest>().Count());
        Assert.Equal(2, f.Db.PrivateNotificationCopies.Count(c => c.SourceMessageId != 0));
    }

    [Fact]
    public async Task PreviewSend_DoesNotCreatePrivateCopies()
    {
        using var f = new DispatcherFixture();
        Link(f, First);
        var notifier = new ScheduleNotifier(f.Db, f.Bot, new FakeScheduleProvider(), f.Options, f.Clock,
            NullLogger<ScheduleNotifier>.Instance, f.CreateMirror());
        await notifier.SendNewAsync(First, "Preview", null, default);
        Assert.Empty(f.Bot.Requests.OfType<CopyMessageRequest>());
        Assert.Empty(f.Bot.SentTo(User));
    }

    private static void Link(DispatcherFixture f, long chatId)
    {
        f.AddChannel(chatId, c => c.Title = $"Source {chatId}");
        f.Db.ChannelAdmins.Add(new() { ChatId = chatId, UserId = User });
        f.Db.SaveChanges();
        f.Bot.SetStatus(User, ChatMemberStatus.Administrator);
    }

    private static Message Text(long chatId, int id, string text) =>
        new() { Id = id, Chat = new Chat { Id = chatId }, Text = text };
}
