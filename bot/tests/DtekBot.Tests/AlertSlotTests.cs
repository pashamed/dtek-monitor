using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Shared;

namespace DtekBot.Tests;

public sealed class AlertSlotTests : IDisposable
{
    private const long Chat = 301;
    private const string Group = "45.1";

    private readonly DispatcherFixture _f = new();

    public void Dispose() => _f.Dispose();

    private static Dictionary<string, string> Map(params (string Slot, string Text)[] items) =>
        items.ToDictionary(i => i.Slot, i => i.Text);

    private Task<DispatchSummary> GroupAsync(bool emergency) =>
        _f.Dispatcher.DispatchGroupAsync(_f.Kyiv, Group, emergency, CancellationToken.None);

    // ---- SlotFor ----

    [Theory]
    [InlineData(AlertKind.Emergency, false, "emergency")]
    [InlineData(AlertKind.Unknown, false, "emergency")]
    [InlineData(AlertKind.Accident, false, "regional")]
    [InlineData(AlertKind.Ended, false, "ended")]
    [InlineData(AlertKind.Emergency, true, "local")]
    [InlineData(AlertKind.Accident, true, "local")]
    [InlineData(AlertKind.Unknown, true, "local")]
    [InlineData(AlertKind.Ended, true, "ended")]
    public void SlotFor_ChoosesTheSlot(AlertKind kind, bool isGroup, string expected)
    {
        Assert.Equal(expected, AlertDispatcher.SlotFor(kind, isGroup));
    }

    [Fact]
    public void SlotFor_NeverReturnsAnUnknownSlot_AndAlwaysAnAlertSlot()
    {
        foreach (var kind in Enum.GetValues<AlertKind>())
        {
            foreach (var isGroup in new[] { false, true })
            {
                var slot = AlertDispatcher.SlotFor(kind, isGroup);
                Assert.NotNull(MessageSlots.Find(slot));
                Assert.True(MessageSlots.IsAlert(slot));
            }
        }
    }

    // ---- region alerts pick their slot's template ----

    private async Task<string> SentTextAsync(string popup, Action<Channel> configure)
    {
        _f.AddChannel(Chat, configure);
        await _f.DispatchAsync(popup);
        return Assert.Single(_f.Bot.Sent).Text;
    }

    [Fact]
    public async Task Emergency_UsesTheEmergencyTemplate_NotTheOthers()
    {
        var text = await SentTextAsync(
            DispatcherFixture.EmergencyText,
            c => c.TemplateMap = Map(("emergency", "EMG {emoji} {message}"), ("regional", "REG {message}"), ("local", "LOC {message}")));

        Assert.StartsWith("EMG 🚨 ", text);
    }

    [Fact]
    public async Task Accident_UsesTheRegionalTemplate()
    {
        var text = await SentTextAsync(
            DispatcherFixture.AccidentRightBankText,
            c => c.TemplateMap = Map(("emergency", "EMG {message}"), ("regional", "REG {emoji} {title}")));

        Assert.Equal("REG ⚠️ Аварія в енергосистемі", text);
    }

    [Fact]
    public async Task Unknown_UsesTheEmergencyTemplate()
    {
        var text = await SentTextAsync(
            DispatcherFixture.UnknownText,
            c => c.TemplateMap = Map(("emergency", "EMG {popup}"), ("regional", "REG {popup}")));

        Assert.StartsWith("EMG Шановні клієнти!", text);
    }

    [Fact]
    public async Task Accident_WithoutARegionalTemplate_UsesTheDefaultLayout_NotTheEmergencyTemplate()
    {
        var text = await SentTextAsync(
            DispatcherFixture.AccidentRightBankText,
            c => c.TemplateMap = Map(("emergency", "EMG {message}")));

        Assert.StartsWith("⚠️ ", text);
        Assert.Contains("Деталі: https://www.dtek-kem.com.ua/ua/shutdowns", text);
        Assert.DoesNotContain("EMG", text);
    }

    [Fact]
    public async Task Ended_UsesTheEndedTemplate_AsAReply()
    {
        _f.AddChannel(Chat, c => c.TemplateMap = Map(("emergency", "EMG {message}"), ("ended", "END {emoji} {region}")));
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        var original = _f.Deliveries(Chat).Single().TelegramMessageId;

        await _f.DispatchAsync("");

        var reply = _f.Bot.Sent.Last();
        Assert.Equal("END ℹ️ м. Київ", reply.Text);
        Assert.Equal(original, reply.ReplyParameters!.MessageId);
    }

