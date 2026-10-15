using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;

namespace DtekBot.Tests;

public class PopupClassifierDistrictTests
{
    private static readonly PopupClassifier Classifier = RealSettings.Classifier();

    private const string RealObolastPopup =
        "Шановні клієнти! Через атаку на енергообʼєкти на Київщині зафіксовані аварійні відключення у Обухівському, Фастівському і Бучанському районах. "
        + "Енергетики вже працюють, щоб якомога швидше покращити ситуацію і повернути світло.";

    private static string Ids(string region, string text, StructuredPopup? structured = null) =>
        string.Join(",", Classifier.Classify(region, text, structured).PartIds.OrderBy(i => i, StringComparer.Ordinal));

    private static string Sorted(string csv) =>
        string.Join(",", csv.Split(',', StringSplitOptions.RemoveEmptyEntries).OrderBy(i => i, StringComparer.Ordinal));

    private const string RightBankAll =
        "right-bank,holosiivskyi,obolonskyi,pecherskyi,podilskyi,sviatoshynskyi,solomianskyi,shevchenkivskyi";

    private const string LeftBankAll = "left-bank,darnytskyi,desnianskyi,dniprovskyi";

    // ---- the real kyiv-oblast popup ----

    [Fact]
    public void RealObolastPopup_IsAnAccident_ForExactlyThreeDistricts_InSettingsOrder()
    {
        var result = Classifier.Classify("kyiv-oblast", RealObolastPopup);

        Assert.Equal(PopupKind.Accident, result.Kind);
        Assert.True(result.IsAlert);
        Assert.Equal(["bucha", "obukhiv", "fastiv"], result.PartIds);
        Assert.DoesNotContain("brovary", result.PartIds);
        Assert.DoesNotContain("boryspil", result.PartIds);
        Assert.DoesNotContain("vyshhorod", result.PartIds);
        Assert.DoesNotContain("bila-tserkva", result.PartIds);
    }

    // ---- keyword matching per region ----

