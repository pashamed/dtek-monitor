using System.Text.RegularExpressions;
using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;

namespace DtekBot.Tests;

public class MessageSlotsTests
{
    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    public static TheoryData<string> SlotIds()
    {
        var data = new TheoryData<string>();
        foreach (var slot in MessageSlots.All)
        {
            data.Add(slot.Id);
        }

        return data;
    }

    // ---- the registry ----

    [Fact]
    public void All_HasTheSlots_InOrder()
    {
        Assert.Equal(["emergency", "ended", "regional", "local", "schedule", "schedule-line", "before-off", "off", "on"], MessageSlots.All.Select(s => s.Id));
    }

    [Fact]
    public void All_GroupsTheSlots()
    {
        Assert.Equal(
            ["emergency", "emergency", "regional", "local", "stabilization", "stabilization", "stabilization", "stabilization", "stabilization"],
            MessageSlots.All.Select(s => s.Group));
    }

    [Fact]
    public void All_IdsAreUnique_AndEveryFieldIsFilledIn()
    {
        Assert.Equal(MessageSlots.All.Count, MessageSlots.All.Select(s => s.Id).Distinct().Count());
        Assert.All(MessageSlots.All, s =>
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Title));
            Assert.False(string.IsNullOrWhiteSpace(s.Description));
            Assert.False(string.IsNullOrWhiteSpace(s.DefaultTemplate));
            Assert.NotEmpty(s.Placeholders);
            Assert.Equal(s.Placeholders.Count, s.Placeholders.Select(p => p.Name).Distinct().Count());
            Assert.All(s.Placeholders, p =>
            {
                Assert.False(string.IsNullOrWhiteSpace(p.Description));
                Assert.False(string.IsNullOrWhiteSpace(p.Example));
            });
        });
    }

    [Theory]
    [InlineData("emergency", "emergency")]
    [InlineData("local", "local")]
    [InlineData("off", "off")]
    public void Find_ReturnsTheSlot(string id, string expected) => Assert.Equal(expected, MessageSlots.Find(id)!.Id);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Emergency")]
    [InlineData("EMERGENCY")]
    [InlineData("bogus")]
    [InlineData(" off")]
    public void Find_IsExact_AndNullForUnknown(string? id) => Assert.Null(MessageSlots.Find(id));

    [Theory]
    [InlineData("emergency", true)]
    [InlineData("regional", true)]
    [InlineData("local", true)]
    [InlineData("ended", true)]
    [InlineData("schedule", false)]
    [InlineData("off", false)]
    [InlineData("on", false)]
    [InlineData("bogus", false)]
    [InlineData(null, false)]
    public void IsAlert_IsTrueOnlyForTheAlertSlots(string? id, bool expected) => Assert.Equal(expected, MessageSlots.IsAlert(id));

    [Theory]
    [InlineData("emergency", "message,popup")]
    [InlineData("regional", "message,popup")]
    [InlineData("local", "message,popup")]
    [InlineData("schedule", "outages,morning,afternoon,evening,night")]
    [InlineData("schedule-line", "start,end")]
    [InlineData("ended", "")]
    [InlineData("off", "")]
    [InlineData("on", "")]
    [InlineData("bogus", "")]
    public void RequiredAny_PerSlot(string slot, string expected) =>
        Assert.Equal(expected, string.Join(',', MessageSlots.RequiredAny(slot)));

    [Fact]
    public void Samples_AreThePlaceholderExamples()
    {
        var off = MessageSlots.Samples("off");

        Assert.Equal(
            new Dictionary<string, string>
            {
                ["time"] = "14:24",
                ["onDuration"] = "17год 23хв",
                ["until"] = "18:00",
                ["planned"] = "~4 год",
                ["region"] = "м. Київ",
                ["group"] = "45.1",
            },
            off.ToDictionary(p => p.Key, p => p.Value));
        Assert.Empty(MessageSlots.Samples("bogus"));
    }

    [Fact]
    public void Placeholders_OfTheAlertSlotsAreTheSharedOnes_AndLocalAddsGroupAndAddress()
    {
        var shared = MessageTemplate.Placeholders.Select(p => p.Name).ToList();
        Assert.Equal(shared, MessageSlots.Find("emergency")!.Placeholders.Select(p => p.Name));
        Assert.Equal(shared, MessageSlots.Find("ended")!.Placeholders.Select(p => p.Name));
        Assert.Equal(shared, MessageSlots.Find("regional")!.Placeholders.Select(p => p.Name));
        Assert.Equal(shared.Concat(new[] { "group", "address" }), MessageSlots.Find("local")!.Placeholders.Select(p => p.Name));
        Assert.Equal(
            new[] { "emoji", "title", "message", "region", "part", "where", "popup", "time", "date", "weekday", "daypart", "site" }.Order(),
            shared.Order());
    }

    // ---- defaults ----

    [Theory]
    [MemberData(nameof(SlotIds))]
    public void EveryDefaultTemplate_IsValid_ForItsOwnSlot(string slot)
    {
        Assert.Empty(MessageTemplate.Validate(MessageSlots.Find(slot)!.DefaultTemplate, slot));
    }

    [Theory]
    [MemberData(nameof(SlotIds))]
    public void EveryDefaultTemplate_RendersItsSamples_WithoutLeakingAnyBrace(string slot)
    {
        var rendered = MessageTemplate.Render(null, MessageSlots.Samples(slot), MessageSlots.Find(slot)!.DefaultTemplate);

        Assert.DoesNotContain('{', rendered);
        Assert.DoesNotContain('}', rendered);
        Assert.False(string.IsNullOrWhiteSpace(rendered));
    }

    [Theory]
    [MemberData(nameof(SlotIds))]
    public void EveryDefaultTemplate_RendersEveryPlaceholder_WithItsSample(string slot)
    {
        var definition = MessageSlots.Find(slot)!;
        var all = string.Concat(definition.Placeholders.Select(p => $"[{{{p.Name}}}]"));
        var rendered = MessageTemplate.Render(all, MessageSlots.Samples(slot));

        foreach (var p in definition.Placeholders)
        {
            Assert.Contains($"[{p.Example.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")}]", rendered);
        }
    }

    [Theory]
    [MemberData(nameof(SlotIds))]
    public void EveryDefaultTemplate_PassesTheHtmlRules_AndRendersValidHtmlWithSamples(string slot)
    {
        var rendered = MessageTemplate.Render(null, MessageSlots.Samples(slot), MessageSlots.Find(slot)!.DefaultTemplate);

        // Samples may contain "<" only if escaped; no raw tags can appear from values.
        Assert.DoesNotMatch(new Regex("<(?!/?(b|i|u|s|a|code|pre)\\b)"), rendered);
    }

    [Fact]
    public void DefaultTemplates_OfTheAlertSlotsAreTheSharedDefault_ExceptLocal()
    {
        Assert.Equal(MessageTemplate.Default, MessageSlots.Find("emergency")!.DefaultTemplate);
        Assert.Equal(MessageTemplate.Default, MessageSlots.Find("ended")!.DefaultTemplate);
        Assert.Equal(MessageTemplate.Default, MessageSlots.Find("regional")!.DefaultTemplate);
        Assert.NotEqual(MessageTemplate.Default, MessageSlots.Find("local")!.DefaultTemplate);
    }

    [Fact]
    public void OffAndOn_Defaults_RenderTheDocumentedSampleWording()
    {
        Assert.Equal(
            "🔴 14:24 Світло вимкнено за графіком\n🕓 Воно було 17год 23хв\n🗓 Очікуємо за графіком о 18:00",
            MessageTemplate.Render(null, MessageSlots.Samples("off"), MessageSlots.Find("off")!.DefaultTemplate));
        Assert.Equal(
            "🟢 18:07 Світло з'явилося за графіком\n🕓 Його не було 3год 42хв\n🗓 Наступне відключення за графіком о 22:00",
            MessageTemplate.Render(null, MessageSlots.Samples("on"), MessageSlots.Find("on")!.DefaultTemplate));
    }

    // ---- conditional blocks: Render ----

    [Fact]
    public void Block_IsShown_WhenTheValueIsNotEmpty()
    {
        Assert.Equal("a [x] b", MessageTemplate.Render("a {#v}[{v}]{/v} b", Values(("v", "x"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void Block_IsHidden_WhenTheValueIsBlank(string value)
    {
        Assert.Equal("a  b", MessageTemplate.Render("a {#v}[{v}]{/v} b", Values(("v", value))));
    }

    [Fact]
    public void Block_IsHidden_WhenTheValueIsMissing()
    {
        Assert.Equal("a  b", MessageTemplate.Render("a {#v}[{v}]{/v} b", Values()));
    }

    [Fact]
    public void Block_MayHoldAnotherValue_AndNewlines()
    {
        var result = MessageTemplate.Render("head{#address}\n📍 {address}{/address}\nend", Values(("address", "вул. А, 1")));

        Assert.Equal("head\n📍 вул. А, 1\nend", result);
        Assert.Equal("head\nend", MessageTemplate.Render("head{#address}\n📍 {address}{/address}\nend", Values(("address", ""))));
    }

    [Fact]
    public void Block_ValuesAreEscaped_AndOwnTagsKept()
    {
        var result = MessageTemplate.Render("{#v}<b>{v}</b>{/v}", Values(("v", "<i>&")));

        Assert.Equal("<b>&lt;i&gt;&amp;</b>", result);
    }

    [Fact]
    public void Block_LiteralBracesInside_AreHonoured()
    {
        Assert.Equal("{x}", MessageTemplate.Render("{#v}{{x}}{/v}", Values(("v", "1"))));
    }

    [Fact]
    public void Block_SeveralBlocks_AreIndependent()
    {
        var template = "{#a}A{/a}-{#b}B{/b}-{#a}A2{/a}";

        Assert.Equal("A--A2", MessageTemplate.Render(template, Values(("a", "1"), ("b", ""))));
        Assert.Equal("-B-", MessageTemplate.Render(template, Values(("a", ""), ("b", "1"))));
    }

    [Fact]
    public void Block_Unclosed_StaysLiteral()
    {
        Assert.Equal("a {#v}[x]", MessageTemplate.Render("a {#v}[{v}]", Values(("v", "x"))));
    }

    [Fact]
    public void Block_StrayClose_IsDropped()
    {
        Assert.Equal("a b", MessageTemplate.Render("a {/v}b", Values(("v", "x"))));
    }

    [Fact]
    public void Block_Nested_RendersWhenBothAreSet_AndHidesWithTheOuter()
    {
        var template = "{#a}1{#b}2{/b}3{/a}";

        Assert.Equal("123", MessageTemplate.Render(template, Values(("a", "x"), ("b", "y"))));
        Assert.Equal("13", MessageTemplate.Render(template, Values(("a", "x"), ("b", ""))));
        Assert.Equal("", MessageTemplate.Render(template, Values(("a", ""), ("b", "y"))));
    }

    [Fact]
    public void Render_UsesTheFallback_ForABlankTemplate_ThenDefault()
    {
        var values = Values(("emoji", "🚨"), ("message", "m"), ("site", "s"));

        Assert.Equal("🚨 m\n\nДеталі: s", MessageTemplate.Render("  ", values));
        Assert.Equal("fb m", MessageTemplate.Render(null, values, "fb {message}"));
        Assert.Equal("tpl", MessageTemplate.Render("tpl", values, "fb {message}"));
    }

    // ---- conditional blocks and slots: Validate ----

    [Fact]
    public void Validate_SlotDefaultsToEmergency()
    {
        Assert.Empty(MessageTemplate.Validate("{message}"));
        Assert.Contains("Невідомий параметр {group}.", MessageTemplate.Validate("{message} {group}"));
    }

    [Fact]
    public void Validate_UnknownSlot_IsAnError()
    {
        Assert.Equal(["Невідомий тип повідомлення."], MessageTemplate.Validate("{message}", "bogus"));
    }

    [Theory]
    [InlineData("local", "{message} {group} {address}")]
    [InlineData("local", "{message}{#address}{address}{/address}")]
    [InlineData("schedule", "{header} {weekday} {date} {outages} {total} {tomorrow} {updated} {region} {group}")]
    [InlineData("schedule", "{outages}{#tomorrow}\n{tomorrow}{/tomorrow}")]
    [InlineData("off", "{time} {onDuration} {until} {planned} {region} {group}")]
    [InlineData("on", "{time} {offDuration} {next} {region} {group}")]
    [InlineData("ended", "x")]
    [InlineData("off", "just text")]
    [InlineData("on", "<b>x</b>")]
    public void Validate_AcceptsTheSlotsOwnPlaceholders(string slot, string template)
    {
        Assert.Empty(MessageTemplate.Validate(template, slot));
    }

    [Theory]
    [InlineData("emergency", "{message} {group}", "group")]
    [InlineData("emergency", "{message} {address}", "address")]
    [InlineData("regional", "{message} {outages}", "outages")]
    [InlineData("schedule", "{outages} {message}", "message")]
    [InlineData("schedule", "{outages} {emoji}", "emoji")]
    [InlineData("off", "{offDuration}", "offDuration")]
    [InlineData("on", "{onDuration}", "onDuration")]
    [InlineData("on", "{until}", "until")]
    [InlineData("off", "{next}", "next")]
    public void Validate_RejectsPlaceholdersOfOtherSlots(string slot, string template, string name)
    {
        Assert.Contains($"Невідомий параметр {{{name}}}.", MessageTemplate.Validate(template, slot));
    }

    [Theory]
    [InlineData("emergency", "no placeholders")]
    [InlineData("regional", "{emoji} {title}")]
    [InlineData("local", "{group}")]
    [InlineData("emergency", "{#message}x{/message}")]
    public void Validate_AlertSlots_NeedMessageOrPopup(string slot, string template)
    {
        Assert.Contains(MessageTemplate.Validate(template, slot), e => e.StartsWith("Додайте {message} або {popup}"));
    }

    [Theory]
    [InlineData("{message}")]
    [InlineData("{popup}")]
    [InlineData("{popup} {message}")]
    public void Validate_AlertSlots_AcceptEitherOfThem(string template)
    {
        Assert.Empty(MessageTemplate.Validate(template, "regional"));
    }

    [Theory]
    [InlineData("nothing")]
    [InlineData("{header} {date}")]
    [InlineData("{#outages}x{/outages}")]
    public void Validate_Schedule_NeedsOutages(string template)
    {
        Assert.Contains(MessageTemplate.Validate(template, "schedule"), error => error.StartsWith("Додайте {outages}"));
    }

    [Fact]
    public void Validate_Block_UnknownName_IsAnError()
    {
        var errors = MessageTemplate.Validate("{message}{#nope}x{/nope}");

        Assert.Equal(["Невідомий параметр {nope} в умовному блоці."], errors);
    }

    [Fact]
    public void Validate_Block_NameFromAnotherSlot_IsUnknown()
    {
        Assert.Contains("Невідомий параметр {address} в умовному блоці.", MessageTemplate.Validate("{message}{#address}x{/address}", "emergency"));
    }

    [Fact]
    public void Validate_Block_Nested_IsAnError()
    {
        var errors = MessageTemplate.Validate("{message}{#region}{#part}x{/part}{/region}");

        Assert.Contains("Умовні блоки не можуть бути вкладеними.", errors);
    }

    [Fact]
    public void Validate_Block_StrayClose_IsAnError()
    {
        Assert.Contains("Блок {/part} закрито без відкриття {#part}.", MessageTemplate.Validate("{message}{/part}"));
    }

    [Fact]
    public void Validate_Block_MismatchedClose_IsAnError()
    {
        var errors = MessageTemplate.Validate("{message}{#part}x{/region}");

        Assert.Contains("Блок {/region} закрито без відкриття {#region}.", errors);
    }

    [Fact]
    public void Validate_Block_Unclosed_IsAnError()
    {
        Assert.Contains("Блок {#part} не закрито: додайте {/part}.", MessageTemplate.Validate("{message}{#part}x"));
    }

    [Fact]
    public void Validate_Block_Valid_IsAccepted()
    {
        Assert.Empty(MessageTemplate.Validate("{message}{#part} ({part}){/part}{#region}!{/region}"));
    }

    [Fact]
    public void Validate_LengthLimit_AppliesToEverySlot()
    {
        foreach (var slot in MessageSlots.All)
        {
            var required = MessageSlots.RequiredAny(slot.Id).FirstOrDefault();
            var template = (required is null ? "" : "{" + required + "}") + new string('x', MessageTemplate.MaxLength + 1);

            Assert.Contains($"Шаблон задовгий: максимум {MessageTemplate.MaxLength} символів.", MessageTemplate.Validate(template, slot.Id));
        }
    }

    [Fact]
    public void Validate_HtmlRules_StillApplyInEverySlot()
    {
        foreach (var slot in MessageSlots.All)
        {
            var required = MessageSlots.RequiredAny(slot.Id).FirstOrDefault();
            var body = required is null ? "x" : "{" + required + "}";

            Assert.NotEmpty(MessageTemplate.Validate("<script>" + body, slot.Id));
            Assert.NotEmpty(MessageTemplate.Validate("<b>" + body, slot.Id));
            Assert.NotEmpty(MessageTemplate.Validate(body + " a & b", slot.Id));
            if (slot.Id == MessageSlots.ScheduleLine)
                Assert.NotEmpty(MessageTemplate.Validate("<b>" + body + "</b>", slot.Id));
            else
                Assert.Empty(MessageTemplate.Validate("<b>" + body + "</b>", slot.Id));
        }
    }

    [Fact]
    public void Validate_Errors_AreDistinct()
    {
        var errors = MessageTemplate.Validate("{message} {nope} {nope} {nope}");

        Assert.Single(errors);
    }

    // ---- render + validate agree ----

    [Theory]
    [InlineData("off", "<b>{time}</b>{#until} до {until}{/until}")]
    [InlineData("on", "{time}{#next} → {next}{/next}")]
    [InlineData("local", "{emoji} {group}: {message}{#address} @ {address}{/address}")]
    [InlineData("schedule", "{header}\n{outages}{#tomorrow}\nЗавтра: {tomorrow}{/tomorrow}")]
    public void ValidTemplates_RenderWithoutRawBraces_ForTheirSamples(string slot, string template)
    {
        Assert.Empty(MessageTemplate.Validate(template, slot));

        var rendered = MessageTemplate.Render(template, MessageSlots.Samples(slot));

        Assert.DoesNotMatch(new Regex("[{}]"), rendered);
    }
}

public class ChannelTemplateTests
{
    private static Dictionary<string, string> Map(params (string Slot, string Text)[] items) =>
        items.ToDictionary(i => i.Slot, i => i.Text);

    [Fact]
    public void NewChannel_HasTheDocumentedDefaults()
    {
        var channel = new Channel();

        Assert.Equal(ScheduleImage.None, channel.ScheduleImage);
        Assert.Equal(PostMode.Edit, channel.ScheduleEventMode);
        Assert.Equal(PostMode.New, channel.SchedulePostMode);
        Assert.Equal(ScheduleImage.Grid, channel.SchedulePostImage);
        Assert.False(channel.SchedulePost);
        Assert.False(channel.ScheduleNotify);
        Assert.Empty(channel.TemplateMap);
        Assert.Null(channel.Templates);
        Assert.Null(channel.Template);
    }

    [Fact]
    public void TemplateFor_PrefersTheSlotsOwnTemplate()
    {
        var channel = new Channel { Template = "legacy", TemplateMap = Map(("regional", "own")) };

        Assert.Equal("own", channel.TemplateFor("regional"));
    }

    [Theory]
    [InlineData("emergency")]
    [InlineData("ended")]
    [InlineData("regional")]
    [InlineData("local")]
    public void TemplateFor_FallsBackToTheLegacyTemplate_ForAlertSlots(string slot)
    {
        var channel = new Channel { Template = "legacy {message}" };

        Assert.Equal("legacy {message}", channel.TemplateFor(slot));
    }

    [Theory]
    [InlineData("schedule")]
    [InlineData("off")]
    [InlineData("on")]
    [InlineData("bogus")]
    public void TemplateFor_DoesNotApplyTheLegacyTemplate_ToTheOtherSlots(string slot)
    {
        var channel = new Channel { Template = "legacy {message}" };

        Assert.Null(channel.TemplateFor(slot));
    }

    [Fact]
    public void TemplateFor_IgnoresBlankTemplates()
    {
        var channel = new Channel { Template = "legacy", TemplateMap = Map(("emergency", "  "), ("off", "")) };

        Assert.Equal("legacy", channel.TemplateFor("emergency"));
        Assert.Null(channel.TemplateFor("off"));
    }

    [Fact]
    public void TemplateFor_BlankLegacy_IsNull()
    {
        Assert.Null(new Channel { Template = "   " }.TemplateFor("emergency"));
    }

    [Fact]
    public void TemplateMap_RoundTrips_AsJson()
    {
        var channel = new Channel { TemplateMap = Map(("on", "ON {time}"), ("off", "OFF <b>{time}</b> «ї»")) };

        Assert.Equal(2, channel.TemplateMap.Count);
        Assert.Equal("OFF <b>{time}</b> «ї»", channel.TemplateMap["off"]);
        Assert.StartsWith("{", channel.Templates);
    }

    [Fact]
    public void TemplateMap_SettingEmpty_ClearsTheColumn()
    {
        var channel = new Channel { TemplateMap = Map(("on", "x")) };

        channel.TemplateMap = new Dictionary<string, string>();

        Assert.Null(channel.Templates);
        Assert.Empty(channel.TemplateMap);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("")]
    [InlineData("{\"a\":")]
    public void TemplateMap_InvalidJson_ReadsAsEmpty(string json)
    {
        var channel = new Channel { Templates = json };

        Assert.Empty(channel.TemplateMap);
        Assert.Null(channel.TemplateFor("off"));
    }

    [Fact]
    public async Task Templates_SurviveTheDatabase()
    {
        using var fixture = new DispatcherFixture();
        fixture.Db.Channels.Add(new Channel
        {
            ChatId = 77,
            RegionId = "kyiv",
            Template = "legacy",
            TemplateMap = Map(("schedule", "{outages}"), ("off", "OFF")),
            ScheduleEventMode = PostMode.ReplaceDelete,
            SchedulePostMode = PostMode.Edit,
            SchedulePostImage = ScheduleImage.Card,
            SchedulePost = true,
        });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var loaded = await fixture.Db.Channels.AsNoTracking().SingleAsync(c => c.ChatId == 77);

        Assert.Equal("{outages}", loaded.TemplateFor("schedule"));
        Assert.Equal("OFF", loaded.TemplateFor("off"));
        Assert.Equal("legacy", loaded.TemplateFor("local"));
        Assert.Equal(PostMode.ReplaceDelete, loaded.ScheduleEventMode);
        Assert.Equal(PostMode.Edit, loaded.SchedulePostMode);
        Assert.Equal(ScheduleImage.Card, loaded.SchedulePostImage);
        Assert.True(loaded.SchedulePost);
    }

    [Fact]
    public async Task NewChannelRows_GetTheDefaults_FromTheDatabase()
    {
        using var fixture = new DispatcherFixture();
        fixture.Db.Channels.Add(new Channel { ChatId = 78, RegionId = "kyiv" });
        await fixture.Db.SaveChangesAsync();
        fixture.Db.ChangeTracker.Clear();

        var loaded = await fixture.Db.Channels.AsNoTracking().SingleAsync(c => c.ChatId == 78);

        Assert.Equal(ScheduleImage.None, loaded.ScheduleImage);
        Assert.Equal(PostMode.Edit, loaded.ScheduleEventMode);
        Assert.Equal(PostMode.New, loaded.SchedulePostMode);
        Assert.Equal(ScheduleImage.Grid, loaded.SchedulePostImage);
    }
}