    [Fact]
    public async Task Ended_DefaultLayout_WhenNoTemplateIsSet()
    {
        _f.AddChannel(Chat);
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        await _f.DispatchAsync("");

        var reply = _f.Bot.Sent.Last();
        Assert.StartsWith("ℹ️ ", reply.Text);
        Assert.Contains("запис про екстрені відключення більше не відображається", reply.Text);
        Assert.Contains("не підтверджує, що електропостачання відновлено", reply.Text);
        Assert.DoesNotContain("✅", reply.Text);
        Assert.Contains("Деталі:", reply.Text);
    }

    [Fact]
    public async Task EndedOfAnAccident_AlsoUsesTheEndedTemplate_NotTheRegionalOne()
    {
        _f.AddChannel(Chat, c => c.TemplateMap = Map(("regional", "REG {message}"), ("ended", "END")));
        await _f.DispatchAsync(DispatcherFixture.AccidentRightBankText);

        await _f.DispatchAsync("");

        Assert.Equal("END", _f.Bot.Sent.Last().Text);
    }

    // ---- legacy template ----

    [Fact]
    public async Task LegacyTemplate_IsUsedForEveryAlertSlot()
    {
        _f.AddChannel(Chat, c => c.Template = "L: {title}");
        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        await _f.DispatchAsync(DispatcherFixture.AccidentRightBankText);
        await _f.DispatchAsync("");

        var texts = _f.Bot.Sent.Select(s => s.Text).Concat(_f.Bot.Edits.Select(e => e.Text)).ToList();
        Assert.All(texts, t => Assert.StartsWith("L: ", t));
        Assert.Contains("L: Екстрені відключення", texts);
        Assert.Contains("L: Аварія в енергосистемі", texts);
    }

    [Fact]
    public async Task OwnSlotTemplate_BeatsTheLegacyOne()
    {
        var text = await SentTextAsync(
            DispatcherFixture.EmergencyText,
            c => { c.Template = "LEGACY"; c.TemplateMap = Map(("emergency", "OWN")); });

        Assert.Equal("OWN", text);
    }

    [Fact]
    public async Task LegacyTemplate_StillWorksForAnotherSlot_WhenOnlyOneSlotHasItsOwn()
    {
        var text = await SentTextAsync(
            DispatcherFixture.AccidentRightBankText,
            c => { c.Template = "LEGACY"; c.TemplateMap = Map(("emergency", "OWN")); });

        Assert.Equal("LEGACY", text);
    }

    [Fact]
    public async Task ScheduleSlotTemplates_DoNotAffectAlerts()
    {
        var text = await SentTextAsync(
            DispatcherFixture.EmergencyText,
            c => c.TemplateMap = Map(("schedule", "{outages}"), ("off", "OFF"), ("on", "ON")));

        Assert.StartsWith("🚨 ", text);
        Assert.Contains("Деталі:", text);
    }

    // ---- districts in the text ----

    // A full set of a retired bank's districts is named by the bank.
    private const string RightBankNames = "Правий берег";

    private const string BothBanksNames = "Лівий берег, Правий берег";

    [Fact]
    public async Task BankAlert_NamesTheBank()
    {
        var text = await SentTextAsync(DispatcherFixture.AccidentRightBankText, _ => { });

        Assert.Contains($"м. Київ ({RightBankNames})", text);
        Assert.DoesNotContain("Дарницький", text);
    }

    [Fact]
    public async Task BankAlert_PartAndWherePlaceholders_AreTheDistricts()
    {
        var text = await SentTextAsync(
            DispatcherFixture.AccidentRightBankText,
            c => c.TemplateMap = Map(("regional", "[{part}] [{where}]")));

        Assert.Equal($"[{RightBankNames}] [м. Київ ({RightBankNames})]", text);
    }

    [Fact]
    public async Task BothBanks_AreNamedTogether()
    {
        var text = await SentTextAsync(
            DispatcherFixture.AccidentBothBanksText,
            c => c.TemplateMap = Map(("regional", "[{part}]")));

        Assert.Equal($"[{BothBanksNames}]", text);
    }

    [Fact]
    public async Task WholeRegionAlert_HasNoPart()
    {
        var text = await SentTextAsync(
            DispatcherFixture.EmergencyText,
            c => c.TemplateMap = Map(("emergency", "[{part}] [{where}]")));

        Assert.Equal("[] [м. Київ]", text);
    }

