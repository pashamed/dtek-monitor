using System.Collections.Concurrent;

namespace DtekBot.Api.MiniApp;

// Test posts go to a real channel, so a chat gets at most a few per minute.
public sealed class TestSendLimiter
{
    private const int MaxPerWindow = 5;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<long, Queue<DateTimeOffset>> _recent = new();

    public bool IsLimited(long chatId, DateTimeOffset now)
    {
        var queue = _recent.GetOrAdd(chatId, _ => new Queue<DateTimeOffset>());
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