    [Theory]
    // kyiv
    [InlineData("kyiv", "Через аварію в мережі знеструмлено частину лівого берега столиці.", LeftBankAll)]
    [InlineData("kyiv", "Через аварію в мережі знеструмлено частину правого берега столиці.", RightBankAll)]
    [InlineData("kyiv", "Через аварію знеструмлено споживачів у Дарницькому районі.", "darnytskyi")]
    [InlineData("kyiv", "Через аварію знеструмлено Оболонський та Подільський райони.", "obolonskyi,podilskyi")]
    [InlineData("kyiv", "Через аварію знеструмлено Печерський, Шевченківський і Солом'янський райони.", "pecherskyi,shevchenkivskyi,solomianskyi")]
    [InlineData("kyiv", "Через аварію знеструмлено споживачів у Солом’янському районі.", "solomianskyi")]
    [InlineData("kyiv", "Через аварію знеструмлено споживачів у Соломянському районі.", "solomianskyi")]
    [InlineData("kyiv", "Екстрені відключення електроенергії в Києві. Графіки відключень при цьому не діють.", "")]
    [InlineData("kyiv", "Через аварію на підстанції без світла Святошинський район і частина Голосіївського району.", "sviatoshynskyi,holosiivskyi")]
    [InlineData("kyiv", "Через аварію знеструмлено Деснянський район та Дніпровський район.", "desnianskyi,dniprovskyi")]
    [InlineData("kyiv", "Через аварію знеструмлено частину лівого та правого берегів столиці.", RightBankAll + "," + LeftBankAll)]
    [InlineData("kyiv", "Через аварію знеструмлено лівий берег та Печерський район.", LeftBankAll + ",pecherskyi")]
    [InlineData("kyiv", "Через аварію в мережі виникли проблеми з електропостачанням у столиці.", "")]
    [InlineData("kyiv", "Через аварію знеструмлено частину правого берега: Святошинський і Голосіївський райони.", RightBankAll)]
    [InlineData("kyiv", "ДТЕК Київські електричні мережі повідомляють про аварію.", "")]
    [InlineData("kyiv", "Через аварію знеструмлено споживачів Дніпровського району.", "dniprovskyi")]
    [InlineData("kyiv", "Через аварію знеструмлено правобережні райони столиці.", RightBankAll)]
    [InlineData("kyiv", "Через аварію знеструмлено лівобережжя.", LeftBankAll)]
    [InlineData("kyiv", "ЧЕРЕЗ АВАРІЮ ЗНЕСТРУМЛЕНО ДАРНИЦЬКИЙ РАЙОН", "darnytskyi")]
    // kyiv-oblast
    [InlineData("kyiv-oblast", RealObolastPopup, "bucha,obukhiv,fastiv")]
    [InlineData("kyiv-oblast", "Через аварію знеструмлено Броварський район.", "brovary")]
    [InlineData("kyiv-oblast", "Через аварію знеструмлено споживачів Білоцерківського і Бориспільського районів.", "bila-tserkva,boryspil")]
    [InlineData("kyiv-oblast", "Екстрені відключення на Київщині. Графіки відключень при цьому не діють.", "")]
    [InlineData("kyiv-oblast", "Через атаку на Київщині знеструмлено частину Вишгородського району.", "vyshhorod")]
    [InlineData("kyiv-oblast", "Через аварію на Київщині знеструмлено частину споживачів.", "")]
    [InlineData("kyiv-oblast", "Аварійні відключення в Бучанському, Вишгородському, Броварському та Бориспільському районах.", "bucha,vyshhorod,brovary,boryspil")]
    [InlineData("kyiv-oblast", "Через аварію знеструмлено частину Бучанського району.", "bucha")]
    [InlineData("kyiv-oblast", "Через аварію знеструмлено споживачів у Фастівському районі.", "fastiv")]
    [InlineData("kyiv-oblast", "Через аварію знеструмлено Вишгородський, Бучанський та Обухівський райони.", "vyshhorod,bucha,obukhiv")]
    [InlineData("kyiv-oblast", "ДТЕК Київські регіональні електромережі повідомляють про аварію.", "")]
    [InlineData("kyiv-oblast", "Через аварію знеструмлено Білоцерківський район.", "bila-tserkva")]
    [InlineData("kyiv-oblast", "Через аварію частково знеструмлено Бориспільський район, Київщина.", "boryspil")]
    [InlineData("kyiv-oblast", "За наказом НЕК Укренерго діють стабілізаційні відключення електроенергії.", "")]
    [InlineData("kyiv-oblast", "Через аварію знеструмлено Броварський район і частина Бучанського району.", "brovary,bucha")]
    // dnipropetrovsk
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено частину Дніпровського району.", "dnipro")]
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено споживачів у Кам'янському та Нікопольському районах.", "kamianske,nikopol")]
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено Кривий Ріг.", "kryvyi-rih")]
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено споживачів у Кривому Розі.", "kryvyi-rih")]
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено Павлоградський, Синельниківський та Новомосковський райони.", "pavlohrad,synelnykove,novomoskovsk")]
    [InlineData("dnipropetrovsk", "ДТЕК Дніпровські електромережі повідомляють про аварію.", "")]
    [InlineData("dnipropetrovsk", "ДТЕК Дніпровські електромережі: через аварію знеструмлено Нікопольський район.", "nikopol")]
    [InlineData("dnipropetrovsk", "Екстрені відключення на Дніпропетровщині. Графіки відключень не діють.", "")]
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено Кам’янський район.", "kamianske")]
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено споживачів Дніпровським районом.", "dnipro")]
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено споживачів Кривого Рогу.", "kryvyi-rih")]
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено Павлоградського району.", "pavlohrad")]
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено Нікополь і Новомосковськ.", "nikopol,novomoskovsk")]
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено частину Дніпровської області.", "")]
    // odesa
    [InlineData("odesa", "ДТЕК Одеські електромережі повідомляє про аварію.", "")]
    [InlineData("odesa", "Через аварію в Одеському районі знеструмлено частину споживачів.", "odeskyi")]
    [InlineData("odesa", "Через аварію знеструмлено Ізмаїльський та Болградський райони.", "izmail,bolhrad")]
    [InlineData("odesa", "Через аварію знеструмлено споживачів Білгород-Дністровського району.", "bilhorod-dnistrovskyi")]
    [InlineData("odesa", "Через аварію знеструмлено Березівський і Роздільнянський райони.", "berezivka,rozdilna")]
    [InlineData("odesa", "Через аварію знеструмлено Приморський та Київський райони Одеси.", "prymorskyi,kyivskyi")]
    [InlineData("odesa", "Через аварію знеструмлено Малиновський район та Суворовський район.", "khadzhybeiskyi,peresypskyi")]
    [InlineData("odesa", "Через аварію знеструмлено Подільський район.", "podilsk")]
    [InlineData("odesa", "Екстрені відключення в Одесі та області. Графіки відключень не діють.", "")]
    [InlineData("odesa", "Через аварію знеструмлено Одеську область.", "")]
    [InlineData("odesa", "Через аварію знеструмлено Одеський район, Київський район.", "odeskyi,kyivskyi")]
    [InlineData("odesa", "Через аварію знеструмлено споживачів Одеського району.", "odeskyi")]
    [InlineData("odesa", "Через аварію знеструмлено Ізмаїл та Болград.", "izmail,bolhrad")]
    [InlineData("odesa", "Через аварію знеструмлено Одещину.", "")]
    [InlineData("odesa", "Через аварію знеструмлено Пересипський район Одеси.", "peresypskyi")]
    // donetsk
    [InlineData("donetsk", "Через аварію знеструмлено частину Краматорського району.", "kramatorsk,sloviansk,druzhkivka,kostiantynivka")]
    [InlineData("donetsk", "Через аварію знеструмлено споживачів у Краматорському районі.", "kramatorsk,sloviansk,druzhkivka,kostiantynivka")]
    [InlineData("donetsk", "Через аварію знеструмлено Слов'янськ.", "sloviansk")]
    [InlineData("donetsk", "Через аварію знеструмлено Дружківку та Костянтинівку.", "druzhkivka,kostiantynivka")]
    [InlineData("donetsk", "Через аварію знеструмлено Бахмутський район.", "bakhmut")]
    [InlineData("donetsk", "Через аварію знеструмлено Покровський район.", "pokrovsk")]
    [InlineData("donetsk", "ДТЕК Донецькі електромережі повідомляють про аварію.", "")]
    [InlineData("donetsk", "Екстрені відключення на Донеччині. Графіки відключень при цьому не діють.", "")]
    [InlineData("donetsk", "Через аварію знеструмлено Краматорськ і Слов’янськ.", "kramatorsk,sloviansk,druzhkivka,kostiantynivka")]
    [InlineData("donetsk", "Через аварію знеструмлено Бахмут і Покровськ.", "bakhmut,pokrovsk")]
    [InlineData("donetsk", "Через аварію знеструмлено Бахмутський та Краматорський райони.", "bakhmut,kramatorsk,sloviansk,druzhkivka,kostiantynivka")]
    [InlineData("donetsk", "Через аварію знеструмлено споживачів Покровського району.", "pokrovsk")]
    [InlineData("donetsk", "Через аварію знеструмлено частину Донецької області.", "")]
    public void Keywords_FindExactlyTheNamedDistricts(string region, string text, string expectedCsv)
    {
        Assert.Equal(Sorted(expectedCsv), Ids(region, text));
    }

