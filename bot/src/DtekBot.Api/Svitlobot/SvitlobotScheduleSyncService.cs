using System.Security.Cryptography;
using System.Text;
using DtekBot.Api.Data;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace DtekBot.Api.Svitlobot;

public sealed class SvitlobotScheduleSyncService(
    BotDbContext db,
    IScheduleProvider schedules,
    IOptionsMonitor<DtekSettings> options,
    SvitlobotKeyProtector protector,
    ISvitlobotClient svitlobot,
    TimeProvider time,
    ILogger<SvitlobotScheduleSyncService> logger)
{
    private const string StateKeyPrefix = "svitlobot-schedule:";
    private const string FailedStateKeyPrefix = "svitlobot-schedule-failed:";
    private const string WeeklyRefreshStateKeyPrefix = "svitlobot-weekly-refresh:";

    // Stored instead of a signature after the backend cleared the schedule it had written.
    private const string RemovedMarker = "removed";
    private static readonly TimeSpan WriteSpacing = TimeSpan.FromMilliseconds(100);

    public async Task<int> TickAsync(CancellationToken ct)
    {
        if (!protector.IsConfigured)
        {
            return 0;
        }

        var channels = await db.Channels
            .Where(channel => channel.GroupId != null && channel.SvitlobotKey != null)
            .ToListAsync(ct);
        if (channels.Count == 0)
        {
            return 0;
        }

        var regionSettings = options.CurrentValue;
        var states = await db.KeyValues
            .Where(state => state.Key.StartsWith(StateKeyPrefix)
                || state.Key.StartsWith(FailedStateKeyPrefix)
                || state.Key.StartsWith(WeeklyRefreshStateKeyPrefix))
            .ToDictionaryAsync(state => state.Key, ct);
        var localNow = KyivTime.FromUtc(time.GetUtcNow());
        var timers = regionSettings.Timers;
        var weeklyRefreshDate = localNow.DayOfWeek == timers.SvitlobotWeeklyRefreshDay
            && localNow.TimeOfDay >= timers.SvitlobotWeeklyRefreshTime
            ? localNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : null;
        var updated = 0;
        var dirty = false;

        foreach (var regionChannels in channels.GroupBy(channel => channel.RegionId))
        {
            var region = regionSettings.Regions.FirstOrDefault(candidate =>
                candidate.HasSchedules && candidate.Id == regionChannels.Key);
            if (region is null)
            {
                continue;
            }

            var snapshot = await schedules.GetAsync(region.Id, ct);
            if (snapshot is null)
            {
                continue;
            }

            foreach (var channel in regionChannels)
            {
                var key = protector.Unprotect(channel.SvitlobotKey);
                if (key is null)
                {
                    logger.LogWarning("Svitlobot schedule sync skipped for chat {ChatId}: saved key is unreadable", channel.ChatId);
                    continue;
                }

                var group = channel.SvitlobotGroup();
                var timetable = group is null ? null : WeeklyPreset.Union(snapshot, [group], includeMaybe: true);
                if (timetable is null)
                {
                    logger.LogInformation(
                        "Svitlobot schedule sync waiting for weekly schedule: region={RegionId}, group={GroupId}",
                        region.Id, group);
                    continue;
                }

                var stateKey = StateKey(channel.ChatId);
                var failedKey = FailedStateKey(channel.ChatId);
                var refreshKey = WeeklyRefreshStateKey(channel.ChatId);
                var signature = Signature(region.Id, group!, timetable);
                states.TryGetValue(stateKey, out var previous);
                states.TryGetValue(failedKey, out var failed);
                var remove = !WeeklyPreset.HasOutages(timetable);
                var refreshDue = false;
                if (remove)
                {
                    // Only a schedule this backend wrote is cleared; removal also deletes the channel's schedule post.
                    if (previous is null || previous.Value == RemovedMarker)
                    {
                        continue;
                    }
                }
                else
                {
                    refreshDue = weeklyRefreshDate is not null
                        && (!states.TryGetValue(refreshKey, out var previousRefresh) || previousRefresh.Value != weeklyRefreshDate);
                    if (previous?.Value == signature && !refreshDue)
                    {
                        continue;
                    }
                }

                if (failed?.Value == signature)
                {
                    continue;
                }

                if (!remove && !WeeklyPreset.IsStorable(timetable))
                {
                    logger.LogWarning(
                        "Svitlobot schedule sync skipped for chat {ChatId}: the week has only half-hour outages, which Svitlobot does not accept",
                        channel.ChatId);
                    SetState(states, failedKey, signature);
                    dirty = true;
                    continue;
                }

                var now = time.GetUtcNow();
                if (SvitlobotRequestLimiter.IsBackingOff(channel.ChatId, now))
                {
                    continue;
                }

                if (SvitlobotRequestLimiter.IsLimited(channel.ChatId, now))
                {
                    logger.LogInformation("Svitlobot schedule sync deferred by request limit for chat {ChatId}", channel.ChatId);
                    continue;
                }

                var result = remove
                    ? await svitlobot.RemoveAsync(key, ct)
                    : await svitlobot.SetTimetableAsync(key, timetable, ct);
                SvitlobotRequestLimiter.Invalidate(channel.ChatId);
                if (!result.Ok)
                {
                    if (result.Failure == SvitlobotFailure.Unavailable)
                    {
                        var pause = SvitlobotRequestLimiter.RecordFailure(channel.ChatId, time.GetUtcNow());
                        logger.LogWarning(
                            "Svitlobot schedule sync failed for chat {ChatId}: {Failure}; next attempt in {Pause}",
                            channel.ChatId, result.Failure, pause);
                    }
                    else
                    {
                        // Repeating the same request would get the same answer; wait for a new schedule or key.
                        SetState(states, failedKey, signature);
                        dirty = true;
                        logger.LogWarning(
                            "Svitlobot schedule sync failed for chat {ChatId}: {Failure}; waiting for a schedule or key change",
                            channel.ChatId, result.Failure);
                    }

                    continue;
                }

                SvitlobotRequestLimiter.RecordSuccess(channel.ChatId);
                if (failed is not null)
                {
                    db.KeyValues.Remove(failed);
                    states.Remove(failedKey);
                }

                SetState(states, stateKey, remove ? RemovedMarker : signature);
                if (refreshDue)
                {
                    SetState(states, refreshKey, weeklyRefreshDate!);
                }

                if (remove)
                {
                    logger.LogInformation("Svitlobot schedule removed for chat {ChatId}: the week has no planned outages", channel.ChatId);
                }

                updated++;
                dirty = true;
                await Task.Delay(WriteSpacing, ct);
            }
        }

        if (dirty)
        {
            await db.SaveChangesAsync(ct);
        }

        if (updated > 0)
        {
            logger.LogInformation("Svitlobot weekly schedules synchronized for {Count} channels", updated);
        }

        return updated;
    }
    public async Task MarkSyncedAsync(long chatId, string regionId, string groupId, string timetable, CancellationToken ct)
    {
        var key = StateKey(chatId);
        var state = await db.KeyValues.FirstOrDefaultAsync(item => item.Key == key, ct);
        var signature = Signature(regionId, groupId, timetable);
        if (state is null)
        {
            db.KeyValues.Add(new KeyValue { Key = key, Value = signature });
        }
        else
        {
            state.Value = signature;
        }

        var failedKey = FailedStateKey(chatId);
        var failed = await db.KeyValues.FirstOrDefaultAsync(item => item.Key == failedKey, ct);
        if (failed is not null)
        {
            db.KeyValues.Remove(failed);
        }

        SvitlobotRequestLimiter.RecordSuccess(chatId);
    }

    public async Task ResetAsync(long chatId, CancellationToken ct)
    {
        var keys = new[] { StateKey(chatId), FailedStateKey(chatId) };
        var states = await db.KeyValues.Where(item => keys.Contains(item.Key)).ToListAsync(ct);
        db.KeyValues.RemoveRange(states);
        SvitlobotRequestLimiter.RecordSuccess(chatId);
        SvitlobotRequestLimiter.Invalidate(chatId);
    }

    private void SetState(Dictionary<string, KeyValue> states, string key, string value)
    {
        if (states.TryGetValue(key, out var state))
        {
            state.Value = value;
            return;
        }

        state = new KeyValue { Key = key, Value = value };
        db.KeyValues.Add(state);
        states[key] = state;
    }

    private static string StateKey(long chatId) => $"{StateKeyPrefix}{chatId}";
    private static string FailedStateKey(long chatId) => $"{FailedStateKeyPrefix}{chatId}";
    private static string WeeklyRefreshStateKey(long chatId) => $"{WeeklyRefreshStateKeyPrefix}{chatId}";

    private static string Signature(string regionId, string groupId, string timetable)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{regionId}\n{groupId}\n{timetable}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
