using System.Collections.Concurrent;

namespace DtekBot.Api.Svitlobot;

// Every write goes to Svitlobot with the channel's key (and may delete the post in the Telegram channel),
// so a chat gets at most a few per minute.
public static class SvitlobotWriteLimiter
{
    public const int MaxPerWindow = 6;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private static readonly ConcurrentDictionary<long, Queue<DateTimeOffset>> Recent = new();

    public static bool IsLimited(long chatId, DateTimeOffset now)
    {
        var queue = Recent.GetOrAdd(chatId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > Window)
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
}
