using System.Text.RegularExpressions;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public class ScheduleTextTests
{
    private static readonly Regex PowerOutPattern =
        new(@"світло\s+зникло", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly ScheduleSettings Settings = new();

    // ---- Duration ----

    [Theory]
    [InlineData(0, "менше хвилини")]
    [InlineData(29, "менше хвилини")]
    [InlineData(60, "1 хв")]
    [InlineData(90, "2 хв")]
    [InlineData(59 * 60, "59 хв")]
    [InlineData(60 * 60, "1 год")]
    [InlineData(61 * 60, "1 год 1 хв")]
    [InlineData(125 * 60, "2 год 5 хв")]
    [InlineData(120 * 60, "2 год")]
    [InlineData(23 * 3600 + 59 * 60, "23 год 59 хв")]
    [InlineData(26 * 3600, "26 год")]
    public void Duration_FormatsMinutesAndHours(int seconds, string expected)
    {
        Assert.Equal(expected, ScheduleText.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Duration_NegativeSpan_IsLessThanAMinute()
    {
        Assert.Equal("менше хвилини", ScheduleText.Duration(TimeSpan.FromMinutes(-5)));
    }

    // ---- Clock / ClockWithDay ----

    [Fact]
    public void Clock_UsesKyivTime_InWinterAndSummer()
    {
        Assert.Equal("14:00", ScheduleText.Clock(new DateTimeOffset(2026, 1, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("15:00", ScheduleText.Clock(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal("00:30", ScheduleText.Clock(new DateTimeOffset(2026, 7, 14, 21, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Clock_ConvertsFromAnyOffset()
    {
        Assert.Equal("15:00", ScheduleText.Clock(new DateTimeOffset(2026, 7, 15, 17, 0, 0, TimeSpan.FromHours(5))));
    }

    [Fact]
    public void Clock_AroundTheDstSwitch()
    {
        Assert.Equal("02:59", ScheduleText.Clock(new DateTimeOffset(2026, 3, 29, 0, 59, 0, TimeSpan.Zero)));
        Assert.Equal("04:00", ScheduleText.Clock(new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero)));
        Assert.Equal("03:59", ScheduleText.Clock(new DateTimeOffset(2026, 10, 25, 0, 59, 0, TimeSpan.Zero)));
        Assert.Equal("03:00", ScheduleText.Clock(new DateTimeOffset(2026, 10, 25, 1, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void ClockWithDay_SameKyivDay_IsJustTheTime()
    {
        Assert.Equal("14:00", ScheduleText.ClockWithDay(Kyiv(15, 14), Kyiv(15, 12)));
        Assert.Equal("23:59", ScheduleText.ClockWithDay(Kyiv(15, 23, 59), Kyiv(15, 0)));
    }

    [Fact]
    public void ClockWithDay_OtherDay_AppendsTheDate()
    {
        Assert.Equal("00:00 (16.10)", ScheduleText.ClockWithDay(Kyiv(16, 0), Kyiv(15, 20)));
        Assert.Equal("06:00 (16.10)", ScheduleText.ClockWithDay(Kyiv(16, 6), Kyiv(15, 12)));
        Assert.Equal("23:00 (14.10)", ScheduleText.ClockWithDay(Kyiv(14, 23), Kyiv(15, 1)));
    }

    [Fact]
    public void ClockWithDay_ComparesKyivDays_NotUtcDays()
    {
        // 16 Oct 00:30 Kyiv is still the 15th in UTC; 15 Oct 23:00 Kyiv is the 15th in UTC too.
        var moment = new DateTimeOffset(2026, 10, 15, 21, 30, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal("00:30 (16.10)", ScheduleText.ClockWithDay(moment, now));
    }

    [Fact]
    public void ClockWithDay_UsesKyivDayForNow_Too()
    {
        // Both are 16 Oct in Kyiv even though "now" is 15 Oct in UTC.
        var now = new DateTimeOffset(2026, 10, 15, 21, 5, 0, TimeSpan.Zero);

        Assert.Equal("06:00", ScheduleText.ClockWithDay(Kyiv(16, 6), now));
    }

    // ---- ImageUrl ----

    [Fact]
    public void ImageUrl_BuildsFileNameFromTheGroup_AndAddsVersion()
    {
        var url = ScheduleText.ImageUrl(Settings, "kyiv", "45.1", RealVersion);

        Assert.Equal(
            "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/images/kyiv/gpv-45-1-emergency.png?v=1991457b4408",
            url);
    }

    [Theory]
    [InlineData("https://example.test/base/")]
    [InlineData("https://example.test/base")]
    [InlineData("https://example.test/base///")]
    public void ImageUrl_HandlesTrailingSlashesOfBaseUrl(string baseUrl)
    {
        var url = ScheduleText.ImageUrl(new ScheduleSettings { BaseUrl = baseUrl }, "kyiv-region", "2.1", "");

        Assert.Equal("https://example.test/base/images/kyiv-region/gpv-2-1-emergency.png", url);
    }

    [Fact]
    public void ImageUrl_WithoutVersion_HasNoQuery()
    {
        Assert.DoesNotContain("?", ScheduleText.ImageUrl(Settings, "kyiv", "45.1", ""));
    }

    [Theory]
    [InlineData("abcdefghijklmnop", "abcdefghijkl")]
    [InlineData("abcdefghijkl", "abcdefghijkl")]
    [InlineData("abc", "abc")]
    public void ImageUrl_VersionIsClippedToTwelveCharacters(string version, string expected)
    {
        Assert.EndsWith("?v=" + expected, ScheduleText.ImageUrl(Settings, "kyiv", "1.1", version));
    }

    [Fact]
    public void ImageUrl_EscapesTheVersion()
    {
        Assert.EndsWith("?v=a%20b%26c", ScheduleText.ImageUrl(Settings, "kyiv", "1.1", "a b&c"));
    }

    [Fact]
    public void ImageUrl_DefaultKind_IsTheGrid()
    {
        Assert.Equal(
            ScheduleText.ImageUrl(Settings, "kyiv", "45.1", RealVersion, DtekBot.Shared.ScheduleImage.Grid),
            ScheduleText.ImageUrl(Settings, "kyiv", "45.1", RealVersion));
    }

    [Fact]
    public void ImageUrl_CardKind_UsesTheSummaryPicture()
    {
        var url = ScheduleText.ImageUrl(Settings, "kyiv", "45.1", RealVersion, DtekBot.Shared.ScheduleImage.Card);

        Assert.Equal(
            "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/images/kyiv/gpv-45-1-summary.png?v=1991457b4408",
            url);
    }

    [Fact]
    public void ImageUrl_GridKind_UsesTheEmergencyPicture()
    {
        var url = ScheduleText.ImageUrl(Settings, "kyiv", "2.1", RealVersion, DtekBot.Shared.ScheduleImage.Grid);

        Assert.Equal(
            "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/images/kyiv/gpv-2-1-emergency.png?v=1991457b4408",
            url);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1991457b44082fffd56b5ba30da9117b7ce609512969f291296a554880cd717a")]
    public void ImageUrl_NoneKind_IsNull_Always(string version)
    {
        Assert.Null(ScheduleText.ImageUrl(Settings, "kyiv", "45.1", version, DtekBot.Shared.ScheduleImage.None));
    }

    [Theory]
    [InlineData(DtekBot.Shared.ScheduleImage.Card, "-summary.png")]
    [InlineData(DtekBot.Shared.ScheduleImage.Grid, "-emergency.png")]
    public void ImageUrl_PictureKinds_FollowTheSameVersioningRules(DtekBot.Shared.ScheduleImage kind, string suffix)
    {
        var withVersion = ScheduleText.ImageUrl(Settings, "kyiv-region", "3.2", "abcdefghijklmnop", kind);
        var withoutVersion = ScheduleText.ImageUrl(Settings, "kyiv-region", "3.2", "", kind);

        Assert.EndsWith("/images/kyiv-region/gpv-3-2" + suffix + "?v=abcdefghijkl", withVersion);
        Assert.EndsWith("/images/kyiv-region/gpv-3-2" + suffix, withoutVersion);
    }

    [Fact]
    public void ImageUrl_DifferentVersions_GiveDifferentUrls()
    {
        Assert.NotEqual(
            ScheduleText.ImageUrl(Settings, "kyiv", "1.1", "aaaaaaaaaaaa1"),
            ScheduleText.ImageUrl(Settings, "kyiv", "1.1", "bbbbbbbbbbbb1"));
    }

    // ---- OffMessage ----

    private static readonly DateTimeOffset Noon = Kyiv(15, 12);

    [Fact]
    public void OffMessage_FirstOutageOfTheDay_HasNoPreviousLine()
    {
        var status = new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), null, Kyiv(15, 20, 30));

        var text = ScheduleText.OffMessage("м. Київ", "45.1", status, Noon);

        Assert.Equal(
            "🔴 11:00 Світло вимкнено за графіком\nм. Київ · черга 45.1\n🗓 Очікуємо повернення о 14:00",
            text);
    }

    [Fact]
    public void OffMessage_WithPreviousEnd_ShowsHowLongThePowerWas_AndNextDayTime()
    {
        var status = new ScheduleStatus(true, Kyiv(15, 20, 30), Kyiv(16, 0), Kyiv(15, 14), Kyiv(16, 6));

        var text = ScheduleText.OffMessage("м. Київ", "45.1", status, Kyiv(15, 21));

        Assert.Equal(
            "🔴 20:30 Світло вимкнено за графіком\nм. Київ · черга 45.1\n🕓 Світло було 6 год 30 хв\n🗓 Очікуємо повернення о 00:00 (16.10)",
            text);
    }

    [Fact]
    public void OffMessage_UnknownStart_OmitsTheTime_AndDurationLine()
    {
        var status = new ScheduleStatus(true, null, Kyiv(15, 3), null, null);

        var text = ScheduleText.OffMessage("м. Київ", "45.1", status, Kyiv(15, 1));

        Assert.Equal(
            "🔴 Світло вимкнено за графіком\nм. Київ · черга 45.1\n🗓 Очікуємо повернення о 03:00",
            text);
    }

    [Fact]
    public void OffMessage_UnknownEnd_SaysWaitingForAnUpdate()
    {
        var status = new ScheduleStatus(true, Kyiv(16, 22), null, Kyiv(16, 20), null);

        var text = ScheduleText.OffMessage("м. Київ", "45.1", status, Kyiv(16, 23));

        Assert.EndsWith("🗓 Час повернення поки невідомий (чекаємо оновлення графіка)", text);
        Assert.Contains("🕓 Світло було 2 год", text);
    }

    [Fact]
    public void OffMessage_PreviousEndNotBeforeStart_HasNoDurationLine()
    {
        var status = new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), Kyiv(15, 11), null);

        Assert.DoesNotContain("Світло було", ScheduleText.OffMessage("R", "1.1", status, Noon));
    }

    [Fact]
    public void OffMessage_UsesTheGivenRegionName()
    {
        var status = new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), null, null);

        Assert.Contains("Київська область · черга 3.2", ScheduleText.OffMessage("Київська область", "3.2", status, Noon));
    }

    // ---- OnMessage ----

    [Fact]
    public void OnMessage_PowerRestoredAfterAnOutage()
    {
        var status = new ScheduleStatus(false, Kyiv(15, 14), Kyiv(15, 20, 30), Kyiv(15, 14), Kyiv(15, 20, 30));

        var text = ScheduleText.OnMessage("м. Київ", "45.1", status, Kyiv(15, 11), Kyiv(15, 14, 5));

        Assert.Equal(
            "🟢 14:00 Світло повернулось за графіком\nм. Київ · черга 45.1\n🕓 Світла не було 3 год\n🕓 Світло є вже 5 хв\n🗓 Наступне відключення за графіком о 20:30",
            text);
    }

    [Fact]
    public void OnMessage_JustRestored_HasNoElapsedLine()
    {
        var status = new ScheduleStatus(false, Kyiv(15, 14), Kyiv(15, 20, 30), Kyiv(15, 14), Kyiv(15, 20, 30));

        var text = ScheduleText.OnMessage("м. Київ", "45.1", status, Kyiv(15, 11), Kyiv(15, 14));

        Assert.DoesNotContain("є вже", text);
        Assert.Contains("🕓 Світла не було 3 год", text);
    }

    [Fact]
    public void OnMessage_NoOutageAnnounced_IsPlainPowerIsOn()
    {
        var status = new ScheduleStatus(false, null, Kyiv(15, 11), null, Kyiv(15, 11));

        var text = ScheduleText.OnMessage("м. Київ", "45.1", status, null, Kyiv(15, 9));

        Assert.Equal(
            "🟢 Світло є за графіком\nм. Київ · черга 45.1\n🗓 Наступне відключення за графіком о 11:00",
            text);
        Assert.DoesNotContain("не було", text);
    }

    [Fact]
    public void OnMessage_NoOutageAnnounced_ButOnForAWhile_ShowsElapsed_WithoutTheClock()
    {
        var status = new ScheduleStatus(false, Kyiv(15, 14), Kyiv(15, 20, 30), Kyiv(15, 14), Kyiv(15, 20, 30));

        var text = ScheduleText.OnMessage("м. Київ", "45.1", status, null, Kyiv(15, 15));

        Assert.StartsWith("🟢 Світло є за графіком\n", text);
        Assert.Contains("🕓 Світло є вже 1 год", text);
        Assert.DoesNotContain("не було", text);
    }

    [Fact]
    public void OnMessage_NoMoreOutages_SaysSo()
    {
        var status = new ScheduleStatus(false, Kyiv(16, 20, 30), null, Kyiv(16, 20, 30), null);

        var text = ScheduleText.OnMessage("м. Київ", "45.1", status, null, Kyiv(16, 22));

        Assert.EndsWith("🗓 Наступних відключень за графіком поки немає", text);
    }

    [Fact]
    public void OnMessage_NextOutageTomorrow_ShowsTheDate()
    {
        var status = new ScheduleStatus(false, Kyiv(15, 20), Kyiv(16, 6), Kyiv(15, 20), Kyiv(16, 6));

        var text = ScheduleText.OnMessage("м. Київ", "45.1", status, null, Kyiv(15, 22));

        Assert.EndsWith("🗓 Наступне відключення за графіком о 06:00 (16.10)", text);
    }

    [Fact]
    public void OnMessage_AnnouncedOutageButUnknownRestoreTime_HasNoClockOrDuration()
    {
        var status = new ScheduleStatus(false, null, Kyiv(15, 20), null, Kyiv(15, 20));

        var text = ScheduleText.OnMessage("м. Київ", "45.1", status, Kyiv(15, 11), Kyiv(15, 12));

        Assert.StartsWith("🟢 Світло повернулось за графіком\n", text);
        Assert.DoesNotContain("не було", text);
    }

    // ---- StabilizationNotice ----

    [Fact]
    public void StabilizationNotice_FullStructure()
    {
        var status = new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), null, Kyiv(15, 20, 30));

        var text = ScheduleText.StabilizationNotice("45.1", status, "15.10.2026 20:24", Noon);

        Assert.Equal(
            "🗓 За графіком (черга 45.1) зараз планове стабілізаційне відключення з 11:00.\n"
            + "Світло очікується о 14:00.\n"
            + "Перевірку за адресою на сайті ДТЕК не запускаю.\n"
            + "\n"
            + "Графік оновлено: 15.10.2026 20:24 (за даними ДТЕК)",
            text);
    }

    [Fact]
    public void StabilizationNotice_EveningOutage_ShowsReturnOnTheNextDay()
    {
        var status = new ScheduleStatus(true, Kyiv(15, 20, 30), Kyiv(16, 0), Kyiv(15, 14), Kyiv(16, 6));

        var text = ScheduleText.StabilizationNotice("45.1", status, "15.10.2026 20:24", Kyiv(15, 21));

        Assert.Equal(
            "🗓 За графіком (черга 45.1) зараз планове стабілізаційне відключення з 20:30.\n"
            + "Світло очікується о 00:00 (16.10).\n"
            + "Перевірку за адресою на сайті ДТЕК не запускаю.\n"
            + "\n"
            + "Графік оновлено: 15.10.2026 20:24 (за даними ДТЕК)",
            text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void StabilizationNotice_WithoutSourceUpdate_HasNoUpdatedLine(string? updated)
    {
        var status = new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), null, null);

        var text = ScheduleText.StabilizationNotice("45.1", status, updated, Noon);

        Assert.DoesNotContain("Графік оновлено", text);
        Assert.False(text.EndsWith('\n'));
        Assert.Equal(3, text.Split('\n').Length);
    }

    [Fact]
    public void StabilizationNotice_UnknownBounds_UsesFallbackWording()
    {
        var status = new ScheduleStatus(true, null, null, null, null);

        var text = ScheduleText.StabilizationNotice("45.1", status, null, Noon);

        Assert.StartsWith("🗓 За графіком (черга 45.1) зараз планове стабілізаційне відключення.\n", text);
        Assert.Contains("Час повернення за графіком поки невідомий.", text);
    }

    // ---- never self-triggering ----

    [Fact]
    public void NoScheduleText_MatchesThePowerOutTriggerPattern()
    {
        var statuses = new[]
        {
            new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), null, Kyiv(15, 20, 30)),
            new ScheduleStatus(true, null, null, null, null),
            new ScheduleStatus(false, Kyiv(15, 14), Kyiv(15, 20, 30), Kyiv(15, 14), Kyiv(15, 20, 30)),
            new ScheduleStatus(false, null, null, null, null),
        };
        var texts = new List<string>();
        foreach (var status in statuses)
        {
            texts.Add(ScheduleText.OffMessage("м. Київ", "45.1", status, Noon));
            texts.Add(ScheduleText.OnMessage("м. Київ", "45.1", status, null, Kyiv(15, 15)));
            texts.Add(ScheduleText.OnMessage("м. Київ", "45.1", status, Kyiv(15, 11), Kyiv(15, 15)));
            texts.Add(ScheduleText.StabilizationNotice("45.1", status, "15.10.2026 20:24", Noon));
            texts.Add(ScheduleText.StabilizationNotice("45.1", status, null, Noon));
        }

        Assert.All(texts, t => Assert.DoesNotMatch(PowerOutPattern, t));
        Assert.Matches(PowerOutPattern, "🔴 14:24 Світло зникло");
    }
}