    [Fact]
    public async Task ConditionalBlock_OnPart_IsShownOnlyWhenThereIsAPart()
    {
        _f.AddChannel(Chat, c => c.TemplateMap = Map(("emergency", "{region}{#part} / {part}{/part}"), ("regional", "{region}{#part} / {part}{/part}")));

        await _f.DispatchAsync(DispatcherFixture.EmergencyText);
        await _f.DispatchAsync(DispatcherFixture.AccidentRightBankText);

        Assert.Equal("м. Київ", _f.Bot.Sent.First().Text);
        Assert.Equal($"м. Київ / {RightBankNames}", _f.Bot.Edits.Last().Text);
    }

    // ---- local (group) alerts ----

    [Fact]
    public async Task GroupAlert_WithLocalSlotOff_SendsNothing()
    {
        _f.AddChannel(Chat, c =>
        {
            c.GroupId = Group;
            c.SlotSettingMap = new Dictionary<string, SlotSettingDto> { ["local"] = new(false, true) };
        });

        var summary = await GroupAsync(emergency: true);

        Assert.Equal(new(0, 0, 0, 0), summary);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task GroupAlert_DefaultLocalLayout_ShowsGroupAndSavedAddress()
    {
        _f.AddChannel(Chat, c => { c.GroupId = Group; c.AddressLabel = "вул. Хрещатик, 1"; });

        await GroupAsync(emergency: true);

        var text = Assert.Single(_f.Bot.Sent).Text;
        Assert.StartsWith("🚨 Локальне відключення, черга 45.1\n", text);
        Assert.Contains("\n📍 вул. Хрещатик, 1\n\nДеталі: https://www.dtek-kem.com.ua/ua/shutdowns", text);
    }

    [Fact]
    public async Task GroupAlert_WithoutAnAddress_HasNoAddressLine()
    {
        _f.AddChannel(Chat, c => c.GroupId = Group);

        await GroupAsync(emergency: true);

        var text = Assert.Single(_f.Bot.Sent).Text;
        Assert.DoesNotContain("📍", text);
        Assert.Contains("\n\nДеталі:", text);
    }

    [Fact]
    public async Task GroupAlert_UsesTheLocalTemplate_WithGroupAndAddressPlaceholders()
    {
        _f.AddChannel(Chat, c =>
        {
            c.GroupId = Group;
            c.AddressLabel = "вул. А & Б";
            c.TemplateMap = Map(("local", "L {group}|{address}|{region}"), ("emergency", "EMG"));
        });

        await GroupAsync(emergency: true);

        Assert.Equal("L 45.1|вул. А &amp; Б|м. Київ", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task GroupAlert_Ended_UsesTheEndedTemplate_WithTheGroupPlaceholderEmpty()
    {
        _f.AddChannel(Chat, c =>
        {
            c.GroupId = Group;
            c.TemplateMap = Map(("local", "LOC {message}"), ("ended", "END {region}"));
        });
        await GroupAsync(emergency: true);

        await GroupAsync(emergency: false);

        var reply = _f.Bot.Sent.Last();
        Assert.Equal("END м. Київ", reply.Text);
        Assert.NotNull(reply.ReplyParameters);
    }

    [Fact]
    public async Task GroupAlert_LegacyTemplate_IsHonoured()
    {
        _f.AddChannel(Chat, c => { c.GroupId = Group; c.Template = "LEG {group}"; });

        await GroupAsync(emergency: true);

        Assert.Equal("LEG 45.1", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task GroupAlert_LocalTemplateWinsOverLegacy()
    {
        _f.AddChannel(Chat, c =>
        {
            c.GroupId = Group;
            c.Template = "LEG {group}";
            c.TemplateMap = Map(("local", "LOC {group}"));
        });

        await GroupAsync(emergency: true);

        Assert.Equal("LOC 45.1", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task RegionAlert_DoesNotExposeGroupOrAddress()
    {
        _f.AddChannel(Chat, c => { c.GroupId = Group; c.AddressLabel = "вул. А"; c.TemplateMap = Map(("emergency", "[{message}]")); });

        await _f.DispatchAsync(DispatcherFixture.EmergencyText);

        Assert.DoesNotContain("45.1", Assert.Single(_f.Bot.Sent).Text);
    }
}
