using System.Text.Json;
using DtekBot.Api.Schedules;
using DtekBot.Api.Svitlobot;
using Microsoft.Extensions.Configuration;

namespace DtekBot.Tests;

public sealed class WeeklyPresetTests
{
    private static readonly string Empty = string.Join(';', Enumerable.Repeat(new string('0', 24), 7));

    // Day 1 = Monday; hour key h covers [h-1:00, h:00).
    private static JsonElement Preset(string group, Dictionary<string, Dictionary<string, string>> days)
    {
        var data = new Dictionary<string, object> { ["GPV" + group] = days };
        return JsonSerializer.SerializeToElement(data);
    }

    private static string Row(string timetable, int day) => timetable.Split(';')[day];

    [Fact]
    public void Build_MapsNoFirstSecond_AndEverythingElseToZero()
    {
        var presetData = Preset("45.1", new()
        {
            ["1"] = new() { ["9"] = "no", ["10"] = "no", ["11"] = "first", ["12"] = "second", ["13"] = "maybe", ["14"] = "mfirst", ["15"] = "msecond", ["16"] = "yes" },
        });

        var result = WeeklyPreset.Build(presetData, "45.1")!;

        Assert.Equal("000000001123000000000000", Row(result, 0));
    }

    [Fact]
    public void Build_Is7RowsOf24Digits_MondayFirst_AndMissingDaysAreZero()
    {
        var presetData = Preset("45.1", new()
        {
            ["1"] = new() { ["1"] = "no" },
            ["7"] = new() { ["24"] = "second" },
        });

        var result = WeeklyPreset.Build(presetData, "45.1")!;

        var rows = result.Split(';');
        Assert.Equal(7, rows.Length);
        Assert.All(rows, r => Assert.Equal(24, r.Length));
        Assert.Equal('1', rows[0][0]);
        Assert.Equal('3', rows[6][23]);
        Assert.Equal(new string('0', 24), rows[3]);
        Assert.Equal(7 * 24 + 6, result.Length);
        Assert.True(WeeklyPreset.HasOutages(result));
    }

    [Fact]
    public void Build_WithMaybe_MapsMaybeVariantsLikeFirmOnes_YesStaysZero()
    {
        var presetData = Preset("45.1", new()
        {
            ["1"] = new() { ["1"] = "maybe", ["2"] = "mfirst", ["3"] = "msecond", ["4"] = "no", ["5"] = "first", ["6"] = "second", ["7"] = "yes" },
        });

        Assert.Equal("123123000000000000000000", Row(WeeklyPreset.Build(presetData, "45.1", includeMaybe: true)!, 0));
        Assert.Equal("000123000000000000000000", Row(WeeklyPreset.Build(presetData, "45.1")!, 0));
    }

    [Fact]
    public void Build_UnknownGroup_IsNull()
    {
        Assert.Null(WeeklyPreset.Build(Preset("45.1", new() { ["1"] = new() }), "1.1"));
        Assert.Null(WeeklyPreset.Build(JsonSerializer.SerializeToElement(new { }), "45.1"));
        Assert.Null(WeeklyPreset.Build(Preset("45.1", new()), "45.1"));
    }

    [Fact]
    public void Snapshot_ExposesTheWeekFromThePresetSection_AndNullWithoutIt()
    {
        var withPreset = InjectPreset(ScheduleFixtures.RealJson());

        var snapshot = ScheduleParser.Parse(withPreset, "kyiv", DateTimeOffset.UtcNow)!;

        Assert.Equal("000000001100000000000000", Row(snapshot.WeeklyTimetable("45.1")!, 0));
        Assert.Null(snapshot.WeeklyTimetable("99.9"));
        Assert.Null(ScheduleFixtures.RealSnapshot().WeeklyTimetable("45.1"));
        Assert.NotNull(snapshot.Find("45.1"));
    }

    [Fact]
    public void RealFeedShape_AllMaybe_HasNoOutages()
    {
        Assert.False(WeeklyPreset.HasOutages(Empty));
    }

    private static string InjectPreset(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node["preset"] = System.Text.Json.Nodes.JsonNode.Parse(
            """{"data":{"GPV45.1":{"1":{"9":"no","10":"no"}}}}""");
        return node.ToJsonString();
    }
}

public sealed class SvitlobotKeyProtectorTests
{
    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Range(40, 32).Select(i => (byte)i).ToArray());

    private static SvitlobotKeyProtector With(string? key) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(
            key is null ? [] : new Dictionary<string, string?> { ["Svitlobot:KeyEncryptionKey"] = key }).Build());

    [Fact]
    public void RoundTrip_Works_AndCiphertextIsRandomised()
    {
        var protector = With(KeyA);

        var first = protector.Protect("my-channel-key é")!;
        var second = protector.Protect("my-channel-key é")!;

        Assert.NotEqual(first, second);
        Assert.DoesNotContain("my-channel-key", first);
        Assert.Equal("my-channel-key é", protector.Unprotect(first));
        Assert.Equal("my-channel-key é", protector.Unprotect(second));
        Assert.True(first.Length <= 512);
    }

    [Fact]
    public void TamperedValue_OrAnotherKey_OrGarbage_GivesNull()
    {
        var protector = With(KeyA);
        var stored = protector.Protect("abc")!;
        var bytes = Convert.FromBase64String(stored);
        bytes[^1] ^= 1;

        Assert.Null(protector.Unprotect(Convert.ToBase64String(bytes)));
        Assert.Null(With(KeyB).Unprotect(stored));
        Assert.Null(protector.Unprotect("not base64!"));
        Assert.Null(protector.Unprotect("AAAA"));
        Assert.Null(protector.Unprotect(null));
        Assert.Null(protector.Unprotect(""));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%%%")]
    [InlineData("AAAA")]
    public void MissingOrInvalidSetting_MeansNotConfigured(string? setting)
    {
        var protector = With(setting);

        Assert.False(protector.IsConfigured);
        Assert.Null(protector.Protect("abc"));
        Assert.Null(protector.Unprotect(With(KeyA).Protect("abc")));
    }

    [Fact]
    public void ValidSetting_IsConfigured() => Assert.True(With(KeyA).IsConfigured);
}
