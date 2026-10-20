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
    private const string WeeklyRefreshStateKeyPrefix = "svitlobot-weekly-refresh:";
    private static readonly TimeSpan WeeklyRefreshTime = new(2, 5, 0);
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
            .Where(state => state.Key.StartsWith(StateKeyPrefix) || state.Key.StartsWith(WeeklyRefreshStateKeyPrefix))
            .ToDictionaryAsync(state => state.Key, ct);
        var localNow = KyivTime.FromUtc(time.GetUtcNow());
        var weeklyRefreshDate = localNow.DayOfWeek == DayOfWeek.Monday && localNow.TimeOfDay >= WeeklyRefreshTime
            ? localNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            : null;
        var updated = 0;

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

                var timetable = WeeklyPreset.Union(snapshot, channel.GroupIds(), includeMaybe: true);
                if (timetable is null)
                {
                    logger.LogInformation(
                        "Svitlobot schedule sync waiting for weekly schedule: region={RegionId}, group={GroupId}",
                        region.Id, channel.GroupId);
                    continue;
                }

                var stateKey = StateKey(channel.ChatId);
                var signature = Signature(region.Id, string.Join(",", channel.GroupIds()), timetable);
                var refreshKey = WeeklyRefreshStateKey(channel.ChatId);
                var refreshDue = weeklyRefreshDate is not null
                    && (!states.TryGetValue(refreshKey, out var previousRefresh) || previousRefresh.Value != weeklyRefreshDate);
                var signatureChanged = !states.TryGetValue(stateKey, out var previous) || previous.Value != signature;
                if (!signatureChanged && !refreshDue)
                {
                    continue;
                }

                if (!WeeklyPreset.HasOutages(timetable))
                {
                    continue;
                }

                if (SvitlobotWriteLimiter.IsLimited(channel.ChatId, time.GetUtcNow()))
                {
                    logger.LogInformation("Svitlobot schedule sync deferred by write limit for chat {ChatId}", channel.ChatId);
                    continue;
                }

                var result = await svitlobot.SetTimetableAsync(key, timetable, ct);
                if (!result.Ok)
                {
                    logger.LogWarning(
                        "Svitlobot schedule sync failed for chat {ChatId}: {Failure}",
                        channel.ChatId, result.Failure);
                    continue;
                }

                if (previous is null)
                {
                    previous = new KeyValue { Key = stateKey, Value = signature };
                    db.KeyValues.Add(previous);
                    states[stateKey] = previous;
                }
                else
                {
                    previous.Value = signature;
                }

                if (refreshDue)
                {
                    if (states.TryGetValue(refreshKey, out var refreshState))
                    {
                        refreshState.Value = weeklyRefreshDate!;
                    }
                    else
                    {
                        refreshState = new KeyValue { Key = refreshKey, Value = weeklyRefreshDate! };
                        db.KeyValues.Add(refreshState);
                        states[refreshKey] = refreshState;
                    }
                }

                updated++;
                await Task.Delay(WriteSpacing, ct);
            }
        }

        if (updated > 0)
        {
            await db.SaveChangesAsync(ct);
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
    }

    public async Task ResetAsync(long chatId, CancellationToken ct)
    {
        var state = await db.KeyValues.FirstOrDefaultAsync(item => item.Key == StateKey(chatId), ct);
        if (state is not null)
        {
            db.KeyValues.Remove(state);
        }
    }

    private static string StateKey(long chatId) => $"{StateKeyPrefix}{chatId}";
    private static string WeeklyRefreshStateKey(long chatId) => $"{WeeklyRefreshStateKeyPrefix}{chatId}";

    private static string Signature(string regionId, string groupId, string timetable)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{regionId}\n{groupId}\n{timetable}"));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
