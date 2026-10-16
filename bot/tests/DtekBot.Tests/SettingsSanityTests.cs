using System.Text.RegularExpressions;
using DtekBot.Api.Settings;

namespace DtekBot.Tests;

// Sanity checks of the settings file that ships with the bot: a typo here silently breaks district matching.
public class SettingsSanityTests
{
    private static readonly DtekSettings Settings = RealSettings.Load();

    private static readonly Regex KebabCase = new("^[a-z]+(-[a-z]+)*$");

    private static RegionSettings Region(string id) => Settings.Regions.Single(r => r.Id == id);

    [Fact]
    public void Regions_AreTheExpectedOnes_InOrder()
    {
        Assert.Equal(
            ["kyiv", "kyiv-oblast", "dnipropetrovsk", "odesa", "donetsk"],
            Settings.Regions.Select(r => r.Id));
    }

    [Theory]
    [InlineData("kyiv", 10)]
    [InlineData("kyiv-oblast", 7)]
    [InlineData("dnipropetrovsk", 7)]
    [InlineData("odesa", 11)]
    [InlineData("donetsk", 6)]
    public void PartCounts_AsExpected(string regionId, int count)
    {
        Assert.Equal(count, Region(regionId).Parts.Count);
    }

    [Fact]
    public void RegionIds_AreUnique_AndKebabCase()
    {
        var ids = Settings.Regions.Select(r => r.Id).ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.Matches(KebabCase, id));
    }

    [Fact]
    public void RegionFields_AreFilledIn()
    {
        foreach (var region in Settings.Regions)
        {
            Assert.False(string.IsNullOrWhiteSpace(region.Name), region.Id);
            Assert.StartsWith("https://", region.SiteUrl);
            Assert.False(string.IsNullOrWhiteSpace(region.GridStatusSlug), region.Id);
            Assert.NotEmpty(region.Aliases);
        }
    }

    [Fact]
    public void PartIds_AreUniqueWithinARegion_AndKebabCase()
    {
        foreach (var region in Settings.Regions)
        {
            var ids = region.Parts.Select(p => p.Id).ToList();

            Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
            Assert.All(ids, id => Assert.Matches(KebabCase, id));
        }
    }

    [Fact]
    public void PartNames_AreFilledIn_AndUniqueWithinARegion()
    {
        foreach (var region in Settings.Regions)
        {
            Assert.All(region.Parts, p => Assert.False(string.IsNullOrWhiteSpace(p.Name), $"{region.Id}/{p.Id}"));
            var names = region.Parts.Select(p => p.Name).ToList();
            Assert.Equal(names.Count, names.Distinct().Count());
        }
    }

    [Fact]
    public void EveryChildId_ExistsInTheSameRegion_AndIsNotTheParentItself()
    {
        foreach (var region in Settings.Regions)
        {
            var known = region.Parts.Select(p => p.Id).ToHashSet();
            foreach (var part in region.Parts)
            {
                Assert.All(part.Children, child =>
                {
                    Assert.Contains(child, known);
                    Assert.NotEqual(part.Id, child);
                });
                Assert.Equal(part.Children.Count, part.Children.Distinct().Count());
            }
        }
    }

    [Fact]
    public void Children_FormNoCycles()
    {
        foreach (var region in Settings.Regions)
        {
            var children = region.Parts.ToDictionary(p => p.Id, p => p.Children);
            foreach (var start in children.Keys)
            {
                var seen = new HashSet<string>();
                var stack = new Stack<string>(children[start]);
                while (stack.Count > 0)
                {
                    var id = stack.Pop();
                    Assert.NotEqual(start, id);
                    if (seen.Add(id))
                    {
                        foreach (var child in children[id])
                        {
                            stack.Push(child);
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void Children_ExactParents_AreOnlyKramatorsk()
    {
        var parents = Settings.Regions
            .SelectMany(r => r.Parts.Where(p => p.Children.Count > 0).Select(p => $"{r.Id}/{p.Id}"))
            .ToList();

        Assert.Equal(["donetsk/kramatorsk"], parents);
        Assert.Equal(3, Region("donetsk").Parts.Single(p => p.Id == "kramatorsk").Children.Count);
    }

    [Fact]
    public void KyivBanks_AreNotParts_ButTheirKeywordsLiveOnTheirDistricts_AndTheOldIdsAreRetired()
    {
        var kyiv = Region("kyiv");
        string[] right = ["holosiivskyi", "obolonskyi", "pecherskyi", "podilskyi", "sviatoshynskyi", "solomianskyi", "shevchenkivskyi"];
        string[] left = ["darnytskyi", "desnianskyi", "dniprovskyi"];

        Assert.DoesNotContain(kyiv.Parts, p => p.Id is "right-bank" or "left-bank");
        Assert.Equal(right, kyiv.RetiredParts["right-bank"]);
        Assert.Equal(left, kyiv.RetiredParts["left-bank"]);
        foreach (var part in kyiv.Parts)
        {
            Assert.Equal(right.Contains(part.Id), part.Aliases.Contains("правобереж"));
            Assert.Equal(left.Contains(part.Id), part.Aliases.Contains("лівобереж"));
        }
    }

    [Fact]
    public void RetiredParts_NeverClashWithCurrentParts_AndPointToExistingOnes()
    {
        foreach (var region in Settings.Regions)
        {
            var known = region.Parts.Select(p => p.Id).ToHashSet();
            foreach (var (retired, replacements) in region.RetiredParts)
            {
                Assert.DoesNotContain(retired, known);
                Assert.All(replacements, id => Assert.Contains(id, known));
            }
        }
    }

    [Fact]
    public void ChildrenOfOneParent_AreNotSharedWithAnotherParent()
    {
        foreach (var region in Settings.Regions)
        {
            var all = region.Parts.SelectMany(p => p.Children).ToList();

            Assert.Equal(all.Count, all.Distinct().Count());
        }
    }

    [Fact]
    public void Aliases_AreLowercase_NonEmpty_AndTrimmed()
    {
        foreach (var region in Settings.Regions)
        {
            Assert.All(region.Aliases, a => AssertAlias(region.Id, a));
            foreach (var part in region.Parts)
            {
                Assert.NotEmpty(part.Aliases);
                Assert.All(part.Aliases, a => AssertAlias($"{region.Id}/{part.Id}", a));
            }
        }

        static void AssertAlias(string owner, string alias)
        {
            Assert.False(string.IsNullOrWhiteSpace(alias), owner);
            Assert.Equal(alias.Trim(), alias);
            Assert.Equal(alias.ToLowerInvariant(), alias);
        }
    }

    [Fact]
    public void Aliases_OfDifferentPartsOfOneRegion_DoNotContainEachOther()
    {
        foreach (var region in Settings.Regions)
        {
            foreach (var a in region.Parts)
            {
                foreach (var b in region.Parts.Where(p => p.Id != a.Id))
                {
                    // Identical aliases are the Kyiv bank keywords that all districts of a bank share on purpose.
                    foreach (var left in a.Aliases)
                    {
                        Assert.DoesNotContain(b.Aliases, right => right != left && right.Contains(left, StringComparison.Ordinal));
                    }
                }
            }
        }
    }

    [Fact]
    public void Classification_ListsAreFilled_AndLowercase()
    {
        var rules = Settings.Classification;

        foreach (var list in new[] { rules.Cancelled, rules.Stabilization, rules.Accident, rules.Emergency })
        {
            Assert.NotEmpty(list);
            Assert.All(list, phrase => Assert.Equal(phrase.ToLowerInvariant(), phrase));
        }
    }

    // ---- data files and Yasno ----

    [Theory]
    [InlineData("kyiv", "kyiv")]
    [InlineData("kyiv-oblast", "kyiv-region")]
    [InlineData("dnipropetrovsk", "dnipro")]
    [InlineData("odesa", "odesa")]
    [InlineData("donetsk", "")]
    public void OutageDataFiles_AsExpected(string regionId, string file)
    {
        var region = Region(regionId);

        Assert.Equal(file, region.OutageDataFile);
        Assert.Equal(file != "", region.HasSchedules);
    }

    [Fact]
    public void OnlyKyiv_HasYasnoGroups()
    {
        Assert.Equal(25, Region("kyiv").YasnoRegionId);
        Assert.Equal(902, Region("kyiv").YasnoDsoId);
        Assert.True(Region("kyiv").HasGroups);
        Assert.All(Settings.Regions.Where(r => r.Id != "kyiv"), r => Assert.False(r.HasGroups));
    }

    [Fact]
    public void KyivRegion_KeepsItsDisplayName_AndSiteUrl()
    {
        Assert.Equal("м. Київ", Region("kyiv").Name);
        Assert.Equal("https://www.dtek-kem.com.ua/ua/shutdowns", Region("kyiv").SiteUrl);
    }

    [Fact]
    public void SchedulesAndAddressChecks_KeepTheirValues()
    {
        Assert.Equal("https://raw.githubusercontent.com/Baskerville42/outage-data-ua/main/", Settings.Schedules.BaseUrl);
        Assert.Equal(5, Settings.Schedules.CacheMinutes);
        Assert.Equal(120, Settings.Schedules.MaxStaleMinutes);

        var checks = Settings.AddressChecks;
        Assert.Equal(10, checks.ChatCooldownMinutes);
        Assert.Equal(20, checks.DailyLimitPerChat);
        Assert.Equal(5, checks.ResultCacheMinutes);
        Assert.Equal(3, checks.DispatchMinIntervalMinutes);
        Assert.Equal(20, checks.MaxStreetsPerRun);
        Assert.Equal(15, checks.RequestTimeoutMinutes);
        Assert.Equal(30, Settings.Polling.CancelRecheckDelaySeconds);
    }
}
