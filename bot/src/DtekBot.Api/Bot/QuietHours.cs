using DtekBot.Api.Data;
using DtekBot.Shared;

namespace DtekBot.Api.Bot;

public static class QuietHours
{
    public static bool IsQuiet(Channel channel, DateTimeOffset now)
    {
        if (!channel.QuietHoursEnabled)
        {
            return false;
        }

        var kyivNow = KyivTime.FromUtc(now);
        var currentMinute = kyivNow.Hour * 60 + kyivNow.Minute;
        var start = channel.QuietHoursStartMinute;
        var end = channel.QuietHoursEndMinute;
        if (start == end)
        {
            return false;
        }

        return start < end
            ? currentMinute >= start && currentMinute < end
            : currentMinute >= start || currentMinute < end;
    }
}
