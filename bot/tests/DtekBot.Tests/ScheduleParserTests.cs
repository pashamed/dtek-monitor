using DtekBot.Api.Schedules;
using static DtekBot.Tests.ScheduleFixtures;

namespace DtekBot.Tests;

public class ScheduleParserTests
{
    private static readonly ScheduleSnapshot Real = RealSnapshot();

    private static OutageInterval Interval(DateTimeOffset start, DateTimeOffset end) => new(start, end);

    // ---- real fixture ----

    [Fact]
    public void Real_GroupIds_AreSortedNaturally()
    {
        Assert.Equal(["1.1", "2.1", "45.1"], Real.GroupIds);
    }

    [Fact]
    public void Real_MetadataAndCoverage()
    {
        Assert.Equal(RealVersion, Real.Version);
        Assert.Equal("15.10.2026 20:24", Real.SourceUpdatedText);
        Assert.Equal("kyiv", Real.DataFile);
        Assert.Equal(Kyiv(15, 0), Real.CoverageStart);
        Assert.Equal(Kyiv(17, 0), Real.CoverageEnd);
        Assert.Equal(Kyiv(15, 20, 30), Real.FetchedAt);
    }

    [Fact]
    public void Real_Group45_1_HasTheExpectedOutages()
    {
        var outages = Real.Find("45.1")!.Outages;

        Assert.Equal(
            [
                Interval(Kyiv(15, 11), Kyiv(15, 14)),
                Interval(Kyiv(15, 20, 30), Kyiv(16, 0)),
                Interval(Kyiv(16, 6), Kyiv(16, 9, 30)),
                Interval(Kyiv(16, 17), Kyiv(16, 20, 30)),
            ],
            outages);
    }

