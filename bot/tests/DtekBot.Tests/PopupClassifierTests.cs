using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace DtekBot.Tests;

public class PopupClassifierTests
{
    private const string OblastStabilization =
        "<h6>Шановні клієнти!</h6><p>За наказом НЕК Укренерго <strong>14.10 з 06:00 до 23:59</strong> діють <strong>стабілізаційні відключення електроенергії</strong>.&nbsp;&nbsp;</p>"
        + "<p>Будь ласка, розумно споживайте електроенергію — це допоможе зменшити навантаження на мережу та уникнути екстрених відключень.&nbsp;&nbsp;</p><p>Дякуємо за ваше розуміння!&nbsp;&nbsp;</p>";

    private const string KyivAccident =
        "<p>Через аварію в мережі <strong>знеструмлено частину правого берега</strong> столиці. </p>"
        + "<p><strong>Стабілізаційні графіки в Києві не діють. </strong><br>Енергетики вже працюють, щоб якомога швидше покращити ситуацію і повернути світло.</p>";

    private const string KyivAccidentBothBanks =
        "<h6>Шановні клієнти!</h6><p>Через аварію в мережі <strong>знеструмлено частину правого та лівого берегів столиці</strong>. </p>"
        + "<p><strong>Стабілізаційні графіки в Києві не діють. </strong><br>Енергетики вже працюють, щоб якомога швидше покращити ситуацію і повернути світло.</p>";

    private const string EmergencyOrder =
        "<p>За наказом НЕК Укренерго введені<strong> екстрені відключення</strong> <strong>електроенергії</strong>.<br>Графіки відключень при цьому не діють. Будь ласка, розумно споживайте електроенергію.</p>";

    private static PopupClassifier CreateClassifier()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "dtek-settings.json");
        var config = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();
        var settings = config.GetSection(DtekSettings.SectionName).Get<DtekSettings>()!;
        return new PopupClassifier(new StaticOptionsMonitor(settings));
    }

    [Fact]
    public void OblastStabilizationPopup_IsNotAnAlert()
    {
        var result = CreateClassifier().Classify("kyiv-oblast", OblastStabilization);

        Assert.Equal(PopupKind.Stabilization, result.Kind);
        Assert.False(result.IsAlert);
    }

    [Fact]
    public void KyivAccidentPopup_BothBanks_IsAccidentForBothParts()
    {
        var result = CreateClassifier().Classify("kyiv", KyivAccidentBothBanks);

        Assert.Equal(PopupKind.Accident, result.Kind);
        Assert.True(result.IsAlert);
        Assert.Equal(12, result.PartIds.Count);
        Assert.Contains("right-bank", result.PartIds);
        Assert.Contains("left-bank", result.PartIds);
        Assert.Contains("darnytskyi", result.PartIds);
        Assert.Contains("obolonskyi", result.PartIds);
    }

    [Fact]
    public void KyivAccidentPopup_IsAccidentScopedToRightBank()
    {
        var result = CreateClassifier().Classify("kyiv", KyivAccident);

        Assert.Equal(PopupKind.Accident, result.Kind);
        Assert.True(result.IsAlert);
        Assert.Equal(
            ["right-bank", "holosiivskyi", "obolonskyi", "pecherskyi", "podilskyi", "sviatoshynskyi", "solomianskyi", "shevchenkivskyi"],
            result.PartIds);
    }

    [Fact]
    public void EmergencyOrderPopup_IsEmergency()
    {
        var result = CreateClassifier().Classify("kyiv-oblast", EmergencyOrder);

        Assert.Equal(PopupKind.Emergency, result.Kind);
        Assert.Empty(result.PartIds);
    }

    [Fact]
    public void CancellationPopup_IsCancelled()
    {
        var result = CreateClassifier().Classify("kyiv", "Київ: повертаємось до графіків, екстрені відключення скасовано");

        Assert.Equal(PopupKind.Cancelled, result.Kind);
        Assert.False(result.IsAlert);
    }

    [Fact]
    public void NegatedStabilizationPhrase_IsNotStabilization()
    {
        var result = CreateClassifier().Classify("kyiv", "Наразі не діють стабілізаційні відключення через аварію.");

        Assert.Equal(PopupKind.Accident, result.Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   <p>&nbsp;</p>  ")]
    public void EmptyPopup_IsNone(string? text)
    {
        var result = CreateClassifier().Classify("kyiv", text);

        Assert.Equal(PopupKind.None, result.Kind);
        Assert.False(result.IsAlert);
    }

    [Fact]
    public void UnrecognisedPopup_IsUnknownAndStillAnAlert()
    {
        var result = CreateClassifier().Classify("kyiv", "Шановні клієнти! Щось нове і незрозуміле.");

        Assert.Equal(PopupKind.Unknown, result.Kind);
        Assert.True(result.IsAlert);
    }

    [Fact]
    public void Hash_IgnoresMarkupAndWhitespace()
    {
        var classifier = CreateClassifier();

        var a = classifier.Classify("kyiv", "<p>Через  аварію</p>\n<p>знеструмлено</p>");
        var b = classifier.Classify("kyiv", "через аварію знеструмлено");

        Assert.Equal(a.Hash, b.Hash);
    }

    private sealed class StaticOptionsMonitor(DtekSettings value) : IOptionsMonitor<DtekSettings>
    {
        public DtekSettings CurrentValue => value;
        public DtekSettings Get(string? name) => value;
        public IDisposable? OnChange(Action<DtekSettings, string?> listener) => null;
    }
}
