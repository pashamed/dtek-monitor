using System.Text.RegularExpressions;
using DtekBot.Api.Schedules;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public class ScheduleTextTests
{
    private static readonly Regex PowerOutPattern =
        new(@"світло\s+зникло", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly ScheduleSettings Settings = new();

    private static readonly ScheduleSnapshot Real = RealSnapshot();

    // ---- Duration (compact) ----

    [Theory]
    [InlineData(0, "<1хв")]
    [InlineData(29, "<1хв")]
    [InlineData(60, "1хв")]
    [InlineData(90, "2хв")]
    [InlineData(45 * 60, "45хв")]
    [InlineData(59 * 60, "59хв")]
    [InlineData(60 * 60, "1год")]
    [InlineData(61 * 60, "1год 1хв")]
    [InlineData(125 * 60, "2год 5хв")]
    [InlineData(120 * 60, "2год")]
    [InlineData(17 * 3600 + 23 * 60, "17год 23хв")]
    [InlineData(3 * 3600 + 42 * 60, "3год 42хв")]
    [InlineData(23 * 3600 + 59 * 60, "23год 59хв")]
    [InlineData(26 * 3600, "26год")]
    public void Duration_IsCompact(int seconds, string expected)
    {
        Assert.Equal(expected, ScheduleText.Duration(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Duration_NegativeSpan_IsLessThanAMinute()
    {
        Assert.Equal("<1хв", ScheduleText.Duration(TimeSpan.FromMinutes(-5)));
    }

    // ---- Approx ----

    [Theory]
    [InlineData(240, "~4 год")]
    [InlineData(210, "~3,5 год")]
    [InlineData(150, "~2,5 год")]
    [InlineData(45, "~45 хв")]
    [InlineData(75, "~1 год 15 хв")]
    [InlineData(90, "~1,5 год")]
    [InlineData(60, "~1 год")]
    [InlineData(0, "~0 хв")]
    [InlineData(395, "~6 год 35 хв")]
    [InlineData(390, "~6,5 год")]
    public void Approx_RoundsToReadableLengths(int minutes, string expected)
    {
        Assert.Equal(expected, ScheduleText.Approx(TimeSpan.FromMinutes(minutes)));
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
    public void ClockWithDay_OtherDay_AppendsTheDateInBrackets()
    {
        Assert.Equal("00:00 (16.10)", ScheduleText.ClockWithDay(Kyiv(16, 0), Kyiv(15, 20)));
        Assert.Equal("06:00 (16.10)", ScheduleText.ClockWithDay(Kyiv(16, 6), Kyiv(15, 12)));
        Assert.Equal("23:00 (14.10)", ScheduleText.ClockWithDay(Kyiv(14, 23), Kyiv(15, 1)));
    }

    [Fact]
    public void ClockWithDay_ComparesKyivDays_NotUtcDays()
    {
        var moment = new DateTimeOffset(2026, 10, 15, 21, 30, 0, TimeSpan.Zero);
        var now = new DateTimeOffset(2026, 10, 15, 20, 0, 0, TimeSpan.Zero);

        Assert.Equal("00:30 (16.10)", ScheduleText.ClockWithDay(moment, now));
        Assert.Equal("06:00", ScheduleText.ClockWithDay(Kyiv(16, 6), new DateTimeOffset(2026, 10, 15, 21, 5, 0, TimeSpan.Zero)));
    }

    // ---- KyivDayStart / DateKey ----

    [Theory]
    [InlineData(2026, 1, 15, 2, 14, 22)]
    [InlineData(2026, 7, 15, 3, 14, 21)]
    [InlineData(2026, 3, 28, 2, 27, 22)]
    [InlineData(2026, 3, 29, 2, 28, 22)]
    [InlineData(2026, 3, 30, 3, 29, 21)]
    [InlineData(2026, 10, 24, 3, 23, 21)]
    [InlineData(2026, 10, 25, 3, 24, 21)]
    [InlineData(2026, 10, 26, 2, 25, 22)]
    public void KyivDayStart_IsKyivMidnight_AcrossDst(int y, int m, int d, int offsetHours, int utcDay, int utcHour)
    {
        var start = ScheduleText.KyivDayStart(new DateTime(y, m, d));

        Assert.Equal(TimeSpan.FromHours(offsetHours), start.Offset);
        Assert.Equal(new DateTimeOffset(y, m, utcDay, utcHour, 0, 0, TimeSpan.Zero), start.ToUniversalTime());
        Assert.Equal("00:00", ScheduleText.Clock(start));
    }

    [Fact]
    public void KyivDayStart_IgnoresTheTimeOfDay()
    {
        Assert.Equal(
            ScheduleText.KyivDayStart(new DateTime(2026, 10, 15)),
            ScheduleText.KyivDayStart(new DateTime(2026, 10, 15, 17, 45, 0)));
    }

    [Fact]
    public void KyivDayStart_DayLengthsFollowDst()
    {
        var spring = ScheduleText.KyivDayStart(new DateTime(2026, 3, 30)) - ScheduleText.KyivDayStart(new DateTime(2026, 3, 29));
        var autumn = ScheduleText.KyivDayStart(new DateTime(2026, 10, 26)) - ScheduleText.KyivDayStart(new DateTime(2026, 10, 25));
        var normal = ScheduleText.KyivDayStart(new DateTime(2026, 7, 16)) - ScheduleText.KyivDayStart(new DateTime(2026, 7, 15));

        Assert.Equal(TimeSpan.FromHours(23), spring);
        Assert.Equal(TimeSpan.FromHours(25), autumn);
        Assert.Equal(TimeSpan.FromHours(24), normal);
    }

    [Theory]
    [InlineData("2026-10-15T09:00:00Z", "2026-10-15")]
    [InlineData("2026-10-15T20:59:59Z", "2026-10-15")]
    [InlineData("2026-10-15T21:00:00Z", "2026-10-16")]
    [InlineData("2026-01-15T21:59:59Z", "2026-01-15")]
    [InlineData("2026-01-15T22:00:00Z", "2026-01-16")]
    public void DateKey_IsTheKyivDate(string utc, string expected)
    {
        Assert.Equal(expected, ScheduleText.DateKey(DateTimeOffset.Parse(utc)));
    }

    // ---- OutageLines ----

    [Fact]
    public void OutageLines_Day1_OfTheRealFixture()
    {
        var lines = ScheduleText.OutageLines(Real.Find("45.1"), Kyiv(15, 0), Kyiv(16, 0));

        Assert.Equal(["🔴 11:00 - 14:00 (~3 год)", "🔴 20:30 - 24:00 (~3,5 год)"], lines);
    }

    [Fact]
    public void OutageLines_Day2_OfTheRealFixture()
    {
        var lines = ScheduleText.OutageLines(Real.Find("45.1"), Kyiv(16, 0), Kyiv(17, 0));

        Assert.Equal(["🔴 06:00 - 09:30 (~3,5 год)", "🔴 17:00 - 20:30 (~3,5 год)"], lines);
    }

    [Fact]
    public void OutageLines_OutageAcrossMidnight_IsClippedToEachDay()
    {
        var snapshot = Parse(Json("v", "u",
            (Day1, "1.1", Hours("no", 22, 23, 24)),
            (Day2, "1.1", Hours("no", 1, 2, 3))))!;
        var group = snapshot.Find("1.1");

        Assert.Equal(["🔴 21:00 - 24:00 (~3 год)"], ScheduleText.OutageLines(group, Kyiv(15, 0), Kyiv(16, 0)));
        Assert.Equal(["🔴 00:00 - 03:00 (~3 год)"], ScheduleText.OutageLines(group, Kyiv(16, 0), Kyiv(17, 0)));
    }

    [Fact]
    public void OutageLines_OutageEndingExactlyAtMidnight_ReadsTwentyFour()
    {
        var snapshot = Parse(Json("v", "u", (Day1, "1.1", Hours("no", 24))))!;

        Assert.Equal(["🔴 23:00 - 24:00 (~1 год)"], ScheduleText.OutageLines(snapshot.Find("1.1"), Kyiv(15, 0), Kyiv(16, 0)));
    }

    [Fact]
    public void OutageLines_OutagesOutsideTheDay_AreSkipped()
    {
        var lines = ScheduleText.OutageLines(Real.Find("45.1"), Kyiv(17, 0), Kyiv(18, 0));

        Assert.Empty(lines);
    }

    [Fact]
    public void OutageLines_NullGroup_IsEmpty()
    {
        Assert.Empty(ScheduleText.OutageLines(null, Kyiv(15, 0), Kyiv(16, 0)));
    }

    [Fact]
    public void OutageLines_ShortOutage_UsesMinutes()
    {
        var snapshot = Parse(Json("v", "u", (Day1, "1.1", [(5, "first")])))!;

        Assert.Equal(["🔴 04:00 - 04:30 (~30 хв)"], ScheduleText.OutageLines(snapshot.Find("1.1"), Kyiv(15, 0), Kyiv(16, 0)));
    }

    [Fact]
    public void OutageLines_OnADstDay_EndsAtTwentyFourOfThatDay()
    {
        var dayStart = ScheduleText.KyivDayStart(new DateTime(2026, 10, 25));
        var dayEnd = ScheduleText.KyivDayStart(new DateTime(2026, 10, 26));
        var group = new GroupSchedule("1.1", [new OutageInterval(dayEnd.AddHours(-2), dayEnd.AddHours(1))]);

        var lines = ScheduleText.OutageLines(group, dayStart, dayEnd);

        Assert.Equal(["🔴 22:00 - 24:00 (~2 год)"], lines);
    }

    // ---- PostValues ----

    [Fact]
    public void PostValues_FirstPostOfTheDay_Real()
    {
        var values = ScheduleText.PostValues("м. Київ", "45.1", Real, Kyiv(15, 12), updated: false);

        Assert.Equal("Графік відключень на сьогодні", values["header"]);
        Assert.Equal("Четвер", values["weekday"]);
        Assert.Equal("15.10.2026", values["date"]);
        Assert.Equal("🔴 11:00 - 14:00 (~3 год)\n🔴 20:30 - 24:00 (~3,5 год)", values["outages"]);
        Assert.Equal("~6,5 год", values["total"]);
        Assert.Equal("🔴 06:00 - 09:30 (~3,5 год)\n🔴 17:00 - 20:30 (~3,5 год)", values["tomorrow"]);
        Assert.Equal("15.10.2026 20:24", values["updated"]);
        Assert.Equal("м. Київ", values["region"]);
        Assert.Equal("45.1", values["group"]);
    }

    [Fact]
    public void PostValues_Updated_UsesTheUpdatedHeading()
    {
        var values = ScheduleText.PostValues("м. Київ", "45.1", Real, Kyiv(15, 12), updated: true);

        Assert.Equal("Оновлений графік відключень на сьогодні", values["header"]);
    }

    [Fact]
    public void PostValues_HasExactlyTheSchedulePlaceholders()
    {
        var values = ScheduleText.PostValues("м. Київ", "45.1", Real, Kyiv(15, 12), false);

        Assert.Equal(
            MessageSlots.Find(MessageSlots.Schedule)!.Placeholders.Select(p => p.Name).OrderBy(n => n),
            values.Keys.OrderBy(n => n));
    }

    [Theory]
    [InlineData(12, "Понеділок")]
    [InlineData(13, "Вівторок")]
    [InlineData(14, "Середа")]
    [InlineData(15, "Четвер")]
    [InlineData(16, "П\u02BCятниця")]
    [InlineData(17, "Субота")]
    [InlineData(18, "Неділя")]
    public void PostValues_WeekdayIsUkrainian(int day, string expected)
    {
        var values = ScheduleText.PostValues("R", "45.1", Real, Kyiv(day, 12), false);

        Assert.Equal(expected, values["weekday"]);
    }

    [Fact]
    public void PostValues_UsesTheKyivDate_NotTheUtcOne()
    {
        var now = new DateTimeOffset(2026, 10, 15, 21, 30, 0, TimeSpan.Zero);

        var values = ScheduleText.PostValues("R", "45.1", Real, now, false);

        Assert.Equal("16.10.2026", values["date"]);
        Assert.Equal("П\u02BCятниця", values["weekday"]);
        Assert.Equal("🔴 06:00 - 09:30 (~3,5 год)\n🔴 17:00 - 20:30 (~3,5 год)", values["outages"]);
    }

    [Fact]
    public void PostValues_TomorrowIsEmpty_WhenTheDataDoesNotCoverIt()
    {
        var values = ScheduleText.PostValues("R", "45.1", Real, Kyiv(16, 12), false);

        Assert.Equal("", values["tomorrow"]);
        Assert.Equal("🔴 06:00 - 09:30 (~3,5 год)\n🔴 17:00 - 20:30 (~3,5 год)", values["outages"]);
    }

    [Fact]
    public void PostValues_NoOutages_SaysNothingIsPlanned_AndHasNoTotal()
    {
        var snapshot = Parse(Json("v", "u", (Day1, "45.1", []), (Day2, "45.1", [])))!;

        var values = ScheduleText.PostValues("R", "45.1", snapshot, Kyiv(15, 12), false);

        Assert.Equal("🟢 Відключень не заплановано", values["outages"]);
        Assert.Equal(ScheduleText.NoOutages, values["outages"]);
        Assert.Equal("", values["total"]);
        Assert.Equal("🟢 Відключень не заплановано", values["tomorrow"]);
    }

    [Fact]
    public void PostValues_TomorrowWithOutages_WhileTodayHasNone()
    {
        var snapshot = Parse(Json("v", "u", (Day1, "45.1", []), (Day2, "45.1", Hours("no", 5))))!;

        var values = ScheduleText.PostValues("R", "45.1", snapshot, Kyiv(15, 12), false);

        Assert.Equal(ScheduleText.NoOutages, values["outages"]);
        Assert.Equal("🔴 04:00 - 05:00 (~1 год)", values["tomorrow"]);
    }

    [Fact]
    public void PostValues_UnknownGroup_HasNoOutages()
    {
        var values = ScheduleText.PostValues("R", "99.9", Real, Kyiv(15, 12), false);

        Assert.Equal(ScheduleText.NoOutages, values["outages"]);
        Assert.Equal("", values["total"]);
    }

    [Fact]
    public void PostValues_MissingUpdateText_IsEmpty()
    {
        var snapshot = Real with { SourceUpdatedText = null };

        Assert.Equal("", ScheduleText.PostValues("R", "45.1", snapshot, Kyiv(15, 12), false)["updated"]);
    }

    [Fact]
    public void PostValues_OnADstDay_UsesTheLocalDate()
    {
        // 25 Oct 2026, 23:30 Kyiv (+02:00 after the clocks went back).
        var now = new DateTimeOffset(2026, 10, 25, 23, 30, 0, TimeSpan.FromHours(2));

        var values = ScheduleText.PostValues("R", "45.1", Real, now, false);

        Assert.Equal("25.10.2026", values["date"]);
        Assert.Equal("Неділя", values["weekday"]);
    }

    [Fact]
    public void PostFingerprint_CoversOutagesAndTomorrow_ButNotTheHeading()
    {
        var first = ScheduleText.PostValues("R", "45.1", Real, Kyiv(15, 12), false);
        var updated = ScheduleText.PostValues("R", "45.1", Real, Kyiv(15, 13), true);

        Assert.Equal(ScheduleText.PostFingerprint(first), ScheduleText.PostFingerprint(updated));
        Assert.Equal(first["outages"] + "|" + first["tomorrow"], ScheduleText.PostFingerprint(first));
    }

    [Fact]
    public void PostFingerprint_ChangesWithTheOutages()
    {
        var changed = Real with
        {
            Groups = new Dictionary<string, GroupSchedule>(Real.Groups)
            {
                ["45.1"] = new GroupSchedule("45.1", [new OutageInterval(Kyiv(15, 11), Kyiv(15, 15))]),
            },
        };

        Assert.NotEqual(
            ScheduleText.PostFingerprint(ScheduleText.PostValues("R", "45.1", Real, Kyiv(15, 12), false)),
            ScheduleText.PostFingerprint(ScheduleText.PostValues("R", "45.1", changed, Kyiv(15, 12), false)));
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
        Assert.DoesNotContain("?", ScheduleText.ImageUrl(Settings, "kyiv", "45.1", "")!);
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
            ScheduleText.ImageUrl(Settings, "kyiv", "45.1", RealVersion, ScheduleImage.Grid),
            ScheduleText.ImageUrl(Settings, "kyiv", "45.1", RealVersion));
    }

    [Fact]
    public void ImageUrl_CardKind_UsesTheSummaryPicture()
    {
        Assert.Equal(
            "https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/images/kyiv/gpv-45-1-summary.png?v=1991457b4408",
            ScheduleText.ImageUrl(Settings, "kyiv", "45.1", RealVersion, ScheduleImage.Card));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(RealVersion)]
    public void ImageUrl_NoneKind_IsNull_Always(string version)
    {
        Assert.Null(ScheduleText.ImageUrl(Settings, "kyiv", "45.1", version, ScheduleImage.None));
    }

    [Theory]
    [InlineData(ScheduleImage.Card, "-summary.png")]
    [InlineData(ScheduleImage.Grid, "-emergency.png")]
    public void ImageUrl_PictureKinds_FollowTheSameVersioningRules(ScheduleImage kind, string suffix)
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

    // ---- OffValues ----

    private static readonly DateTimeOffset Noon = Kyiv(15, 12);

    [Fact]
    public void OffValues_FullStatus()
    {
        var status = new ScheduleStatus(true, Kyiv(15, 20, 30), Kyiv(16, 0), Kyiv(15, 14), Kyiv(16, 6));

        var values = ScheduleText.OffValues("м. Київ", "45.1", status, Kyiv(15, 21));

        Assert.Equal("20:30", values["time"]);
        Assert.Equal("6год 30хв", values["onDuration"]);
        Assert.Equal("00:00 (16.10)", values["until"]);
        Assert.Equal("~3,5 год", values["planned"]);
        Assert.Equal("м. Київ", values["region"]);
        Assert.Equal("45.1", values["group"]);
    }

    [Fact]
    public void OffValues_HasExactlyTheOffPlaceholders()
    {
        var status = new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), null, null);

        var values = ScheduleText.OffValues("R", "45.1", status, Noon);

        Assert.Equal(
            MessageSlots.Find(MessageSlots.Off)!.Placeholders.Select(p => p.Name).OrderBy(n => n),
            values.Keys.OrderBy(n => n));
    }

    [Fact]
    public void OffValues_UnknownStart_UsesNowAsTheTime_AndHasNoDurations()
    {
        var status = new ScheduleStatus(true, null, Kyiv(15, 3), null, null);

        var values = ScheduleText.OffValues("R", "45.1", status, Kyiv(15, 1, 20));

        Assert.Equal("01:20", values["time"]);
        Assert.Equal("", values["onDuration"]);
        Assert.Equal("", values["planned"]);
        Assert.Equal("03:00", values["until"]);
    }

    [Fact]
    public void OffValues_UnknownEnd_HasNoUntilAndNoPlanned()
    {
        var status = new ScheduleStatus(true, Kyiv(16, 22), null, Kyiv(16, 20), null);

        var values = ScheduleText.OffValues("R", "45.1", status, Kyiv(16, 23));

        Assert.Equal("", values["until"]);
        Assert.Equal("", values["planned"]);
        Assert.Equal("2год", values["onDuration"]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    public void OffValues_PreviousEndTooCloseToTheStart_HasNoOnDuration(int secondsBefore)
    {
        var since = Kyiv(15, 11);
        var status = new ScheduleStatus(true, since, Kyiv(15, 14), since.AddSeconds(-secondsBefore), null);

        Assert.Equal("", ScheduleText.OffValues("R", "1.1", status, Noon)["onDuration"]);
    }

    [Fact]
    public void OffValues_OneMinuteOfPower_IsStillReported()
    {
        var since = Kyiv(15, 11);
        var status = new ScheduleStatus(true, since, Kyiv(15, 14), since.AddMinutes(-1), null);

        Assert.Equal("1хв", ScheduleText.OffValues("R", "1.1", status, Noon)["onDuration"]);
    }

    // ---- OnValues ----

    [Fact]
    public void OnValues_FullStatus()
    {
        var status = new ScheduleStatus(false, Kyiv(15, 14), Kyiv(15, 20, 30), Kyiv(15, 14), Kyiv(15, 20, 30));

        var values = ScheduleText.OnValues("м. Київ", "45.1", status, Kyiv(15, 11), Kyiv(15, 14, 5));

        Assert.Equal("14:00", values["time"]);
        Assert.Equal("3год", values["offDuration"]);
        Assert.Equal("20:30", values["next"]);
        Assert.Equal("м. Київ", values["region"]);
        Assert.Equal("45.1", values["group"]);
    }

    [Fact]
    public void OnValues_HasExactlyTheOnPlaceholders()
    {
        var status = new ScheduleStatus(false, Kyiv(15, 14), null, Kyiv(15, 14), null);

        var values = ScheduleText.OnValues("R", "45.1", status, null, Noon);

        Assert.Equal(
            MessageSlots.Find(MessageSlots.On)!.Placeholders.Select(p => p.Name).OrderBy(n => n),
            values.Keys.OrderBy(n => n));
    }

    [Fact]
    public void OnValues_WithoutAnnouncedOutage_HasNoOffDuration()
    {
        var status = new ScheduleStatus(false, Kyiv(15, 14), Kyiv(15, 20, 30), Kyiv(15, 14), Kyiv(15, 20, 30));

        Assert.Equal("", ScheduleText.OnValues("R", "1.1", status, null, Kyiv(15, 15))["offDuration"]);
    }

    [Fact]
    public void OnValues_UnknownRestoreTime_UsesNow()
    {
        var status = new ScheduleStatus(false, null, Kyiv(15, 20), null, Kyiv(15, 20));

        var values = ScheduleText.OnValues("R", "1.1", status, Kyiv(15, 11), Kyiv(15, 12));

        Assert.Equal("12:00", values["time"]);
        Assert.Equal("1год", values["offDuration"]);
    }

    [Fact]
    public void OnValues_NoNextOutage_IsEmpty_AndNextDayHasTheDate()
    {
        var none = new ScheduleStatus(false, Kyiv(16, 20, 30), null, Kyiv(16, 20, 30), null);
        var tomorrow = new ScheduleStatus(false, Kyiv(15, 20), Kyiv(16, 6), Kyiv(15, 20), Kyiv(16, 6));

        Assert.Equal("", ScheduleText.OnValues("R", "1.1", none, null, Kyiv(16, 22))["next"]);
        Assert.Equal("06:00 (16.10)", ScheduleText.OnValues("R", "1.1", tomorrow, null, Kyiv(15, 22))["next"]);
    }

    [Fact]
    public void OnValues_OutageShorterThanAMinute_HasNoOffDuration()
    {
        var status = new ScheduleStatus(false, Kyiv(15, 14), null, Kyiv(15, 14), null);

        Assert.Equal("", ScheduleText.OnValues("R", "1.1", status, Kyiv(15, 14).AddSeconds(-30), Noon)["offDuration"]);
    }

    // ---- OffMessage / OnMessage defaults ----

    [Fact]
    public void OffMessage_Default_ProducesTheSampleWording()
    {
        var since = Kyiv(15, 14, 24);
        var status = new ScheduleStatus(true, since, Kyiv(15, 18), since - new TimeSpan(17, 23, 0), null);

        var text = ScheduleText.OffMessage("м. Київ", "45.1", status, Kyiv(15, 14, 30));

        Assert.Equal("🔴 14:24 Світло вимкнено за графіком\n🕓 Воно було 17год 23хв\n🗓 Очікуємо за графіком о 18:00", text);
    }

    [Fact]
    public void OffMessage_Default_FirstOutageOfTheDay_HasNoDurationLine()
    {
        var status = new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), null, Kyiv(15, 20, 30));

        Assert.Equal(
            "🔴 11:00 Світло вимкнено за графіком\n🗓 Очікуємо за графіком о 14:00",
            ScheduleText.OffMessage("м. Київ", "45.1", status, Noon));
    }

    [Fact]
    public void OffMessage_Default_WithPreviousEndAndNextDayReturn()
    {
        var status = new ScheduleStatus(true, Kyiv(15, 20, 30), Kyiv(16, 0), Kyiv(15, 14), Kyiv(16, 6));

        Assert.Equal(
            "🔴 20:30 Світло вимкнено за графіком\n🕓 Воно було 6год 30хв\n🗓 Очікуємо за графіком о 00:00 (16.10)",
            ScheduleText.OffMessage("м. Київ", "45.1", status, Kyiv(15, 21)));
    }

    [Fact]
    public void OffMessage_Default_UnknownEnd_HasNoReturnLine()
    {
        var status = new ScheduleStatus(true, Kyiv(16, 22), null, Kyiv(16, 20), null);

        Assert.Equal(
            "🔴 22:00 Світло вимкнено за графіком\n🕓 Воно було 2год",
            ScheduleText.OffMessage("R", "45.1", status, Kyiv(16, 23)));
    }

    [Fact]
    public void OnMessage_Default_ProducesTheSampleWording()
    {
        var restored = Kyiv(15, 18, 7);
        var status = new ScheduleStatus(false, restored, null, restored, null);

        var text = ScheduleText.OnMessage("м. Київ", "45.1", status, restored - new TimeSpan(3, 42, 0), Kyiv(15, 18, 10));

        Assert.Equal("🟢 18:07 Світло з'явилося за графіком\n🕓 Його не було 3год 42хв", text);
    }

    [Fact]
    public void OnMessage_Default_WithTheNextOutage()
    {
        var restored = Kyiv(15, 18, 7);
        var status = new ScheduleStatus(false, restored, Kyiv(15, 22), restored, Kyiv(15, 22));

        var text = ScheduleText.OnMessage("м. Київ", "45.1", status, restored - new TimeSpan(3, 42, 0), Kyiv(15, 18, 10));

        Assert.Equal(
            "🟢 18:07 Світло з'явилося за графіком\n🕓 Його не було 3год 42хв\n🗓 Наступне відключення за графіком о 22:00",
            text);
    }

    [Fact]
    public void OnMessage_Default_WithoutAnyOptionalLine()
    {
        var status = new ScheduleStatus(false, Kyiv(15, 18, 7), null, Kyiv(15, 18, 7), null);

        Assert.Equal(
            "🟢 18:07 Світло з'явилося за графіком",
            ScheduleText.OnMessage("R", "1.1", status, null, Kyiv(15, 18, 10)));
    }

    [Fact]
    public void OffAndOnMessages_UseACustomTemplate_WhenGiven()
    {
        var off = new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), null, null);
        var on = new ScheduleStatus(false, Kyiv(15, 14), null, Kyiv(15, 14), null);

        Assert.Equal("11:00|м. Київ|45.1|~3 год", ScheduleText.OffMessage("м. Київ", "45.1", off, Noon, "{time}|{region}|{group}|{planned}"));
        Assert.Equal("14:00|3год", ScheduleText.OnMessage("м. Київ", "45.1", on, Kyiv(15, 11), Kyiv(15, 14, 5), "{time}|{offDuration}"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void OffAndOnMessages_FallBackToTheDefault_ForABlankTemplate(string? template)
    {
        var off = new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), null, null);
        var on = new ScheduleStatus(false, Kyiv(15, 14), null, Kyiv(15, 14), null);

        Assert.Equal(ScheduleText.OffMessage("R", "1.1", off, Noon), ScheduleText.OffMessage("R", "1.1", off, Noon, template));
        Assert.Equal(ScheduleText.OnMessage("R", "1.1", on, null, Noon), ScheduleText.OnMessage("R", "1.1", on, null, Noon, template));
    }

    [Fact]
    public void OffMessage_CustomTemplateWithABlock_OmitsItWhenTheValueIsEmpty()
    {
        var withUntil = new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), null, null);
        var withoutUntil = new ScheduleStatus(true, Kyiv(15, 11), null, null, null);
        const string template = "{time}{#until} до {until}{/until}";

        Assert.Equal("11:00 до 14:00", ScheduleText.OffMessage("R", "1.1", withUntil, Noon, template));
        Assert.Equal("11:00", ScheduleText.OffMessage("R", "1.1", withoutUntil, Noon, template));
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
        }

        var post = ScheduleText.PostValues("м. Київ", "45.1", Real, Noon, false);
        texts.Add(MessageTemplate.Render(null, post, MessageSlots.Find(MessageSlots.Schedule)!.DefaultTemplate));

        Assert.All(texts, t => Assert.DoesNotMatch(PowerOutPattern, t));
        Assert.Matches(PowerOutPattern, "🔴 14:24 Світло зникло");
    }
}