    [Fact]
    public void Keywords_KyivRightBank_ExpandsToItsChildren_InSettingsOrder()
    {
        var result = Classifier.Classify("kyiv", "Через аварію знеструмлено частину правого берега.");

        Assert.Equal(
            ["right-bank", "holosiivskyi", "obolonskyi", "pecherskyi", "podilskyi", "sviatoshynskyi", "solomianskyi", "shevchenkivskyi"],
            result.PartIds);
    }

    [Fact]
    public void Keywords_KyivLeftBank_ExpandsToItsChildren()
    {
        var result = Classifier.Classify("kyiv", "Через аварію знеструмлено лівого берега.");

        Assert.Equal(["left-bank", "darnytskyi", "desnianskyi", "dniprovskyi"], result.PartIds);
    }

    [Fact]
    public void Keywords_DonetskKramatorsk_ExpandsToItsCities()
    {
        var result = Classifier.Classify("donetsk", "Через аварію знеструмлено споживачів у Краматорському районі.");

        Assert.Equal(["kramatorsk", "sloviansk", "druzhkivka", "kostiantynivka"], result.PartIds);
    }

    [Fact]
    public void Keywords_NoDuplicatesWhenParentAndChildAreBothNamed()
    {
        var ids = Classifier.Classify("kyiv", "Правий берег: Оболонський та Печерський райони, Голосіївський район.").PartIds;

        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Equal(8, ids.Count);
    }

    [Fact]
    public void Keywords_RegionIdIsCaseInsensitive()
    {
        Assert.Equal(
            Ids("kyiv-oblast", RealObolastPopup),
            Ids("KYIV-OBLAST", RealObolastPopup));
    }

