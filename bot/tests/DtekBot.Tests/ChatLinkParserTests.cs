using DtekBot.Api.MiniApp;
using Xunit;

namespace DtekBot.Tests;

public class ChatLinkParserTests
{
    [Theory]
    [InlineData("https://t.me/my_channel", "@my_channel")]
    [InlineData("http://t.me/my_channel/", "@my_channel")]
    [InlineData("t.me/my_channel", "@my_channel")]
    [InlineData("https://www.t.me/My_Channel?start=1", "@My_Channel")]
    [InlineData("https://telegram.me/my_channel", "@my_channel")]
    [InlineData("https://t.me/my_channel/123", "@my_channel")]
    [InlineData("https://t.me/s/my_channel", "@my_channel")]
    [InlineData("tg://resolve?domain=my_channel", "@my_channel")]
    [InlineData("@my_channel", "@my_channel")]
    [InlineData("  my_channel  ", "@my_channel")]
    [InlineData("-1001234567890", "-1001234567890")]
    public void Parse_AcceptsEveryWayToWriteAPublicChannel(string input, string expected)
    {
        var result = ChatLinkParser.Parse(input);

        Assert.Equal(expected, result.Chat);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("https://t.me/+AbCdEf123")]
    [InlineData("https://t.me/joinchat/AbCdEf123")]
    public void Parse_PrivateInviteLinks_AreRefusedWithAnExplanation(string input)
    {
        var result = ChatLinkParser.Parse(input);

        Assert.Null(result.Chat);
        Assert.Equal(ChatLinkParser.InviteError, result.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("два слова")]
    [InlineData("ab")]
    [InlineData("https://example.com/my_channel")]
    [InlineData("https://t.me/")]
    public void Parse_Garbage_IsRefused(string? input)
    {
        var result = ChatLinkParser.Parse(input);

        Assert.Null(result.Chat);
        Assert.NotNull(result.Error);
    }
}
