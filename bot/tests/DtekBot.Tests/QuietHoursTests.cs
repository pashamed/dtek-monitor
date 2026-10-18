using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DtekBot.Tests;

public sealed class QuietHoursTests
{
    [Theory]
    [InlineData(22, 0, true)]
    [InlineData(23, 59, true)]
    [InlineData(0, 0, true)]
    [InlineData(7, 59, true)]
    [InlineData(8, 0, false)]
    [InlineData(21, 59, false)]
    public void OvernightWindow_IncludesStartAndExcludesEnd(int hour, int minute, bool expected)
    {
        var channel = new Channel
        {
            QuietHoursEnabled = true,
            QuietHoursStartMinute = 22 * 60,
            QuietHoursEndMinute = 8 * 60,
        };
        var now = new DateTimeOffset(2026, 1, 10, hour, minute, 0, TimeSpan.FromHours(2));

        Assert.Equal(expected, QuietHours.IsQuiet(channel, now));
    }

    [Theory]
    [InlineData(9, 0, true)]
    [InlineData(10, 30, true)]
    [InlineData(17, 0, false)]
    public void SameDayWindow_UsesKyivLocalTime(int hour, int minute, bool expected)
    {
        var channel = new Channel
        {
            QuietHoursEnabled = true,
            QuietHoursStartMinute = 9 * 60,
            QuietHoursEndMinute = 17 * 60,
        };
        var now = new DateTimeOffset(2026, 1, 10, hour, minute, 0, TimeSpan.FromHours(2));

        Assert.Equal(expected, QuietHours.IsQuiet(channel, now));
    }

    [Fact]
    public void DisabledQuietHours_NeverSilenceMessages()
    {
        var channel = new Channel { QuietHoursEnabled = false };

        Assert.False(QuietHours.IsQuiet(channel, new DateTimeOffset(2026, 1, 10, 23, 0, 0, TimeSpan.FromHours(2))));
    }

    [Fact]
    public void QuietHoursMigration_IsDiscoveredAndGeneratesSql()
    {
        var options = new DbContextOptionsBuilder<BotDbContext>()
            .UseSqlServer("Server=localhost;Database=dtek_migration_test;TrustServerCertificate=True")
            .Options;
        using var db = new BotDbContext(options);

        var migration = Assert.Single(db.Database.GetMigrations(), id => id == "20261018234000_QuietHours");
        var script = db.GetService<IMigrator>().GenerateScript(toMigration: migration);

        Assert.Contains("QuietHoursEnabled", script);
        Assert.Contains("QuietHoursStartMinute", script);
        Assert.Contains("QuietHoursEndMinute", script);
    }
}