    [Fact]
    public void Keywords_UnknownRegion_GivesNoParts()
    {
        var result = Classifier.Classify("atlantis", "Через аварію знеструмлено Бучанський район.");

        Assert.Equal(PopupKind.Accident, result.Kind);
        Assert.Empty(result.PartIds);
    }

    [Fact]
    public void Keywords_DistrictsOfAnotherRegion_AreNotFound()
    {
        Assert.Equal("", Ids("kyiv-oblast", "Через аварію знеструмлено Дарницький район і Нікопольський район."));
        Assert.Equal("", Ids("kyiv", "Через аварію знеструмлено Бучанський район."));
    }

    // ---- known false negatives / positives of the keyword rules ----

    [Theory]
    [InlineData("donetsk", "Через аварію знеструмлено споживачів у Дружківці.", "druzhkivka")]
    [InlineData("donetsk", "Через аварію знеструмлено споживачів у Костянтинівці.", "kostiantynivka")]
    [InlineData("donetsk", "Через аварію знеструмлено Костянтинівський район.", "kostiantynivka")]
    [InlineData("dnipropetrovsk", "Через аварію знеструмлено споживачів у Нікополі.", "nikopol")]
    public void Keywords_CityNamesInTheLocativeAndAdjectiveForms_AreRecognised(string region, string text, string expectedCsv)
    {
        Assert.Equal(Sorted(expectedCsv), Ids(region, text));
    }

    [Fact]
    public void Keywords_ModifierLetterApostrophe_IsRecognisedInDistrictNames()
    {
        // U+02BC is the apostrophe used by the DTEK site itself (see "енергообʼєкти" in the real popup).
        Assert.Equal("solomianskyi", Ids("kyiv", "Через аварію знеструмлено споживачів у Соломʼянському районі."));
        Assert.Equal("kamianske", Ids("dnipropetrovsk", "Через аварію знеструмлено Камʼянський район."));
        Assert.Equal("sloviansk", Ids("donetsk", "Через аварію знеструмлено Словʼянськ."));
    }

    [Fact]
    public void Keywords_AnOtherRegionsNameInPassing_IsNotADistrict()
    {
        // "Київської області" is not the Kyivskyi district of Odesa.
        Assert.Equal("", Ids("odesa", "Через аварію знеструмлено споживачів; бригади з Київської області вже працюють."));
    }

    // ---- kind is independent of districts ----

    [Theory]
    [InlineData("Через аварію знеструмлено Бучанський район.", PopupKind.Accident)]
    [InlineData("Екстрені відключення в Бучанському районі.", PopupKind.Emergency)]
    [InlineData("У Бучанському районі діють стабілізаційні відключення електроенергії.", PopupKind.Stabilization)]
    [InlineData("У Бучанському районі повертаємось до графіків.", PopupKind.Cancelled)]
    [InlineData("Новина для Бучанського району.", PopupKind.Unknown)]
    public void Kind_DoesNotDependOnWhichDistrictsAreNamed(string text, PopupKind kind)
    {
        var result = Classifier.Classify("kyiv-oblast", text);

        Assert.Equal(kind, result.Kind);
        Assert.Equal(["bucha"], result.PartIds);
    }

    // ---- structured popup ----

    [Theory]
    [InlineData("all")]
    [InlineData("ALL")]
    [InlineData("All")]
    public void Structured_ScopeAll_MeansWholeRegion_EvenIfKeywordsMatch(string scope)
    {
        var result = Classifier.Classify("kyiv-oblast", RealObolastPopup, new StructuredPopup(scope, null));

        Assert.Equal(PopupKind.Accident, result.Kind);
        Assert.Empty(result.PartIds);
    }

    [Fact]
    public void Structured_ScopeAll_IgnoresDistrictsList()
    {
        var result = Classifier.Classify("kyiv-oblast", RealObolastPopup, new StructuredPopup("all", ["bucha"]));

        Assert.Empty(result.PartIds);
    }

    [Theory]
    [InlineData("districts")]
    [InlineData("DISTRICTS")]
    [InlineData("Districts")]
    public void Structured_ScopeDistricts_UsesTheGivenIds_InTheGivenOrder(string scope)
    {
        var result = Classifier.Classify(
            "kyiv-oblast", "Через аварію виникли проблеми.", new StructuredPopup(scope, ["fastiv", "bucha"]));

        Assert.Equal(["fastiv", "bucha"], result.PartIds);
    }