    [Fact]
    public void Real_Group45_1_IntervalsAreUtcInstantsOfTheKyivTimes()
    {
        var first = Real.Find("45.1")!.Outages[0];

        Assert.Equal(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero), first.Start);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 11, 0, 0, TimeSpan.Zero), first.End);
    }

    [Fact]
    public void Real_Outages_AreSortedAndNonOverlapping_ForEveryGroup()
    {
        foreach (var id in Real.GroupIds)
        {
            var outages = Real.Find(id)!.Outages;
            Assert.NotEmpty(outages);
            for (var i = 0; i < outages.Count; i++)
            {
                Assert.True(outages[i].Start < outages[i].End);
                if (i > 0)
                {
                    Assert.True(outages[i - 1].End < outages[i].Start, $"group {id}: pieces were not merged");
                }
            }
        }
    }

    [Fact]
    public void Real_Find_UnknownGroup_IsNull()
    {
        Assert.Null(Real.Find("99.9"));
        Assert.Null(Real.Find("GPV45.1"));
    }

    // ---- invalid input ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{not json")]
    [InlineData("{}")]
    [InlineData("""{"fact":{}}""")]
    [InlineData("""{"fact":{"data":[]}}""")]
    [InlineData("""{"fact":{"data":{}}}""")]
    [InlineData("""{"fact":{"data":{"1792011600":{}}}}""")]
    [InlineData("""{"fact":{"data":{"1792011600":{"X45.1":{"1":"no"}}}}}""")]
    [InlineData("""{"fact":{"data":{"abc":{"GPV45.1":{"1":"no"}}}}}""")]
    public void InvalidOrEmpty_ReturnsNull(string json)
    {
        Assert.Null(Parse(json));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"text\"")]
    [InlineData("""{"fact":"x"}""")]
    [InlineData("""{"fact":{"data":{"1792011600":"x"}}}""")]
    [InlineData("""{"fact":{"data":{"1792011600":{"GPV1.1":[]}}}}""")]
    public void WellFormedJsonOfTheWrongShape_ReturnsNullInsteadOfThrowing(string json)
    {
        Assert.Null(ScheduleParser.Parse(json, "kyiv", Kyiv(15, 12)));
    }

    [Fact]
    public void MissingMeta_GivesEmptyVersion_AndMissingUpdate_GivesNullText()
    {
        var snapshot = Parse(Json(null, null, (Day1, "1.1", Hours("no", 3))))!;

        Assert.Equal("", snapshot.Version);
        Assert.Null(snapshot.SourceUpdatedText);
    }

    [Fact]
    public void VersionAndUpdateText_AreTakenFromMetaAndFact()
    {
        var snapshot = Parse(Json("abc123", "01.01.2026 10:00", (Day1, "1.1", Hours("no", 3))))!;

        Assert.Equal("abc123", snapshot.Version);
        Assert.Equal("01.01.2026 10:00", snapshot.SourceUpdatedText);
    }

    [Fact]
    public void DataFile_AndFetchedAt_AreCarriedOver()
    {
        var fetched = Kyiv(15, 7, 7);

        var snapshot = Parse(Json("v", "u", (Day1, "1.1", Hours("no", 3))), "kyiv-region", fetched)!;

        Assert.Equal("kyiv-region", snapshot.DataFile);
        Assert.Equal(fetched, snapshot.FetchedAt);
    }

    // ---- value semantics ----

    [Fact]
    public void HourKey_CoversThePrecedingHour()
    {
        var snapshot = Parse(Json("v", "u", (Day1, "1.1", Hours("no", 1))))!;

        Assert.Equal([Interval(Kyiv(15, 0), Kyiv(15, 1))], snapshot.Find("1.1")!.Outages);
    }

    [Fact]
    public void Hour24_EndsAtTheNextMidnight()
    {
        var snapshot = Parse(Json("v", "u", (Day1, "1.1", Hours("no", 24))))!;

        Assert.Equal([Interval(Kyiv(15, 23), Kyiv(16, 0))], snapshot.Find("1.1")!.Outages);
    }

    [Fact]
    public void FirstAndSecond_AreHalfHours()
    {
        var snapshot = Parse(Json("v", "u",
            (Day1, "1.1", [(5, "first"), (9, "second")])))!;

        Assert.Equal(
            [Interval(Kyiv(15, 4), Kyiv(15, 4, 30)), Interval(Kyiv(15, 8, 30), Kyiv(15, 9))],
            snapshot.Find("1.1")!.Outages);
    }

    [Fact]
    public void HalfHourPieces_ThatDoNotTouch_StayApart()
    {
        var snapshot = Parse(Json("v", "u",
            (Day1, "1.1", [(3, "first"), (4, "no"), (5, "second")])))!;

        Assert.Equal(
            [Interval(Kyiv(15, 2), Kyiv(15, 2, 30)), Interval(Kyiv(15, 3), Kyiv(15, 4)), Interval(Kyiv(15, 4, 30), Kyiv(15, 5))],
            snapshot.Find("1.1")!.Outages);
    }

    [Fact]
    public void SecondFollowedByNo_Merge_IntoOneOutage()
    {
        var snapshot = Parse(Json("v", "u",
            (Day1, "1.1", [(3, "second"), (4, "no"), (5, "first")])))!;

        Assert.Equal([Interval(Kyiv(15, 2, 30), Kyiv(15, 4, 30))], snapshot.Find("1.1")!.Outages);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("maybe")]
    [InlineData("mfirst")]
    [InlineData("msecond")]
    [InlineData("")]
    [InlineData("whatever")]
    public void OnAndMaybeValues_AreNotOutages(string value)
    {
        var snapshot = Parse(Json("v", "u",
            (Day1, "1.1", [(3, value), (4, value)]),
            (Day1, "2.1", Hours("no", 1))))!;

        Assert.Empty(snapshot.Find("1.1")!.Outages);
        Assert.Single(snapshot.Find("2.1")!.Outages);
    }

    [Fact]
    public void UnknownHourKeys_AreIgnored()
    {
        var json = """
            {"fact":{"data":{"1792011600":{"GPV1.1":{"0":"no","25":"no","-1":"no","abc":"no","3":"no"}}}}}
            """;

        var snapshot = Parse(json)!;

        Assert.Equal([Interval(Kyiv(15, 2), Kyiv(15, 3))], snapshot.Find("1.1")!.Outages);
    }

    [Fact]
    public void NonGroupKeys_AndNonNumericDayKeys_AreIgnored()
    {
        var json = """
            {"fact":{"data":{
              "1792011600":{"GPV1.1":{"3":"no"},"OTHER":{"3":"no"},"gpv2.1":{"3":"no"}},
              "tomorrow":{"GPV9.9":{"3":"no"}}}}}
            """;

        var snapshot = Parse(json)!;

        Assert.Equal(["1.1"], snapshot.GroupIds);
        Assert.Equal(Kyiv(15, 0), snapshot.CoverageStart);
        Assert.Equal(Kyiv(16, 0), snapshot.CoverageEnd);
    }

    [Fact]
    public void Outage_SpanningMidnight_IsOneInterval()
    {
        var snapshot = Parse(Json("v", "u",
            (Day1, "1.1", Hours("no", 22, 23, 24)),
            (Day2, "1.1", Hours("no", 1, 2, 3))))!;

        Assert.Equal([Interval(Kyiv(15, 21), Kyiv(16, 3))], snapshot.Find("1.1")!.Outages);
    }

    [Fact]
    public void Outage_AcrossMidnight_WithHalfHours_Merges()
    {
        var snapshot = Parse(Json("v", "u",
            (Day1, "1.1", [(24, "no")]),
            (Day2, "1.1", [(1, "first")])))!;

        Assert.Equal([Interval(Kyiv(15, 23), Kyiv(16, 0, 30))], snapshot.Find("1.1")!.Outages);
    }

    [Fact]
    public void Outages_Separated_ByASecondHalfHour_StayApart()
    {
        // "first" at hour 1 is 00:00-00:30, "second" at hour 2 is 01:30-02:00: a gap in between.
        var snapshot = Parse(Json("v", "u", (Day1, "1.1", [(1, "first"), (2, "second")])))!;

        Assert.Equal(2, snapshot.Find("1.1")!.Outages.Count);
    }

    [Fact]
    public void Days_ProvidedOutOfOrder_AreStillMergedInOrder()
    {
        var json = Json("v", "u",
            (Day2, "1.1", Hours("no", 1)),
            (Day1, "1.1", Hours("no", 24)));

        var snapshot = Parse(json)!;

        Assert.Equal([Interval(Kyiv(15, 23), Kyiv(16, 1))], snapshot.Find("1.1")!.Outages);
        Assert.Equal(Kyiv(15, 0), snapshot.CoverageStart);
        Assert.Equal(Kyiv(17, 0), snapshot.CoverageEnd);
    }

    [Fact]
    public void Group_PresentOnlyOnOneDay_StillExists()
    {
        var snapshot = Parse(Json("v", "u",
            (Day1, "1.1", Hours("no", 3)),
            (Day2, "2.1", Hours("no", 3))))!;

        Assert.Equal(["1.1", "2.1"], snapshot.GroupIds);
    }

    // ---- StatusAt: real fixture, group 45.1 ----

    private ScheduleStatus? Status(DateTimeOffset at, string group = "45.1") => Real.StatusAt(group, at);

    [Fact]
    public void StatusAt_BeforeCoverage_IsNull()
    {
        Assert.Null(Status(Kyiv(14, 23, 59, 59)));
        Assert.Null(Status(Kyiv(10, 12)));
    }

    [Fact]
    public void StatusAt_AtCoverageEnd_AndAfter_IsNull()
    {
        Assert.NotNull(Status(Kyiv(16, 23, 59, 59)));
        Assert.Null(Status(Kyiv(17, 0)));
        Assert.Null(Status(Kyiv(20, 12)));
    }

    [Fact]
    public void StatusAt_UnknownGroup_IsNull()
    {
        Assert.Null(Status(Kyiv(15, 12), "99.9"));
        Assert.Null(Status(Kyiv(15, 12), ""));
    }

    [Fact]
    public void StatusAt_CoverageStart_IsInclusive_AndOn()
    {
        var status = Status(Kyiv(15, 0))!;

        Assert.Equal(new ScheduleStatus(false, null, Kyiv(15, 11), null, Kyiv(15, 11)), status);
    }

    [Fact]
    public void StatusAt_JustBeforeFirstOutage_IsOn()
    {
        var status = Status(Kyiv(15, 10, 59, 59))!;

        Assert.False(status.IsOff);
        Assert.Equal(Kyiv(15, 11), status.NextStart);
    }

    [Fact]
    public void StatusAt_OutageStart_IsInclusive()
    {
        var status = Status(Kyiv(15, 11))!;

        Assert.Equal(new ScheduleStatus(true, Kyiv(15, 11), Kyiv(15, 14), null, Kyiv(15, 20, 30)), status);
    }

    [Fact]
    public void StatusAt_InsideOutage_IsOff_WithBounds()
    {
        var status = Status(Kyiv(15, 12))!;

        Assert.True(status.IsOff);
        Assert.Equal(Kyiv(15, 11), status.Since);
        Assert.Equal(Kyiv(15, 14), status.Until);
        Assert.Null(status.PreviousEnd);
        Assert.Equal(Kyiv(15, 20, 30), status.NextStart);
    }

    [Fact]
    public void StatusAt_JustBeforeOutageEnd_IsStillOff()
    {
        Assert.True(Status(Kyiv(15, 13, 59, 59))!.IsOff);
    }

    [Fact]
    public void StatusAt_OutageEnd_IsExclusive_AndPowerIsOn()
    {
        var status = Status(Kyiv(15, 14))!;

        Assert.Equal(new ScheduleStatus(false, Kyiv(15, 14), Kyiv(15, 20, 30), Kyiv(15, 14), Kyiv(15, 20, 30)), status);
    }

    [Fact]
    public void StatusAt_BetweenOutages_SinceIsPreviousEnd_AndUntilIsNextStart()
    {
        var status = Status(Kyiv(15, 17, 45))!;

        Assert.False(status.IsOff);
        Assert.Equal(status.PreviousEnd, status.Since);
        Assert.Equal(status.NextStart, status.Until);
        Assert.Equal(Kyiv(15, 14), status.Since);
        Assert.Equal(Kyiv(15, 20, 30), status.Until);
    }

    [Fact]
    public void StatusAt_HalfHourBoundary_20_29_And_20_30()
    {
        Assert.False(Status(Kyiv(15, 20, 29, 59))!.IsOff);

        var off = Status(Kyiv(15, 20, 30))!;
        Assert.True(off.IsOff);
        Assert.Equal(Kyiv(15, 20, 30), off.Since);
        Assert.Equal(Kyiv(16, 0), off.Until);
        Assert.Equal(Kyiv(15, 14), off.PreviousEnd);
        Assert.Equal(Kyiv(16, 6), off.NextStart);
    }

    [Fact]
    public void StatusAt_EveningOutage_EndingAtMidnight_IsNotAnEdgeOutage()
    {
        // 20:30-24:00 on the 15th ends exactly at midnight, which is inside the data, so Until is known.
        var status = Status(Kyiv(15, 23, 59, 59))!;

        Assert.True(status.IsOff);
        Assert.Equal(Kyiv(16, 0), status.Until);
    }

    [Fact]
    public void StatusAt_Midnight_PowerIsBack()
    {
        var status = Status(Kyiv(16, 0))!;

        Assert.False(status.IsOff);
        Assert.Equal(Kyiv(16, 0), status.Since);
        Assert.Equal(Kyiv(16, 6), status.Until);
    }

    [Fact]
    public void StatusAt_SecondDay_MorningOutage_EndsAt0930()
    {
        var inside = Status(Kyiv(16, 7))!;
        Assert.Equal(new ScheduleStatus(true, Kyiv(16, 6), Kyiv(16, 9, 30), Kyiv(16, 0), Kyiv(16, 17)), inside);

        Assert.True(Status(Kyiv(16, 9, 29, 59))!.IsOff);
        Assert.False(Status(Kyiv(16, 9, 30))!.IsOff);
    }

    [Fact]
    public void StatusAt_AfterLastOutage_HasNoNextStart()
    {
        var status = Status(Kyiv(16, 22))!;

        Assert.False(status.IsOff);
        Assert.Equal(Kyiv(16, 20, 30), status.PreviousEnd);
        Assert.Null(status.NextStart);
        Assert.Null(status.Until);
    }

    [Fact]
    public void StatusAt_WorksWithUtcAndOtherOffsets()
    {
        var utc = Kyiv(15, 12).ToUniversalTime();
        var other = Kyiv(15, 12).ToOffset(TimeSpan.FromHours(-5));

        Assert.True(Status(utc)!.IsOff);
        Assert.True(Status(other)!.IsOff);
    }

    // ---- StatusAt: edges of the data ----

    [Fact]
    public void StatusAt_OutageTouchingCoverageStart_HasNoKnownStart()
    {
        var snapshot = Parse(Json("v", "u", (Day1, "1.1", Hours("no", 1, 2))))!;

        var status = snapshot.StatusAt("1.1", Kyiv(15, 0, 30))!;

        Assert.True(status.IsOff);
        Assert.Null(status.Since);
        Assert.Equal(Kyiv(15, 2), status.Until);
    }

    [Fact]
    public void StatusAt_OutageTouchingCoverageEnd_HasNoKnownEnd()
    {
        var snapshot = Parse(Json("v", "u", (Day1, "1.1", Hours("no", 23, 24))))!;

        var status = snapshot.StatusAt("1.1", Kyiv(15, 23, 30))!;

        Assert.True(status.IsOff);
        Assert.Equal(Kyiv(15, 22), status.Since);
        Assert.Null(status.Until);
    }

    [Fact]
    public void StatusAt_OutageCoveringTheWholeData_HasNeitherBound()
    {
        var snapshot = Parse(Json("v", "u", (Day1, "1.1", Enumerable.Range(1, 24).Select(h => (h, "no")).ToArray())))!;

        var status = snapshot.StatusAt("1.1", Kyiv(15, 12))!;

        Assert.True(status.IsOff);
        Assert.Null(status.Since);
        Assert.Null(status.Until);
    }

    [Fact]
    public void StatusAt_MergedOutageAcrossMidnight_KeepsItsRealStart()
    {
        var snapshot = Parse(Json("v", "u",
            (Day1, "1.1", Hours("no", 22, 23, 24)),
            (Day2, "1.1", Hours("no", 1, 2, 3))))!;

        var before = snapshot.StatusAt("1.1", Kyiv(15, 23, 30))!;
        var after = snapshot.StatusAt("1.1", Kyiv(16, 1, 30))!;

        Assert.Equal(Kyiv(15, 21), before.Since);
        Assert.Equal(Kyiv(16, 3), before.Until);
        Assert.Equal(before, after);
    }

    [Fact]
    public void StatusAt_GroupWithoutOutages_IsAlwaysOn_WithNoBounds()
    {
        var snapshot = Parse(Json("v", "u", (Day1, "1.1", [])))!;

        var status = snapshot.StatusAt("1.1", Kyiv(15, 12))!;

        Assert.Equal(new ScheduleStatus(false, null, null, null, null), status);
    }
}
