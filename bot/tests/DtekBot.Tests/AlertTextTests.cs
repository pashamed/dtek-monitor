using DtekBot.Shared;

namespace DtekBot.Tests;

public class AlertTextTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyDictionary<string, string> Build(
        IReadOnlyList<string>? parts = null,
        string? popup = "text",
        AlertKind kind = AlertKind.Emergency) =>
        AlertText.Values(kind, "м. Київ", parts ?? [], "https://x.y", Now, popup);

    [Fact]
    public void Values_ContainsAllExpectedKeys()
    {
        var values = Build();

        Assert.Equal(
            new[] { "date", "daypart", "emoji", "message", "part", "popup", "region", "site", "time", "title", "weekday", "where" },
            values.Keys.OrderBy(k => k).ToArray());
        Assert.Equal("15.01.2026", values["date"]);
        Assert.Equal("Четвер", values["weekday"]);
        Assert.Equal("День", values["daypart"]);
    }

    [Fact]
    public void Values_NoParts_WhereIsJustRegion_AndPartIsEmpty()
    {
        var values = Build([]);

        Assert.Equal("м. Київ", values["where"]);
        Assert.Equal("", values["part"]);
        Assert.Equal("м. Київ", values["region"]);
    }

    [Fact]
    public void Values_OnePart_WhereIncludesPart()
    {
        var values = Build(["Правий берег"]);

        Assert.Equal("Правий берег", values["part"]);
        Assert.Equal("м. Київ (Правий берег)", values["where"]);
    }

    [Fact]
    public void Values_TwoParts_AreCommaSeparated()
    {
        var values = Build(["Правий берег", "Лівий берег"]);

        Assert.Equal("Правий берег, Лівий берег", values["part"]);
        Assert.Equal("м. Київ (Правий берег, Лівий берег)", values["where"]);
    }

    [Fact]
    public void Values_Message_StartsWithWhere()
    {
        var values = Build(["Правий берег"]);

        Assert.StartsWith("м. Київ (Правий берег): ", values["message"]);
    }

    [Theory]
    [InlineData(AlertKind.Emergency, "\U0001F6A8")]
    [InlineData(AlertKind.Accident, "\u26A0")]
    [InlineData(AlertKind.Unknown, "\u2139")]
    [InlineData(AlertKind.Ended, "\u2139")]
    public void Values_EmojiMatchesKind(AlertKind kind, string emoji)
    {
        Assert.Contains(emoji, Build(kind: kind)["emoji"]);
    }

    [Fact]
    public void Values_KindsHaveDistinctTitles()
    {
        var titles = Enum.GetValues<AlertKind>().Select(k => Build(kind: k)["title"]).ToList();

        Assert.Equal(titles.Count, titles.Distinct().Count());
    }

    [Fact]
    public void Values_PassesThroughSiteAndKyivTime()
    {
        var values = Build();

        Assert.Equal("https://x.y", values["site"]);
        Assert.Equal("15.01 14:00", values["time"]);
    }

    [Fact]
    public void Values_Popup_IsTrimmed_AndNullBecomesEmpty()
    {
        Assert.Equal("hello", Build(popup: "  hello \n")["popup"]);
        Assert.Equal("", Build(popup: null)["popup"]);
    }

    [Fact]
    public void Values_Popup_AtLimit_IsNotTruncated()
    {
        var popup = new string('a', 2000);

        Assert.Equal(popup, Build(popup: popup)["popup"]);
    }

    [Fact]
    public void Values_Popup_OverLimit_IsTruncatedWithEllipsis()
    {
        var value = Build(popup: new string('a', 2500))["popup"];

        Assert.Equal(2001, value.Length);
        Assert.Equal(new string('a', 2000) + "…", value);
    }
}