    [Fact]
    public void Structured_Districts_OverrideTheKeywordResult()
    {
        var result = Classifier.Classify(
            "kyiv-oblast", RealObolastPopup, new StructuredPopup("districts", ["brovary"]));

        Assert.Equal(["brovary"], result.PartIds);
    }

    [Fact]
    public void Structured_UnknownIdsAreDropped_AndDuplicatesRemoved()
    {
        var result = Classifier.Classify(
            "kyiv-oblast",
            "Через аварію виникли проблеми.",
            new StructuredPopup("districts", ["bucha", "atlantis", "bucha", "BUCHA", "fastiv", "", "dnipro"]));

        Assert.Equal(["bucha", "fastiv"], result.PartIds);
    }

    [Fact]
    public void Structured_IdsOfAnotherRegion_AreDropped_SoKeywordsDecide()
    {
        var result = Classifier.Classify(
            "kyiv-oblast", RealObolastPopup, new StructuredPopup("districts", ["darnytskyi", "right-bank"]));

        Assert.Equal(["bucha", "obukhiv", "fastiv"], result.PartIds);
    }

    [Fact]
    public void Structured_Districts_AreExpandedWithChildren()
    {
        var kyiv = Classifier.Classify("kyiv", "Через аварію.", new StructuredPopup("districts", ["right-bank"]));
        var donetsk = Classifier.Classify("donetsk", "Через аварію.", new StructuredPopup("districts", ["kramatorsk"]));

        Assert.Equal(Sorted(RightBankAll), string.Join(",", kyiv.PartIds.OrderBy(i => i, StringComparer.Ordinal)));
        Assert.Equal(["kramatorsk", "sloviansk", "druzhkivka", "kostiantynivka"], donetsk.PartIds);
    }

    [Fact]
    public void Structured_BothBanks_AreExpandedWithoutDuplicates()
    {
        var result = Classifier.Classify(
            "kyiv", "Через аварію.", new StructuredPopup("districts", ["right-bank", "left-bank", "obolonskyi", "left-bank"]));

        Assert.Equal(12, result.PartIds.Count);
        Assert.Equal(12, result.PartIds.Distinct().Count());
    }

    [Fact]
    public void Structured_ChildOnly_IsNotExpandedUpwards()
    {
        var result = Classifier.Classify("kyiv", "Через аварію.", new StructuredPopup("districts", ["obolonskyi"]));

        Assert.Equal(["obolonskyi"], result.PartIds);
    }

    [Theory]
    [InlineData("districts", new string[0])]
    [InlineData("districts", null)]
    [InlineData("districts", new[] { "atlantis", "nowhere" })]
    [InlineData("unknown", new[] { "brovary" })]
    [InlineData("other", new[] { "brovary" })]
    [InlineData("", new[] { "brovary" })]
    [InlineData(null, new[] { "brovary" })]
    [InlineData(null, null)]
    public void Structured_WithoutUsableAnswer_FallsBackToKeywords(string? scope, string[]? districts)
    {
        var result = Classifier.Classify("kyiv-oblast", RealObolastPopup, new StructuredPopup(scope, districts));

        Assert.Equal(["bucha", "obukhiv", "fastiv"], result.PartIds);
    }

    [Fact]
    public void Structured_Null_UsesKeywords()
    {
        var result = Classifier.Classify("kyiv-oblast", RealObolastPopup, null);

        Assert.Equal(["bucha", "obukhiv", "fastiv"], result.PartIds);
    }

    [Fact]
    public void Structured_NeverChangesTheKindOrTheHash()
    {
        var plain = Classifier.Classify("kyiv-oblast", RealObolastPopup);

        foreach (var structured in new[]
        {
            new StructuredPopup("all", null),
            new StructuredPopup("districts", ["brovary"]),
            new StructuredPopup("unknown", null),
            new StructuredPopup(null, null),
        })
        {
            var result = Classifier.Classify("kyiv-oblast", RealObolastPopup, structured);

            Assert.Equal(plain.Kind, result.Kind);
            Assert.Equal(plain.Hash, result.Hash);
            Assert.Equal(plain.NormalizedText, result.NormalizedText);
            Assert.Equal(plain.IsAlert, result.IsAlert);
        }
    }

