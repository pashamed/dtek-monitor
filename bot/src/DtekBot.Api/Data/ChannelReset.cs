using Microsoft.EntityFrameworkCore;

namespace DtekBot.Api.Data;

public static class ChannelReset
{
    // Turns the row into a brand-new unsubscribed channel (only the id, title and creation time survive) and drops what
    // the bot posted for it: schedule threads and still-open alert deliveries. Does not save.
    public static async Task ResetAsync(BotDbContext db, Channel channel, CancellationToken ct)
    {
        var fresh = new Channel
        {
            ChatId = channel.ChatId,
            Title = channel.Title,
            CreatedAt = channel.CreatedAt,
            IsActive = false,
        };
        db.Entry(channel).CurrentValues.SetValues(fresh);

        db.ScheduleThreads.RemoveRange(await db.ScheduleThreads.Where(t => t.ChatId == channel.ChatId).ToListAsync(ct));
        db.SchedulePostThreads.RemoveRange(await db.SchedulePostThreads.Where(t => t.ChatId == channel.ChatId).ToListAsync(ct));
        db.Deliveries.RemoveRange(await db.Deliveries.Where(d => d.ChatId == channel.ChatId && !d.IsClosed).ToListAsync(ct));
    }
}
