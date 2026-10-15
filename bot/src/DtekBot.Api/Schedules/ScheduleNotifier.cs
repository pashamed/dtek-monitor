using System.Security.Cryptography;
using System.Text;
using DtekBot.Api.Data;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace DtekBot.Api.Schedules;

public sealed record ScheduleTickSummary(int Chats, int Sent, int Edited, int Skipped, int Failed);

// Keeps one schedule message per chat in step with the group's schedule: it is posted when an outage starts and
// edited as the expected return time changes and when the power comes back. Called on a timer from outside
// (the workflow), so a tick only compares the chat's stored state with the schedule and never assumes it ran on time.
public sealed class ScheduleNotifier(
    BotDbContext db,
    ITelegramBotClient bot,
    IScheduleProvider schedules,
    IOptionsMonitor<DtekSettings> options,
    TimeProvider time,
    ILogger<ScheduleNotifier> logger)
{
    private const int MaxRetryAfterSeconds = 10;
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
            .Where(c => c.IsActive && !c.IsPaused && c.ScheduleNotify && c.GroupId != null)
            .ToListAsync(ct);
        var threads = await db.ScheduleThreads.ToDictionaryAsync(t => t.ChatId, ct);

        // A thread whose chat turned the feature off or changed group starts fresh next time.
        var eligible = channels.ToDictionary(c => c.ChatId);
        foreach (var thread in threads.Values.ToList())
        {
            if (!eligible.TryGetValue(thread.ChatId, out var channel)
                || channel.GroupId != thread.GroupId || channel.RegionId != thread.RegionId)
            {
                db.ScheduleThreads.Remove(thread);
                threads.Remove(thread.ChatId);
            }
        }

        int sent = 0, edited = 0, skipped = 0, failed = 0;
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
                threads.TryGetValue(channel.ChatId, out var thread);
                var desired = Decide(settings, region, snapshot, channel, thread, now);
                if (desired is null)
                {
                    skipped++;
                    continue;
                }

                var outcome = await ApplyAsync(channel, region, thread, desired, threads, now, ct);
                switch (outcome)
                {
                    case Outcome.Sent: sent++; break;
                    case Outcome.Edited: edited++; break;
                    case Outcome.Failed: failed++; break;
                    default: skipped++; break;
                }

                if (outcome is Outcome.Sent or Outcome.Edited)
                {
                    await Task.Delay(SendSpacing, ct);
                }
            }
        }

        await db.SaveChangesAsync(ct);
        return new ScheduleTickSummary(channels.Count, sent, edited, skipped, failed);
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
            return new Desired(
                SchedulePhase.Off,
                episode,
                ScheduleText.OffMessage(region.Name, groupId, status, now),
                snapshot.Version,
                imageUrl,
                channel.ScheduleImage);
        }

        // Power is on. If the chat still shows an outage, this message is its happy ending.
        DateTimeOffset? outageStart = thread is { Phase: SchedulePhase.Off or SchedulePhase.On } && thread.EpisodeStart > 0
            ? DateTimeOffset.FromUnixTimeSeconds(thread.EpisodeStart)
            : null;
        return new Desired(
            SchedulePhase.On,
            thread?.EpisodeStart ?? 0,
            ScheduleText.OnMessage(region.Name, groupId, status, outageStart, now),
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
        CancellationToken ct)
    {
        var hash = Hash(desired.Text);

        try
        {
            // New episode, first message for the chat, "new message" mode at a phase change, or a switch between
            // text-only and picture messages (a text message cannot become a photo or back).
            var newEpisode = thread is null || (desired.Phase == SchedulePhase.Off && thread.EpisodeStart != desired.EpisodeStart);
            var phaseChanged = thread is not null && thread.Phase != desired.Phase;
            var pictureModeFlipped = thread is not null && (thread.ImageKind == ScheduleImage.None) != (desired.ImageKind == ScheduleImage.None);
            if (newEpisode || pictureModeFlipped || (phaseChanged && !channel.EditInPlace))
            {
                var (messageId, isPhoto) = await SendNewAsync(channel, desired, ct);
                Save(channel, region, thread, desired, hash, messageId, isPhoto, threads, now);
                return Outcome.Sent;
            }

            // Same episode and phase: only touch the message when something visible changed, and only if editing is allowed.
            var pictureChanged = thread!.ImageVersion != desired.ImageVersion || thread.ImageKind != desired.ImageKind;
            if (!channel.EditInPlace || (!phaseChanged && thread.ContentHash == hash && !pictureChanged))
            {
                return Outcome.Skipped;
            }

            if (!await EditAsync(channel, thread!, desired, ct))
            {
                // The message is gone (deleted by an admin): post a fresh one.
                var (messageId, isPhoto) = await SendNewAsync(channel, desired, ct);
                Save(channel, region, thread, desired, hash, messageId, isPhoto, threads, now);
                return Outcome.Sent;
            }

            Save(channel, region, thread, desired, hash, thread!.MessageId, thread.IsPhoto, threads, now);
            return Outcome.Edited;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 403
            || (ex.ErrorCode == 400 && ex.Message.Contains("chat not found", StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogWarning("Deactivating chat {ChatId} after schedule send: {Error}", channel.ChatId, ex.Message);
            channel.IsActive = false;
            if (thread is not null)
            {
                db.ScheduleThreads.Remove(thread);
                threads.Remove(channel.ChatId);
            }

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

    // The picture is nice to have: if Telegram cannot load it, the text alone still goes out.
    private async Task<(int MessageId, bool IsPhoto)> SendNewAsync(Channel channel, Desired desired, CancellationToken ct)
    {
        if (desired.ImageUrl is not null)
        {
            try
            {
                var message = await WithRetry(() => bot.SendPhoto(
                    channel.ChatId,
                    InputFile.FromString(desired.ImageUrl),
                    caption: desired.Text,
                    cancellationToken: ct), ct);
                return (message.Id, true);
            }
            catch (ApiRequestException ex) when (ex.ErrorCode == 400 && !IsChatProblem(ex))
            {
                logger.LogInformation("Schedule picture rejected for chat {ChatId}, sending text only: {Error}", channel.ChatId, ex.Message);
            }
        }

        var text = await WithRetry(() => bot.SendMessage(channel.ChatId, desired.Text, cancellationToken: ct), ct);
        return (text.Id, false);
    }

    // False when the message no longer exists and a new one must be posted.
    private async Task<bool> EditAsync(Channel channel, ScheduleThread thread, Desired desired, CancellationToken ct)
    {
        try
        {
            if (!thread.IsPhoto || desired.ImageUrl is null)
            {
                await WithRetry(() => bot.EditMessageText(channel.ChatId, thread.MessageId, desired.Text, cancellationToken: ct), ct);
                return true;
            }

            if (thread.ImageVersion != desired.ImageVersion || thread.ImageKind != desired.ImageKind)
            {
                try
                {
                    await WithRetry(() => bot.EditMessageMedia(
                        channel.ChatId,
                        thread.MessageId,
                        new InputMediaPhoto(InputFile.FromString(desired.ImageUrl)) { Caption = desired.Text },
                        cancellationToken: ct), ct);
                    return true;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 400 && !IsGone(ex) && !IsNotModified(ex))
                {
                    logger.LogInformation("Schedule picture update rejected for chat {ChatId}, updating the caption only: {Error}", channel.ChatId, ex.Message);
                }
            }

            await WithRetry(() => bot.EditMessageCaption(channel.ChatId, thread.MessageId, desired.Text, cancellationToken: ct), ct);
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
}