    [Theory]
    [InlineData("Екстрені відключення в Києві. Графіки відключень при цьому не діють.", PopupKind.Emergency)]
    [InlineData("За наказом НЕК Укренерго діють стабілізаційні відключення електроенергії.", PopupKind.Stabilization)]
    [InlineData("Нове невідоме повідомлення.", PopupKind.Unknown)]
    public void Structured_AllOrDistricts_KeepTheKindDecidedByTheText(string text, PopupKind kind)
    {
        Assert.Equal(kind, Classifier.Classify("kyiv", text, new StructuredPopup("all", null)).Kind);
        Assert.Equal(kind, Classifier.Classify("kyiv", text, new StructuredPopup("districts", ["right-bank"])).Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   <p>&nbsp;</p>  ")]
    public void Structured_WithEmptyText_StillMeansNothingIsShown(string? text)
    {
        var result = Classifier.Classify("kyiv", text, new StructuredPopup("districts", ["right-bank"]));

        Assert.Equal(PopupKind.None, result.Kind);
        Assert.Empty(result.PartIds);
        Assert.False(result.IsAlert);
    }

    [Fact]
    public void Structured_RegionIdIsCaseInsensitive()
    {
        var result = Classifier.Classify("KYIV-OBLAST", "Через аварію.", new StructuredPopup("districts", ["bucha"]));

        Assert.Equal(["bucha"], result.PartIds);
    }

    [Fact]
    public void Structured_ForAnUnknownRegion_GivesNoParts()
    {
        var all = Classifier.Classify("atlantis", "Через аварію.", new StructuredPopup("all", null));
        var districts = Classifier.Classify("atlantis", "Через аварію.", new StructuredPopup("districts", ["bucha"]));

        Assert.Empty(all.PartIds);
        Assert.Empty(districts.PartIds);
    }

    // ---- transitive children and cycles (synthetic settings) ----

    private static PopupClassifier Synthetic(params PartSettings[] parts)
    {
        var real = RealSettings.Load();
        var settings = new DtekSettings
        {
            Classification = real.Classification,
            Regions = [new RegionSettings { Id = "r", Name = "R", Parts = [.. parts] }],
        };
        return RealSettings.Classifier(settings);
    }

    private static PartSettings Part(string id, string alias, params string[] children) =>
        new() { Id = id, Name = id, Aliases = [alias], Children = [.. children] };

    [Fact]
    public void Children_AreExpandedTransitively_ForKeywords()
    {
        var classifier = Synthetic(Part("a", "альфа", "b"), Part("b", "бета", "c"), Part("c", "гама"), Part("d", "дельта"));

        var result = classifier.Classify("r", "Через аварію знеструмлено альфа.");

        Assert.Equal(["a", "b", "c"], result.PartIds);
    }

    [Fact]
    public void Children_AreExpandedTransitively_ForStructured()
    {
        var classifier = Synthetic(Part("a", "альфа", "b"), Part("b", "бета", "c"), Part("c", "гама"), Part("d", "дельта"));

        var result = classifier.Classify("r", "Через аварію.", new StructuredPopup("districts", ["a"]));

        Assert.Equal(["a", "b", "c"], result.PartIds);
    }

    [Fact]
    public void Children_CyclesDoNotLoopForever()
    {
        var classifier = Synthetic(Part("a", "альфа", "b"), Part("b", "бета", "a"));

        var keywords = classifier.Classify("r", "Через аварію знеструмлено альфа.");
        var structured = classifier.Classify("r", "Через аварію.", new StructuredPopup("districts", ["b"]));

        Assert.Equal(["a", "b"], keywords.PartIds.OrderBy(i => i));
        Assert.Equal(["a", "b"], structured.PartIds.OrderBy(i => i));
    }

    [Fact]
    public void Children_ReferencingMissingParts_AreKept_WithoutFailing()
    {
        var classifier = Synthetic(Part("a", "альфа", "ghost"));

        var result = classifier.Classify("r", "Через аварію знеструмлено альфа.");

        Assert.Equal(["a", "ghost"], result.PartIds);
    }

    [Fact]
    public void Children_SharedByTwoParents_AppearOnce()
    {
        var classifier = Synthetic(Part("a", "альфа", "c"), Part("b", "бета", "c"), Part("c", "гама"));

        var result = classifier.Classify("r", "Через аварію знеструмлено альфа і бета.");

        Assert.Equal(["a", "b", "c"], result.PartIds.OrderBy(i => i));
        Assert.Equal(3, result.PartIds.Count);
    }
}
