using System.Collections.Concurrent;

namespace DtekBot.Api.MiniApp;

public sealed class FeedbackRateLimiter
{
    private const int MaxRequests = 3;
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<long, Queue<DateTimeOffset>> _recent = new();

    public bool IsLimited(long userId, DateTimeOffset now)
    {
        var queue = _recent.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxRequests)
            {
                return true;
            }

            queue.Enqueue(now);
            return false;
        }
    }
}
