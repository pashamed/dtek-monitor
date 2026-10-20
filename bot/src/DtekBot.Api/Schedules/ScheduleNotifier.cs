using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DtekBot.Api.Schedules;

public sealed record ScheduleTickSummary(int Chats, int Sent, int Edited, int Skipped, int Failed);
public sealed record SchedulePostNowResult(bool Sent, bool Failed, string Message);

// Keeps two kinds of stabilization messages in step with the chat's group schedule:
//  - the "light off / on" event messages (one thread per outage episode), and
//  - the schedule post (the day's outages, usually with a picture).
// Each follows the chat's own mode: edit the same message, post a new one, or post a new one and delete the old.
// Called on a timer from outside (the workflow), so a tick only compares the stored state with the schedule
// and never assumes it ran on time.
public sealed partial class ScheduleNotifier(
    BotDbContext db,
    ITelegramBotClient bot,
    IScheduleProvider schedules,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time,
    ILogger<ScheduleNotifier> logger)
{
    private const int MaxRetryAfterSeconds = 10;
    private const int MaxCaptionLength = 1024;
    private static readonly TimeSpan SendSpacing = TimeSpan.FromMilliseconds(50);

    private enum Outcome
    {
        Skipped,
        Sent,
        Edited,
        Failed,
    }

    private sealed record Desired(SchedulePhase Phase, long EpisodeStart, string Text, string ImageVersion, string? ImageUrl, ScheduleImage ImageKind);

    public async Task<ScheduleTickSummary> TickAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var now = time.GetUtcNow();

        var channels = await db.Channels
            .Where(c => c.IsActive && !c.IsPaused && c.GroupId != null
                && ((settings.Schedules.EnableEventNotifications && c.ScheduleNotify)
                    || c.SchedulePost || c.ScheduleReminder))
            .ToListAsync(ct);
        var threads = await db.ScheduleThreads.ToDictionaryAsync(t => t.ChatId, ct);
        var posts = await db.SchedulePostThreads.ToDictionaryAsync(t => t.ChatId, ct);
        var cutoff = now.AddDays(-7);
        var reminders = await db.ScheduleReminderDeliveries
            .Where(d => d.OutageStart >= cutoff)
            .ToListAsync(ct);
        var deliveredReminders = reminders
            .Select(r => (r.ChatId, r.RegionId, r.GroupId, r.OutageStart))
            .ToHashSet();
        db.ScheduleReminderDeliveries.RemoveRange(
            await db.ScheduleReminderDeliveries.Where(d => d.OutageStart < cutoff).ToListAsync(ct));

        // A thread whose chat turned the feature off or changed group starts fresh next time.
        var byChat = channels.ToDictionary(c => c.ChatId);
        foreach (var thread in threads.Values.ToList())
        {
            if (!byChat.TryGetValue(thread.ChatId, out var channel)
                || !settings.Schedules.EnableEventNotifications || !channel.ScheduleNotify
                || channel.GroupId != thread.GroupId || channel.RegionId != thread.RegionId)
            {
                db.ScheduleThreads.Remove(thread);
                threads.Remove(thread.ChatId);
            }
        }

        foreach (var post in posts.Values.ToList())
        {
            if (!byChat.TryGetValue(post.ChatId, out var channel) || !channel.SchedulePost
                || (post.MessageIds is null && channel.GroupId != post.GroupId) || channel.RegionId != post.RegionId)
            {
                db.SchedulePostThreads.Remove(post);
                posts.Remove(post.ChatId);
            }
        }

        int sent = 0, edited = 0, skipped = 0, failed = 0;
        void Count(Outcome outcome)
        {
            switch (outcome)
            {
                case Outcome.Sent: sent++; break;
                case Outcome.Edited: edited++; break;
                case Outcome.Failed: failed++; break;
                default: skipped++; break;
            }
        }

        foreach (var regionGroup in channels.GroupBy(c => c.RegionId))
        {
            var region = settings.Regions.FirstOrDefault(r => r.Id == regionGroup.Key);
            var snapshot = region is { HasSchedules: true } ? await schedules.GetAsync(region.Id, ct) : null;
            if (region is null || snapshot is null)
            {
                skipped += regionGroup.Count();
                continue;
            }

            foreach (var channel in regionGroup)
            {
                if (channel.ScheduleReminder)
                {
                    var outcome = await SendDueReminderAsync(
                        channel, region, snapshot, deliveredReminders, now, ct,
                        disableNotification: QuietHours.IsQuiet(channel, now));
                    Count(outcome);
                    if (outcome is Outcome.Sent)
                    {
                        await Task.Delay(SendSpacing, ct);
                    }
                }

                if (settings.Schedules.EnableEventNotifications && channel.ScheduleNotify)
                {
                    threads.TryGetValue(channel.ChatId, out var thread);
                    var desired = Decide(settings, region, snapshot, channel, thread, now);
                    var outcome = desired is null
                        ? Outcome.Skipped
                        : await ApplyAsync(
                            channel, region, thread, desired, threads, now, ct,
                            disableNotification: QuietHours.IsQuiet(channel, now));
                    Count(outcome);
                    if (outcome is Outcome.Sent or Outcome.Edited)
                    {
                        await Task.Delay(SendSpacing, ct);
                    }
                }

                if (channel.SchedulePost && channel.IsActive)
                {
                    posts.TryGetValue(channel.ChatId, out var post);
                    var outcome = await ApplyPostAsync(
                        settings, channel, region, snapshot, post, posts, now, ct,
                        disableNotification: QuietHours.IsQuiet(channel, now));
                    Count(outcome);
                    if (outcome is Outcome.Sent or Outcome.Edited)
                    {
                        await Task.Delay(SendSpacing, ct);
                    }
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return new ScheduleTickSummary(channels.Count, sent, edited, skipped, failed);
    }

    public async Task<SchedulePostNowResult> SendScheduleNowAsync(Channel channel, CancellationToken ct)
    {
        var settings = options.CurrentValue;
        var region = settings.Regions.FirstOrDefault(r => r.Id == channel.RegionId && r.HasSchedules);
        if (region is null || string.IsNullOrWhiteSpace(channel.GroupId))
        {
            return new(false, false, "Спершу оберіть регіон із графіками та чергу.");
        }

        var snapshot = await schedules.GetAsync(region.Id, ct);
        if (snapshot is null)
        {
            return new(false, false, "Графіки тимчасово недоступні. Спробуйте пізніше.");
        }

        if (!channel.GroupIds().Any(id => snapshot.Find(id) is not null))
        {
            return new(false, false, "Для обраної черги графік недоступний.");
        }

        var post = await db.SchedulePostThreads.FirstOrDefaultAsync(t => t.ChatId == channel.ChatId, ct);
        var posts = post is null
            ? new Dictionary<long, SchedulePostThread>()
            : new Dictionary<long, SchedulePostThread> { [channel.ChatId] = post };
        var outcome = await ApplyPostAsync(
            settings, channel, region, snapshot, post, posts, time.GetUtcNow(), ct, force: true);
        await db.SaveChangesAsync(ct);
        return outcome switch
        {
            Outcome.Sent => new(true, false, "Графік надіслано в канал."),
            Outcome.Edited => new(true, false, "Графік у каналі оновлено."),
            _ => new(false, true, "Не вдалося надіслати графік. Перевірте, що бот має право публікувати повідомлення в каналі."),
        };
    }

    private async Task<Outcome> SendDueReminderAsync(
        Channel channel,
        RegionSettings region,
        ScheduleSnapshot snapshot,
        HashSet<(long ChatId, string RegionId, string GroupId, DateTimeOffset OutageStart)> delivered,
        DateTimeOffset now,
        CancellationToken ct,
        bool disableNotification)
    {
        var groupIds = channel.GroupIds();
        var lead = TimeSpan.FromMinutes(channel.ScheduleReminderMinutes);
        foreach (var startGroup in groupIds.SelectMany(id => snapshot.Find(id)?.Outages ?? [])
            .GroupBy(o => o.Start).OrderBy(g => g.Key))
        {
            var outage = startGroup.First();
            if (outage.Start <= now || now < outage.Start - lead)
            {
                continue;
            }

            var key = (channel.ChatId, region.Id, channel.GroupId!, outage.Start);
            if (delivered.Any(d => d.ChatId == channel.ChatId && d.RegionId == region.Id && d.OutageStart == outage.Start))
            {
                continue;
            }

            var affected = groupIds.Where(id => snapshot.Find(id)?.Outages.Any(o => o.Start == outage.Start) == true).ToArray();
            var values = ScheduleText.BeforeOffValues(region.Name, string.Join(", ", affected), outage.Start, channel.ScheduleReminderMinutes);
            var text = MessageTemplate.Render(
                channel.TemplateFor(MessageSlots.BeforeOff),
                values,
                MessageSlots.Find(MessageSlots.BeforeOff)!.DefaultTemplate);
            if (channel.IsCombined())
                text += "\n" + string.Join("; ", affected.Select(id => System.Net.WebUtility.HtmlEncode(channel.GroupNames(id)) + " ? " + id));
            try
            {
                await WithRetry(() => WithMarkupFallback(
                    text,
                    (t, mode) => bot.SendMessage(
                        channel.ChatId, t, parseMode: mode, disableNotification: disableNotification, cancellationToken: ct)), ct);
                db.ScheduleReminderDeliveries.Add(new ScheduleReminderDelivery
                {
                    ChatId = channel.ChatId,
                    RegionId = region.Id,
                    GroupId = channel.GroupId!,
                    OutageStart = outage.Start,
                    SentAt = now,
                });
                delivered.Add(key);
                return Outcome.Sent;
            }
            catch (ApiRequestException ex) when (IsChatGone(ex))
            {
                Deactivate(channel, ex);
                return Outcome.Failed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Schedule reminder for chat {ChatId} failed", channel.ChatId);
                return Outcome.Failed;
            }
        }

        return Outcome.Skipped;
    }

    private static Desired? Decide(
        DtekSettings settings,
        RegionSettings region,
        ScheduleSnapshot snapshot,
        Channel channel,
        ScheduleThread? thread,
        DateTimeOffset now)
    {
        var groupId = channel.GroupId!;
        var status = snapshot.StatusAt(groupId, now);
        if (status is null)
        {
            return null;
        }

        var imageUrl = ScheduleText.ImageUrl(settings.Schedules, snapshot.DataFile, groupId, snapshot.Version, channel.ScheduleImage);
        if (status.IsOff)
        {
            // An outage that began before today's data (past midnight) keeps the episode it was announced under.
            var episode = status.Since?.ToUnixTimeSeconds()
                ?? (thread is { Phase: SchedulePhase.Off } ? thread.EpisodeStart : snapshot.CoverageStart.ToUnixTimeSeconds());

            // Without a known start the stable episode marker is shown, so the text does not change on every tick.
            var shown = status.Since is null ? status with { Since = DateTimeOffset.FromUnixTimeSeconds(episode) } : status;
            return new Desired(
                SchedulePhase.Off,
                episode,
                ScheduleText.OffMessage(region.Name, groupId, shown, now, channel.TemplateFor(MessageSlots.Off)),
                snapshot.Version,
                imageUrl,
                channel.ScheduleImage);
        }

        // Power is on. Only an outage the chat was told about gets a "light is back" message.
        if (thread is not { Phase: SchedulePhase.Off or SchedulePhase.On } || thread.EpisodeStart <= 0)
        {
            return null;
        }

        return new Desired(
            SchedulePhase.On,
            thread.EpisodeStart,
            ScheduleText.OnMessage(
                region.Name,
                groupId,
                status,
                DateTimeOffset.FromUnixTimeSeconds(thread.EpisodeStart),
                now,
                channel.TemplateFor(MessageSlots.On)),
            snapshot.Version,
            imageUrl,
            channel.ScheduleImage);
    }

    private async Task<Outcome> ApplyAsync(
        Channel channel,
        RegionSettings region,
        ScheduleThread? thread,
        Desired desired,
        Dictionary<long, ScheduleThread> threads,
        DateTimeOffset now,
        CancellationToken ct,
        bool disableNotification)
    {
        var hash = Hash(desired.Text);
        var mode = channel.ScheduleEventMode;

        try
        {
            // New episode, first message for the chat, a phase change outside "edit" mode, or (in "edit" mode)
            // a switch between text-only and picture messages, which a message cannot do by editing.
            var newEpisode = thread is null || (desired.Phase == SchedulePhase.Off && thread.EpisodeStart != desired.EpisodeStart);
            var phaseChanged = thread is not null && thread.Phase != desired.Phase;
            var pictureModeFlipped = mode == PostMode.Edit && thread is not null
                && (thread.ImageKind == ScheduleImage.None) != (desired.ImageKind == ScheduleImage.None);
            if (newEpisode || pictureModeFlipped || (phaseChanged && mode != PostMode.Edit))
            {
                var (messageId, isPhoto) = await SendNewAsync(
                    channel.ChatId, desired.Text, desired.ImageUrl, ct, disableNotification);
                if (thread is not null && (mode == PostMode.ReplaceDelete || pictureModeFlipped))
                {
                    await DeleteQuietlyAsync(channel.ChatId, thread.MessageId, ct);
                }

                Save(channel, region, thread, desired, hash, messageId, isPhoto, threads, now);
                return Outcome.Sent;
            }

            // Same episode: only "edit" mode touches the message, and only when something visible changed.
            var pictureChanged = thread!.ImageVersion != desired.ImageVersion || thread.ImageKind != desired.ImageKind;
            if (mode != PostMode.Edit || (!phaseChanged && thread.ContentHash == hash && !pictureChanged))
            {
                return Outcome.Skipped;
            }

            if (!await EditAsync(channel.ChatId, thread.MessageId, thread.IsPhoto, thread.ImageVersion, thread.ImageKind, desired, ct))
            {
                // The message is gone (deleted by an admin): post a fresh one.
                var (messageId, isPhoto) = await SendNewAsync(
                    channel.ChatId, desired.Text, desired.ImageUrl, ct, disableNotification);
                Save(channel, region, thread, desired, hash, messageId, isPhoto, threads, now);
                return Outcome.Sent;
            }

            Save(channel, region, thread, desired, hash, thread.MessageId, thread.IsPhoto, threads, now);
            return Outcome.Edited;
        }
        catch (ApiRequestException ex) when (IsChatGone(ex))
        {
            Deactivate(channel, ex);
            return Outcome.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Schedule message for chat {ChatId} failed", channel.ChatId);
            return Outcome.Failed;
        }
    }

    private void Save(
        Channel channel,
        RegionSettings region,
        ScheduleThread? thread,
        Desired desired,
        string hash,
        int messageId,
        bool isPhoto,
        Dictionary<long, ScheduleThread> threads,
        DateTimeOffset now)
    {
        if (thread is null)
        {
            thread = new ScheduleThread { ChatId = channel.ChatId };
            db.ScheduleThreads.Add(thread);
            threads[channel.ChatId] = thread;
        }

        thread.RegionId = region.Id;
        thread.GroupId = channel.GroupId!;
        thread.MessageId = messageId;
        thread.IsPhoto = isPhoto;
        thread.Phase = desired.Phase;
        thread.EpisodeStart = desired.EpisodeStart;
        thread.ContentHash = hash;
        thread.ImageVersion = desired.ImageVersion;
        thread.ImageKind = desired.ImageKind;
        thread.UpdatedAt = now;
    }

    private async Task<Outcome> ApplyPostAsync(
        DtekSettings settings,
        Channel channel,
        RegionSettings region,
        ScheduleSnapshot snapshot,
        SchedulePostThread? post,
        Dictionary<long, SchedulePostThread> posts,
        DateTimeOffset now,
        CancellationToken ct,
        bool force = false,
        bool disableNotification = false)
    {
        if (channel.IsCombined())
            return await ApplyCombinedPostAsync(channel, region, snapshot, post, posts, now, ct, force, disableNotification);
        if (post?.MessageIds is not null)
        {
            if (channel.SchedulePostMode != PostMode.New)
                foreach (var id in (System.Text.Json.JsonSerializer.Deserialize<List<int>>(post.MessageIds) ?? []).Skip(1))
                    await DeleteQuietlyAsync(channel.ChatId, id, ct);
            post.MessageIds = null;
        }
        var groupId = channel.GroupId!;
        if (snapshot.Find(groupId) is null || now < snapshot.CoverageStart || now >= snapshot.CoverageEnd)
        {
            return Outcome.Skipped;
        }

        var dateKey = ScheduleText.DateKey(now);
        var sameDay = post is not null && post.DateKey == dateKey;
        var values = ScheduleText.PostValues(region.Name, groupId, snapshot, now, updated: sameDay);
        var fingerprint = Hash(ScheduleText.PostFingerprint(values) + "|" + channel.SchedulePostImage);

        if (!force && (sameDay ? post!.ContentHash == fingerprint : values["outages"] == ScheduleText.NoOutages))
        {
            return Outcome.Skipped;
        }

        var text = MessageTemplate.Render(
            channel.TemplateFor(MessageSlots.Schedule),
            values,
            MessageSlots.Find(MessageSlots.Schedule)!.DefaultTemplate);
        var imageUrl = ScheduleText.ImageUrl(settings.Schedules, snapshot.DataFile, groupId, snapshot.Version, channel.SchedulePostImage);
        var mode = channel.SchedulePostMode;

        try
        {
            var desired = new Desired(SchedulePhase.Off, 0, text, snapshot.Version, imageUrl, channel.SchedulePostImage);
            var flipped = post is not null && (post.ImageKind == ScheduleImage.None) != (channel.SchedulePostImage == ScheduleImage.None);
            var outcome = Outcome.Sent;
            int messageId;
            bool isPhoto;

            if (sameDay && mode == PostMode.Edit && !flipped)
            {
                if (await EditAsync(post!.ChatId, post.MessageId, post.IsPhoto, post.ImageVersion, post.ImageKind, desired, ct))
                {
                    messageId = post.MessageId;
                    isPhoto = post.IsPhoto;
                    outcome = Outcome.Edited;
                }
                else
                {
                    (messageId, isPhoto) = await SendNewAsync(
                        channel.ChatId, text, imageUrl, ct, disableNotification);
                }
            }
            else
            {
                (messageId, isPhoto) = await SendNewAsync(
                    channel.ChatId, text, imageUrl, ct, disableNotification);
                if (post is not null && (mode == PostMode.ReplaceDelete || (mode == PostMode.Edit && sameDay && flipped)))
                {
                    await DeleteQuietlyAsync(channel.ChatId, post.MessageId, ct);
                }
            }

            if (post is null)
            {
                post = new SchedulePostThread { ChatId = channel.ChatId };
                db.SchedulePostThreads.Add(post);
                posts[channel.ChatId] = post;
            }

            post.RegionId = region.Id;
            post.GroupId = groupId;
            post.MessageId = messageId;
            post.IsPhoto = isPhoto;
            post.DateKey = dateKey;
            post.ContentHash = fingerprint;
            post.ImageVersion = snapshot.Version;
            post.ImageKind = channel.SchedulePostImage;
            post.UpdatedAt = now;
            return outcome;
        }
        catch (ApiRequestException ex) when (IsChatGone(ex))
        {
            Deactivate(channel, ex);
            return Outcome.Failed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Schedule post for chat {ChatId} failed", channel.ChatId);
            return Outcome.Failed;
        }
    }

    public async Task<string?> PreviewCombinedAsync(Channel channel, CancellationToken ct, string? template = null)
    {
        var region = options.CurrentValue.Regions.FirstOrDefault(r => r.Id == channel.RegionId && r.HasSchedules);
        if (region is null) return null;
        var snapshot = await schedules.GetAsync(region.Id, ct);
        if (snapshot is null) return null;
        var target = channel;
        if (template is not null)
        {
            target = new Channel { RegionId = channel.RegionId, GroupId = channel.GroupId, Addresses = channel.Addresses };
            target.TemplateMap = new Dictionary<string, string> { [MessageSlots.Schedule] = template };
        }
        return CombinedSchedule.Render(target, snapshot, region.Name, time.GetUtcNow(), false);
    }

    private async Task<Outcome> ApplyCombinedPostAsync(Channel channel, RegionSettings region,
        ScheduleSnapshot snapshot, SchedulePostThread? post, Dictionary<long, SchedulePostThread> posts,
        DateTimeOffset now, CancellationToken ct, bool force, bool disableNotification)
    {
        if (now < snapshot.CoverageStart || now >= snapshot.CoverageEnd) return Outcome.Skipped;
        var date = ScheduleText.DateKey(now);
        var sameDay = post?.DateKey == date;
        var stable = CombinedSchedule.Render(channel, snapshot, region.Name, now, false);
        var fingerprint = Hash(stable);
        if (!force && sameDay && post!.ContentHash == fingerprint) return Outcome.Skipped;
        var text = CombinedSchedule.Render(channel, snapshot, region.Name, now, sameDay);
        var parts = CombinedSchedule.Parts(text);
        var oldIds = post is null ? [] : post.MessageIds is null ? new List<int> { post.MessageId }
            : System.Text.Json.JsonSerializer.Deserialize<List<int>>(post.MessageIds) ?? [];
        var ids = new List<int>();
        var edit = sameDay && channel.SchedulePostMode == PostMode.Edit && post is { IsPhoto: false };
        try
        {
            for (var i = 0; i < parts.Count; i++)
            {
                var desired = new Desired(SchedulePhase.Off, 0, parts[i], "", null, ScheduleImage.None);
                if (edit && i < oldIds.Count && await EditAsync(channel.ChatId, oldIds[i], false, "", ScheduleImage.None, desired, ct))
                    ids.Add(oldIds[i]);
                else
                    ids.Add((await SendNewAsync(channel.ChatId, parts[i], null, ct, disableNotification)).MessageId);
            }
            if (channel.SchedulePostMode == PostMode.ReplaceDelete || (sameDay && channel.SchedulePostMode == PostMode.Edit))
                foreach (var id in oldIds.Except(ids)) await DeleteQuietlyAsync(channel.ChatId, id, ct);
            if (post is null)
            {
                post = new SchedulePostThread { ChatId = channel.ChatId };
                db.SchedulePostThreads.Add(post); posts[channel.ChatId] = post;
            }
            post.MessageId = ids[0]; post.MessageIds = System.Text.Json.JsonSerializer.Serialize(ids);
            post.IsPhoto = false; post.RegionId = region.Id; post.GroupId = channel.GroupId!;
            post.DateKey = date; post.ContentHash = fingerprint; post.ImageKind = ScheduleImage.None;
            post.ImageVersion = ""; post.UpdatedAt = now;
            return edit ? Outcome.Edited : Outcome.Sent;
        }
        catch (ApiRequestException ex) when (IsChatGone(ex)) { Deactivate(channel, ex); return Outcome.Failed; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Remove only newly sent fragments on a partial failure; retries can safely rebuild the digest.
            foreach (var id in ids.Except(oldIds)) await DeleteQuietlyAsync(channel.ChatId, id, ct);
            logger.LogWarning(ex, "Combined schedule for chat {ChatId} failed", channel.ChatId);
            return Outcome.Failed;
        }
    }

    private void Deactivate(Channel channel, ApiRequestException ex)
    {
        logger.LogWarning("Deactivating chat {ChatId} after schedule send: {Error}", channel.ChatId, ex.Message);
        channel.IsActive = false;
        foreach (var thread in db.ScheduleThreads.Local.Where(t => t.ChatId == channel.ChatId).ToList())
        {
            db.ScheduleThreads.Remove(thread);
        }

        foreach (var post in db.SchedulePostThreads.Local.Where(t => t.ChatId == channel.ChatId).ToList())
        {
            db.SchedulePostThreads.Remove(post);
        }
    }

    // Used by the test post as well: sends one schedule-style message (picture first, text-only as a fallback).
    // The picture is nice to have: if Telegram cannot load it, the text alone still goes out.
    public async Task<(int MessageId, bool IsPhoto)> SendNewAsync(
        long chatId,
        string text,
        string? imageUrl,
        CancellationToken ct,
        bool disableNotification = false)
    {
        if (imageUrl is not null && text.Length <= MaxCaptionLength)
        {
            try
            {
                var message = await WithRetry(() => WithMarkupFallback(
                    text,
                    (t, mode) => bot.SendPhoto(
                        chatId, InputFile.FromString(imageUrl), caption: t, parseMode: mode,
                        disableNotification: disableNotification, cancellationToken: ct)), ct);
                return (message.Id, true);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && !IsChatProblem(ex))
            {
                logger.LogInformation("Schedule picture rejected for chat {ChatId}, sending text only: {Error}", chatId, ex.Message);
            }
        }

        var sent = await WithRetry(() => WithMarkupFallback(
            text,
            (t, mode) => bot.SendMessage(
                chatId, t, parseMode: mode, linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                disableNotification: disableNotification, cancellationToken: ct)), ct);
        return (sent.Id, false);
    }

    // False when the message no longer exists and a new one must be posted.
    private async Task<bool> EditAsync(
        long chatId,
        int messageId,
        bool isPhoto,
        string oldVersion,
        ScheduleImage oldKind,
        Desired desired,
        CancellationToken ct)
    {
        try
        {
            if (!isPhoto || desired.ImageUrl is null)
            {
                await WithRetry(() => WithMarkupFallback(
                    desired.Text,
                    (t, mode) => bot.EditMessageText(chatId, messageId, t, parseMode: mode, cancellationToken: ct)), ct);
                return true;
            }

            if (oldVersion != desired.ImageVersion || oldKind != desired.ImageKind)
            {
                try
                {
                    await WithRetry(() => WithMarkupFallback(
                        desired.Text,
                        (t, mode) => bot.EditMessageMedia(
                            chatId,
                            messageId,
                            new InputMediaPhoto(InputFile.FromString(desired.ImageUrl)) { Caption = t, ParseMode = mode },
                            cancellationToken: ct)), ct);
                    return true;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400 && !IsGone(ex) && !IsNotModified(ex))
                {
                    logger.LogInformation("Schedule picture update rejected for chat {ChatId}, updating the caption only: {Error}", chatId, ex.Message);
                }
            }

            await WithRetry(() => WithMarkupFallback(
                desired.Text,
                (t, mode) => bot.EditMessageCaption(chatId, messageId, t, parseMode: mode, cancellationToken: ct)), ct);
            return true;
        }
        catch (ApiRequestException ex) when (IsNotModified(ex))
        {
            return true;
        }
        catch (ApiRequestException ex) when (IsGone(ex))
        {
            return false;
        }
    }

    // Needs the "delete messages" admin right; when it is missing the old message simply stays.
    private async Task DeleteQuietlyAsync(long chatId, int messageId, CancellationToken ct)
    {
        try
        {
            await bot.DeleteMessage(chatId, messageId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("Could not delete message {MessageId} in chat {ChatId}: {Error}", messageId, chatId, ex.Message);
        }
    }

    // A saved template is validated, but if Telegram still rejects its markup the message must go out.
    private static async Task<T> WithMarkupFallback<T>(string text, Func<string, ParseMode, Task<T>> call)
    {
        try
        {
            return await call(text, ParseMode.Html);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("parse entities", StringComparison.OrdinalIgnoreCase))
        {
            return await call(WebUtility.HtmlDecode(TagPattern().Replace(text, "")), ParseMode.None);
        }
    }

    private static async Task<T> WithRetry<T>(Func<Task<T>> action, CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(ex.Parameters?.RetryAfter ?? 1, MaxRetryAfterSeconds)), ct);
            return await action();
        }
    }

    private static bool IsChatGone(ApiRequestException ex) =>
        ex.ErrorCode == 403 || (ex.ErrorCode == 400 && IsChatProblem(ex));

    private static bool IsNotModified(ApiRequestException ex) =>
        ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);

    private static bool IsGone(ApiRequestException ex) =>
        ex.ErrorCode == 400
        && (ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("message can't be edited", StringComparison.OrdinalIgnoreCase));

    private static bool IsChatProblem(ApiRequestException ex) =>
        ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase);

    private static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex TagPattern();
}
