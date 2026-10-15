using DtekBot.Shared;

namespace DtekBot.Tests;

public class MessageTemplateTests
{
    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value);

    // ---- Render ----

    [Fact]
    public void Render_ReplacesPlaceholders()
    {
        var result = MessageTemplate.Render("{emoji} {region}!", Values(("emoji", "🚨"), ("region", "Київ")));

        Assert.Equal("🚨 Київ!", result);
    }

    [Fact]
    public void Render_HtmlEscapesValues()
    {
        var result = MessageTemplate.Render("<b>{popup}</b>", Values(("popup", "<b>&")));

        Assert.Equal("<b>&lt;b&gt;&amp;</b>", result);
    }

    [Fact]
    public void Render_DoesNotEscapeTemplateMarkup()
    {
        var result = MessageTemplate.Render("<i>{region}</i> &amp; more", Values(("region", "Київ")));

        Assert.Equal("<i>Київ</i> &amp; more", result);
    }

    [Fact]
    public void Render_DoubleBraces_AreLiteralBraces()
    {
        var result = MessageTemplate.Render("{{region}} = {region}", Values(("region", "Київ")));

        Assert.Equal("{region} = Київ", result);
    }

    [Fact]
    public void Render_LiteralBracesAdjacentToPlaceholder()
    {
        var result = MessageTemplate.Render("{{{region}}}", Values(("region", "Київ")));

        Assert.Equal("{Київ}", result);
    }

    [Fact]
    public void Render_UnknownPlaceholder_IsLeftAsIs()
    {
        var result = MessageTemplate.Render("a {nope} b {region}", Values(("region", "Київ")));

        Assert.Equal("a {nope} b Київ", result);
    }

    [Fact]
    public void Render_UnclosedBrace_IsLeftAsIs()
    {
        var result = MessageTemplate.Render("a { b", Values(("region", "Київ")));

        Assert.Equal("a { b", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n ")]
    public void Render_NullOrBlankTemplate_RendersDefault(string? template)
    {
        var values = Values(("emoji", "🚨"), ("message", "Повідомлення"), ("site", "https://x.y"));

        var result = MessageTemplate.Render(template, values);

        Assert.Equal("🚨 Повідомлення\n\nДеталі: https://x.y", result);
        Assert.Equal(MessageTemplate.Render(MessageTemplate.Default, values), result);
    }

    // ---- Validate ----

    [Fact]
    public void Validate_DefaultTemplate_IsValid()
    {
        Assert.Empty(MessageTemplate.Validate(MessageTemplate.Default));
    }

    [Theory]
    [InlineData("{message}")]
    [InlineData("{popup}")]
    [InlineData("<b>{title}</b>: {message} {popup}")]
    [InlineData("{{literal}} {message}")]
    [InlineData("<a href=\"https://x.y\">t</a> {message}")]
    [InlineData("<a href=\"{site}\">сайт</a> {message}")]
    [InlineData("<a href=\"tg://user?id=1\">t</a> {message}")]
    [InlineData("<B>{message}</B>")]
    [InlineData("<b><i>{message}</i></b>")]
    [InlineData("<tg-spoiler>{message}</tg-spoiler>")]
    [InlineData("{message} &amp; &lt; &gt; &#169;")]
    public void Validate_ValidTemplates_HaveNoErrors(string template)
    {
        Assert.Empty(MessageTemplate.Validate(template));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_EmptyTemplate_IsError(string? template)
    {
        Assert.Single(MessageTemplate.Validate(template));
    }

    [Fact]
    public void Validate_TemplateAtMaxLength_IsAccepted()
    {
        var template = "{message}" + new string('x', MessageTemplate.MaxLength - "{message}".Length);

        Assert.Equal(MessageTemplate.MaxLength, template.Length);
        Assert.Empty(MessageTemplate.Validate(template));
    }

    [Fact]
    public void Validate_TemplateOverMaxLength_IsError()
    {
        var template = "{message}" + new string('x', MessageTemplate.MaxLength);

        var errors = MessageTemplate.Validate(template);

        Assert.Single(errors);
        Assert.Contains(MessageTemplate.MaxLength.ToString(), errors[0]);
    }

    [Fact]
    public void Validate_UnknownPlaceholder_IsError()
    {
        var errors = MessageTemplate.Validate("{message} {nope}");

        var error = Assert.Single(errors);
        Assert.Contains("{nope}", error);
    }

    [Theory]
    [InlineData("just text")]
    [InlineData("{emoji} {title} {region} {part} {where} {site} {time}")]
    public void Validate_MissingMessageAndPopup_IsError(string template)
    {
        var errors = MessageTemplate.Validate(template);

        var error = Assert.Single(errors);
        Assert.Contains("{message}", error);
        Assert.Contains("{popup}", error);
    }

    [Theory]
    [InlineData("{message} <script>x</script>")]
    [InlineData("{message} <div>x</div>")]
    [InlineData("{message} <br>")]
    public void Validate_UnsupportedTags_AreErrors(string template)
    {
        Assert.NotEmpty(MessageTemplate.Validate(template));
    }

    [Fact]
    public void Validate_UnsupportedTag_NamesTheTag()
    {
        Assert.Contains(MessageTemplate.Validate("{message} <script>"), e => e.Contains("<script>"));
    }

    [Fact]
    public void Validate_UnclosedTag_IsError()
    {
        var error = Assert.Single(MessageTemplate.Validate("<b>{message}"));

        Assert.Contains("<b>", error);
    }

    [Theory]
    [InlineData("<b>{message}</i>")]
    [InlineData("<b><i>{message}</b></i>")]
    [InlineData("{message}</b>")]
    public void Validate_MismatchedOrStrayClosingTag_IsError(string template)
    {
        Assert.NotEmpty(MessageTemplate.Validate(template));
    }

    [Theory]
    [InlineData("<b class=\"x\">{message}</b>")]
    [InlineData("<i style=\"color:red\">{message}</i>")]
    public void Validate_TagWithAttributes_IsError(string template)
    {
        var errors = MessageTemplate.Validate(template);

        Assert.Contains(errors, e => e.Contains("атрибут"));
    }

    [Theory]
    [InlineData("<a href=\"javascript:alert(1)\">t</a> {message}")]
    [InlineData("<a>t</a> {message}")]
    [InlineData("<a onclick=\"x\" href=\"https://x.y\">t</a> {message}")]
    [InlineData("<a href='https://x.y'>t</a> {message}")]
    public void Validate_UnsafeOrMalformedLink_IsError(string template)
    {
        Assert.NotEmpty(MessageTemplate.Validate(template));
    }

    [Theory]
    [InlineData("{message} a < b")]
    [InlineData("{message} a > b")]
    [InlineData("{message} <")]
    public void Validate_BareAngleBrackets_AreErrors(string template)
    {
        Assert.Contains(MessageTemplate.Validate(template), e => e.Contains("&lt;") && e.Contains("&gt;"));
    }

    [Fact]
    public void Validate_BareAmpersand_IsError()
    {
        Assert.Contains(MessageTemplate.Validate("{message} a & b"), e => e.Contains("&amp;"));
    }

    [Fact]
    public void Validate_EscapedAmpersand_IsOk()
    {
        Assert.Empty(MessageTemplate.Validate("{message} a &amp; b"));
    }

    [Theory]
    [InlineData("{message} {")]
    [InlineData("{message} { text")]
    public void Validate_StrayOpeningBrace_IsError(string template)
    {
        Assert.Contains(MessageTemplate.Validate(template), e => e.Contains("{"));
    }

    [Fact]
    public void Validate_StrayClosingBrace_IsError()
    {
        Assert.Contains(MessageTemplate.Validate("{message} }"), e => e.Contains("}"));
    }

    [Fact]
    public void Validate_DoesNotReportDuplicateErrors()
    {
        var errors = MessageTemplate.Validate("{message} {nope} {nope}");

        Assert.Equal(errors.Count, errors.Distinct().Count());
    }

    [Fact]
    public void Placeholders_CoverEveryValueProducedByAlertText()
    {
        var values = AlertText.Values(AlertKind.Emergency, "R", [], "https://x.y", DateTimeOffset.UtcNow, "p");

        Assert.Equal(
            values.Keys.OrderBy(k => k),
            MessageTemplate.Placeholders.Select(p => p.Name).OrderBy(k => k));
    }

    // ---- RenderPreviewHtml ----

    [Theory]
    [InlineData(AlertKind.Emergency, "\U0001F6A8")]
    [InlineData(AlertKind.Accident, "\u26A0")]
    [InlineData(AlertKind.Unknown, "\u2139")]
    [InlineData(AlertKind.Ended, "\u2705")]
    public void RenderPreviewHtml_ContainsEmojiAndRegionName(AlertKind kind, string emoji)
    {
        var html = MessageTemplate.RenderPreviewHtml(null, kind, "м. Київ", [], "https://x.y");

        Assert.Contains(emoji, html);
        Assert.Contains("м. Київ", html);
        Assert.Contains("https://x.y", html);
    }

    [Fact]
    public void RenderPreviewHtml_IncludesPartInWhere()
    {
        var html = MessageTemplate.RenderPreviewHtml("{where}", AlertKind.Accident, "м. Київ", ["Правий берег"], "https://x.y");

        Assert.Equal("м. Київ (Правий берег)", html);
    }

    [Fact]
    public void RenderPreviewHtml_UsesSamplePopup_WhenNoneGiven_AndEscapesGivenPopup()
    {
        var sample = MessageTemplate.RenderPreviewHtml("{popup}", AlertKind.Accident, "R", [], "s");
        var custom = MessageTemplate.RenderPreviewHtml("{popup}", AlertKind.Accident, "R", [], "s", "<script>&");

        Assert.False(string.IsNullOrWhiteSpace(sample));
        Assert.Equal("&lt;script&gt;&amp;", custom);
    }
}
