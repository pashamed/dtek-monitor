using DtekBot.Shared;

namespace DtekBot.Tests;

public class KyivTimeTests
{
    private static DateTimeOffset Utc(int y, int m, int d, int h, int min = 0) =>
        new(y, m, d, h, min, 0, TimeSpan.Zero);

    [Fact]
    public void Winter_IsUtcPlus2()
    {
        var kyiv = KyivTime.FromUtc(Utc(2026, 1, 15, 12));

        Assert.Equal(TimeSpan.FromHours(2), kyiv.Offset);
        Assert.Equal(14, kyiv.Hour);
        Assert.Equal(Utc(2026, 1, 15, 12), kyiv);
    }

    [Fact]
    public void Summer_IsUtcPlus3()
    {
        var kyiv = KyivTime.FromUtc(Utc(2026, 7, 15, 12));

        Assert.Equal(TimeSpan.FromHours(3), kyiv.Offset);
        Assert.Equal(15, kyiv.Hour);
    }

    [Fact]
    public void SpringForward_2026_SwitchesAt0100Utc()
    {
        // Last Sunday of March 2026 is 29 March.
        Assert.Equal(DayOfWeek.Sunday, new DateTime(2026, 3, 29).DayOfWeek);

        var before = KyivTime.FromUtc(Utc(2026, 3, 29, 0, 59));
        var at = KyivTime.FromUtc(Utc(2026, 3, 29, 1, 0));
        var after = KyivTime.FromUtc(Utc(2026, 3, 29, 1, 1));

        Assert.Equal(TimeSpan.FromHours(2), before.Offset);
        Assert.Equal(new DateTime(2026, 3, 29, 2, 59, 0), before.DateTime);
        Assert.Equal(TimeSpan.FromHours(3), at.Offset);
        Assert.Equal(new DateTime(2026, 3, 29, 4, 0, 0), at.DateTime);
        Assert.Equal(TimeSpan.FromHours(3), after.Offset);
        Assert.Equal(new DateTime(2026, 3, 29, 4, 1, 0), after.DateTime);
    }

    [Fact]
    public void FallBack_2026_SwitchesAt0100Utc()
    {
        // Last Sunday of October 2026 is 25 October.
        Assert.Equal(DayOfWeek.Sunday, new DateTime(2026, 10, 25).DayOfWeek);

        var before = KyivTime.FromUtc(Utc(2026, 10, 25, 0, 59));
        var at = KyivTime.FromUtc(Utc(2026, 10, 25, 1, 0));
        var after = KyivTime.FromUtc(Utc(2026, 10, 25, 1, 1));

        Assert.Equal(TimeSpan.FromHours(3), before.Offset);
        Assert.Equal(new DateTime(2026, 10, 25, 3, 59, 0), before.DateTime);
        Assert.Equal(TimeSpan.FromHours(2), at.Offset);
        Assert.Equal(new DateTime(2026, 10, 25, 3, 0, 0), at.DateTime);
        Assert.Equal(TimeSpan.FromHours(2), after.Offset);
        Assert.Equal(new DateTime(2026, 10, 25, 3, 1, 0), after.DateTime);
    }

    [Theory]
    [InlineData("2026-03-29T00:59:00Z", "29.03 02:59")]
    [InlineData("2026-03-29T01:00:00Z", "29.03 04:00")]
    [InlineData("2026-10-25T00:59:00Z", "25.10 03:59")]
    [InlineData("2026-10-25T01:00:00Z", "25.10 03:00")]
    [InlineData("2026-01-15T12:00:00Z", "15.01 14:00")]
    [InlineData("2026-07-15T12:00:00Z", "15.07 15:00")]
    [InlineData("2026-12-31T23:30:00Z", "01.01 01:30")]
    public void Format_UsesDayMonthHourMinute(string utc, string expected)
    {
        Assert.Equal(expected, KyivTime.Format(DateTimeOffset.Parse(utc)));
    }

    [Fact]
    public void Format_ConvertsFromAnyInputOffset()
    {
        var local = new DateTimeOffset(2026, 7, 15, 17, 0, 0, TimeSpan.FromHours(5));

        Assert.Equal("15.07 15:00", KyivTime.Format(local));
    }
}
