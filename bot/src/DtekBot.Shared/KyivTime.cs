namespace DtekBot.Shared;

// Ukraine follows the EU summer-time rule. Computed by hand because browsers and
// minimal hosts do not always ship the tz database.
public static class KyivTime
{
    public static DateTimeOffset FromUtc(DateTimeOffset moment)
    {
        var utc = moment.ToUniversalTime();
        var start = LastSundayAt0100Utc(utc.Year, 3);
        var end = LastSundayAt0100Utc(utc.Year, 10);
        var offset = utc >= start && utc < end ? TimeSpan.FromHours(3) : TimeSpan.FromHours(2);
        return utc.ToOffset(offset);
    }

    public static string Format(DateTimeOffset moment) => FromUtc(moment).ToString("dd.MM HH:mm");

    private static DateTimeOffset LastSundayAt0100Utc(int year, int month)
    {
        var last = new DateTime(year, month, DateTime.DaysInMonth(year, month), 1, 0, 0, DateTimeKind.Utc);
        while (last.DayOfWeek != DayOfWeek.Sunday)
        {
            last = last.AddDays(-1);
        }

        return new DateTimeOffset(last);
    }
}
