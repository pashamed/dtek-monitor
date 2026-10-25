using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace DtekBot.Api.Svitlobot;

// Svitlobot's integration rules: at most one request per minute per channel, reads cached for 5 minutes,
// and growing pauses after errors. A single slot per chat also keeps requests for one key from running in parallel.
public static class SvitlobotRequestLimiter
{
    public const int MaxPerWindow = 1;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan ReadCacheTime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan[] Backoff =
        [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5)];

    private static readonly ConcurrentDictionary<long, Queue<DateTimeOffset>> Recent = new();
    private static readonly ConcurrentDictionary<long, (int Failures, DateTimeOffset RetryAt)> Failures = new();
    private static readonly ConcurrentDictionary<long, (string KeyHash, DateTimeOffset Expires, SvitlobotChannelInfo Info)> Reads = new();

    // Takes the chat's request slot; true means the caller must not contact Svitlobot now.
    public static bool IsLimited(long chatId, DateTimeOffset now)
    {
        var queue = Recent.GetOrAdd(chatId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxPerWindow)
            {
                return true;
            }

            queue.Enqueue(now);
            return false;
        }
    }

    public static bool IsBackingOff(long chatId, DateTimeOffset now) =>
        Failures.TryGetValue(chatId, out var state) && now < state.RetryAt;

    public static TimeSpan RecordFailure(long chatId, DateTimeOffset now)
    {
        var state = Failures.AddOrUpdate(
            chatId,
            _ => (1, now + Backoff[0]),
            (_, previous) => (previous.Failures + 1, now + Backoff[Math.Min(previous.Failures, Backoff.Length - 1)]));
        return state.RetryAt - now;
    }

    public static void RecordSuccess(long chatId) => Failures.TryRemove(chatId, out _);

    public static bool TryGetCached(long chatId, string key, DateTimeOffset now, out SvitlobotChannelInfo info)
    {
        if (Reads.TryGetValue(chatId, out var cached) && cached.KeyHash == Hash(key) && now < cached.Expires)
        {
            info = cached.Info;
            return true;
        }

        info = null!;
        return false;
    }

    public static void Cache(long chatId, string key, SvitlobotChannelInfo info, DateTimeOffset now) =>
        Reads[chatId] = (Hash(key), now + ReadCacheTime, info);

    // A write changes what Svitlobot reports (schedule flag, template), so the cached read is dropped.
    public static void Invalidate(long chatId) => Reads.TryRemove(chatId, out _);

    private static string Hash(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
