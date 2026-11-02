using DtekBot.Api.Bot;
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
            new[] { "address", "city", "date", "daypart", "emoji", "group", "message", "part", "popup", "region", "regionId", "site", "status", "statusCode", "time", "title", "weekday", "where" },
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
    [InlineData(AlertKind.Emergency, "Графіки не діють")]
    [InlineData(AlertKind.Accident, "Можливі позачергові відключення")]
    [InlineData(AlertKind.Unknown, "статус відключень не визначено")]
    [InlineData(AlertKind.Ended, "Скасування не підтверджене")]
    public void DefaultAlerts_AreConciseAndPreserveUncertainty(AlertKind kind, string required)
    {
        var values = Build(kind: kind);
        var message = values["message"];
        var rendered = MessageTemplate.Render(null, values);

        Assert.Contains(required, message);
        Assert.True(message.Length <= 200, $"Default message is too long: {message.Length}");
        Assert.Single(rendered.Split('\n'));
        Assert.DoesNotContain("Розумно споживайте", rendered);
        Assert.DoesNotContain("Деталі:", rendered);
        Assert.DoesNotContain("https://x.y", rendered);
        if (kind is AlertKind.Emergency or AlertKind.Accident)
            Assert.Contains("повідомимо про зміни", message);
        if (kind == AlertKind.Unknown)
            Assert.Contains("Перевіряємо оновлення", message);
    }

    [Fact]
    public void StabilizationEnd_DoesNotDuplicateSourceOrConfirmPower()
    {
        var values = AlertText.Values(AlertKind.Ended, "м. Київ", [], "https://x.y", Now, null,
            stabilizationActive: true);
        Assert.Contains("діють стабілізаційні графіки", values["message"]);
        Assert.Contains("не підтверджує наявність світла", values["message"]);
        Assert.DoesNotContain("екстрені відключення завершено", values["message"]);
        Assert.Contains("стежимо за змінами", values["message"]);
        Assert.DoesNotContain("за повідомленням ДТЕК", values["message"]);
        Assert.True(values["message"].Length <= 200);
        Assert.Single(MessageTemplate.Render(null, values).Split('\n'));
    }

    [Fact]
    public void MissingNotice_DoesNotClaimCancellationOrPowerRestoration()
    {
        var message = Build(kind: AlertKind.Ended)["message"];
        Assert.Contains("Скасування не підтверджене", message);
        Assert.Contains("перевіряємо оновлення", message);
        Assert.Contains("не підтверджує наявність світла", message);
        Assert.DoesNotContain("✅", message);
    }

    [Fact]
    public void DtekAlerts_AndCustomPreviews_DoNotAppendAttribution()
    {
        const string custom = "<b>Власний текст</b>";
        Assert.Equal(custom, AlertSourceText.Tag(custom, AlertSource.Dtek));
        Assert.Equal(custom, AlertSourceText.EnsureAttribution(custom, AlertSource.Dtek));
        var rendered = MessageTemplate.Render("{message}\nДеталі: {site}", Build());
        Assert.Contains("Деталі: https://x.y", rendered);
    }

    [Theory]
    [InlineData(AlertSource.Ukrenergo, "(за даними Укренерго)")]
    [InlineData(AlertSource.Yasno, "(за даними Yasno)")]
    public void OtherSources_StillHaveDistinctAttribution(AlertSource source, string label)
    {
        Assert.Contains(label, AlertSourceText.Tag("Статус.", source));
        Assert.Contains(label, AlertSourceText.EnsureAttribution("Власний текст", source));
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
