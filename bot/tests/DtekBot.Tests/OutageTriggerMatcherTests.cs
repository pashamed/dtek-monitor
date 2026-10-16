using DtekBot.Api.Bot;
using Xunit;

namespace DtekBot.Tests;

public class OutageTriggerMatcherTests
{
    [Theory]
    [InlineData(null, "🔴 14:24 Світло зникло", true)]
    [InlineData("", "СВІТЛО   ЗНИКЛО", true)]
    [InlineData("зараз світло зникло", "Увага: зараз світло зникло!", true)]
    [InlineData("зараз світло зникло", "🔴 14:24 Світло зникло", false)]
    [InlineData(@"світло\s+(зникло|вимкнули)", "Світло вимкнули", true)]
    [InlineData("світло (зникло", "а світло (зникло тут", true)]
    public void IsMatch_UsesCustomOrDefaultPhrase(string? pattern, string post, bool expected) =>
        Assert.Equal(expected, OutageTriggerMatcher.IsMatch(pattern, post));

    [Fact]
    public void Normalize_DropsEmptyAndDefault()
    {
        Assert.Null(OutageTriggerMatcher.Normalize("  "));
        Assert.Null(OutageTriggerMatcher.Normalize(OutageTriggerMatcher.DefaultPattern));
        Assert.Equal("зараз світло зникло", OutageTriggerMatcher.Normalize("  зараз світло зникло "));
    }

    [Fact]
    public void Recent_KeepsLastTenAndRoundTripsCyrillic()
    {
        string? json = null;
        for (var i = 0; i < 12; i++)
        {
            json = OutageTriggerMatcher.AppendRecent(json, $"пост {i}");
        }

        var recent = OutageTriggerMatcher.ParseRecent(json);
        Assert.Equal(10, recent.Count);
        Assert.Equal("пост 2", recent[0]);
        Assert.Equal("пост 11", recent[^1]);
        Assert.Contains("пост 11", json);
    }

    [Fact]
    public void Check_WarnsButNeverFails()
    {
        var none = OutageTriggerMatcher.Check("зараз світло зникло", []);
        Assert.True(none.Valid);
        Assert.NotNull(none.Warning);

        var miss = OutageTriggerMatcher.Check("зараз світло зникло", ["🔴 14:24 Світло зникло"]);
        Assert.Equal((1, 0), (miss.Seen, miss.Matched));
        Assert.NotNull(miss.Warning);

        var hit = OutageTriggerMatcher.Check("світло зникло", ["🔴 14:24 Світло зникло", "інше"]);
        Assert.Equal((2, 1), (hit.Seen, hit.Matched));
        Assert.Null(hit.Warning);

        var invalid = OutageTriggerMatcher.Check("світло (зникло", ["світло (зникло"]);
        Assert.False(invalid.Valid);
        Assert.Equal(1, invalid.Matched);
        Assert.NotNull(invalid.Warning);
    }
}
