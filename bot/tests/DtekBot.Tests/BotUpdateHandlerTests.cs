using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.MiniApp;
using DtekBot.Api.Parsing;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace DtekBot.Tests;

public sealed class BotUpdateHandlerTests : IDisposable
{
    private const string NoAccess = "Керувати ботом можуть лише адміністратори чату.";
    private const string ChooseRegionFirst = "Спершу оберіть регіон";

    private const long PrivateChat = 500;
    private const long GroupChat = -1001;
    private const long ChannelChat = -1002;
    private const long AdminUser = 1;
    private const long PlainUser = 2;

    private readonly DispatcherFixture _f = new();
    private readonly BotUpdateHandler _handler;
    private int _updateId;

    public BotUpdateHandlerTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["App:PublicUrl"] = "https://example.test" })
            .Build();
        var access = new ChatAccessService(_f.Bot, _f.Db, NullLogger<ChatAccessService>.Instance);
        _handler = new BotUpdateHandler(
            _f.Bot, _f.Db, access, _f.Options, config, TimeProvider.System, NullLogger<BotUpdateHandler>.Instance);

        _f.Bot.SetStatus(AdminUser, ChatMemberStatus.Administrator);
    }

    public void Dispose() => _f.Dispose();

    // ---- helpers ----

    private static Chat MakeChat(long id, ChatType type, string? title = null, string? username = null) =>
        new() { Id = id, Type = type, Title = title, Username = username };

    private static Chat Private => MakeChat(PrivateChat, ChatType.Private, username: "pavlo");

    private static Chat Group => MakeChat(GroupChat, ChatType.Supergroup, "Мій будинок");

    private Update TextUpdate(Chat chat, string text, long? from) => new()
    {
        Id = ++_updateId,
        Message = new Message
        {
            Id = ++_updateId,
            Chat = chat,
            Text = text,
            From = from is { } id ? new User { Id = id, FirstName = "U" } : null,
        },
    };

    private Update ChannelPostUpdate(string text) => new()
    {
        Id = ++_updateId,
        ChannelPost = new Message { Id = ++_updateId, Chat = MakeChat(ChannelChat, ChatType.Channel, "Канал"), Text = text },
    };

    private Update CallbackUpdate(Chat chat, string data, long from) => new()
    {
        Id = ++_updateId,
        CallbackQuery = new CallbackQuery
        {
            Id = "cb" + ++_updateId,
            From = new User { Id = from, FirstName = "U" },
            Data = data,
            ChatInstance = "ci",
            Message = new Message { Id = 77, Chat = chat },
        },
    };

    private Task SendAsync(Chat chat, string text, long? from = null) =>
        _handler.HandleAsync(TextUpdate(chat, text, from ?? chat.Id), CancellationToken.None);

    private Task PressAsync(Chat chat, string data, long? from = null) =>
        _handler.HandleAsync(CallbackUpdate(chat, data, from ?? chat.Id), CancellationToken.None);

    private string Reply() => Assert.Single(_f.Bot.Sent).Text;

    private EditMessageTextRequest Edit() => Assert.Single(_f.Bot.Edits);

    private static List<InlineKeyboardButton> Buttons(Telegram.Bot.Types.ReplyMarkups.ReplyMarkup? markup) =>
        ((InlineKeyboardMarkup)markup!).InlineKeyboard.SelectMany(r => r).ToList();

    private Channel Row(long chatId) => _f.LoadChannel(chatId);

    private bool HasRow(long chatId) => _f.Db.Channels.AsNoTracking().Any(c => c.ChatId == chatId);

    // ---- /start, /menu, /help ----

    [Theory]
    [InlineData("/start")]
    [InlineData("/menu")]
    [InlineData("/help")]
    [InlineData("/start@dtek_test_bot")]
    public async Task MenuCommands_InPrivateChat_SendMainMenuWithCallbackAndWebAppButtons(string command)
    {
        await SendAsync(Private, command);

        var request = Assert.Single(_f.Bot.Sent);
        Assert.Contains("Підписки немає.", request.Text);
        var buttons = Buttons(request.ReplyMarkup);

        var callbacks = buttons.Where(b => b.CallbackData is not null).Select(b => b.CallbackData).ToList();
        Assert.Equal(["regions", "status", "cfg"], callbacks);

        var webApps = buttons.Where(b => b.WebApp is not null).Select(b => b.WebApp!.Url).ToList();
        Assert.Equal(["https://example.test/app/", "https://example.test/lite/"], webApps);
    }

    [Fact]
    public async Task Start_InGroup_HasNoWebAppButtons()
    {
        await SendAsync(Group, "/start", PlainUser);

        var buttons = Buttons(Assert.Single(_f.Bot.Sent).ReplyMarkup);
        Assert.DoesNotContain(buttons, b => b.WebApp is not null);
        Assert.Contains(buttons, b => b.CallbackData == "regions");
    }

    [Fact]
    public async Task Start_InChannel_HasNoWebAppButtons()
    {
        await _handler.HandleAsync(ChannelPostUpdate("/start"), CancellationToken.None);

        Assert.DoesNotContain(Buttons(Assert.Single(_f.Bot.Sent).ReplyMarkup), b => b.WebApp is not null);
    }

    [Fact]
    public async Task Start_WhenSubscribed_ShowsSubscriptionAndUnsubscribeButton()
    {
        await SendAsync(Private, "/region kyiv right-bank");
        _f.Bot.Requests.Clear();

        await SendAsync(Private, "/start");

        var request = Assert.Single(_f.Bot.Sent);
        Assert.Contains("Підписка: м. Київ (Правий берег)", request.Text);
        Assert.Contains(Buttons(request.ReplyMarkup), b => b.CallbackData == "stop");
    }

    [Fact]
    public async Task Start_WhenPublicUrlIsMissing_HasNoWebAppButtons()
    {
        var handler = new BotUpdateHandler(
            _f.Bot,
            _f.Db,
            new ChatAccessService(_f.Bot, _f.Db, NullLogger<ChatAccessService>.Instance),
            _f.Options,
            new ConfigurationBuilder().Build(),
            TimeProvider.System,
            NullLogger<BotUpdateHandler>.Instance);

        await handler.HandleAsync(TextUpdate(Private, "/start", PrivateChat), CancellationToken.None);

        Assert.DoesNotContain(Buttons(Assert.Single(_f.Bot.Sent).ReplyMarkup), b => b.WebApp is not null);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/unknown")]
    [InlineData("")]
    public async Task IgnoredMessages_ProduceNoRequests(string text)
    {
        await SendAsync(Private, text);

        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task UpdateWithoutMessage_IsIgnored()
    {
        await _handler.HandleAsync(new Update { Id = 1 }, CancellationToken.None);

        Assert.Empty(_f.Bot.Requests);
    }

    // ---- /region ----

    [Fact]
    public async Task Region_InPrivateChat_Subscribes()
    {
        await SendAsync(Private, "/region kyiv");

        var channel = Row(PrivateChat);
        Assert.True(channel.IsActive);
        Assert.Equal("kyiv", channel.RegionId);
        Assert.Null(channel.PartId);
        Assert.Equal("pavlo", channel.Title);
        Assert.Contains("Підписано: м. Київ", Reply());
        Assert.Equal(ParseMode.Html, Assert.Single(_f.Bot.Sent).ParseMode);
        Assert.Empty(_f.Db.ChannelAdmins);
    }

    [Fact]
    public async Task Region_TitleComesFromGroupTitle()
    {
        await SendAsync(Group, "/region kyiv", AdminUser);

        Assert.Equal("Мій будинок", Row(GroupChat).Title);
    }

    [Fact]
    public async Task Region_WithPart_SetsPartId()
    {
        await SendAsync(Private, "/region kyiv right-bank");

        Assert.Equal("right-bank", Row(PrivateChat).PartId);
        Assert.Contains("Підписано", Reply());
    }

    [Fact]
    public async Task Region_IsCaseInsensitive_AndAcceptsBotSuffix()
    {
        await SendAsync(Private, "/REGION@dtek_test_bot KYIV Left-Bank");

        var channel = Row(PrivateChat);
        Assert.Equal("kyiv", channel.RegionId);
        Assert.Equal("left-bank", channel.PartId);
    }

    [Fact]
    public async Task Region_Resubscribe_ClearsPart_AndReactivates_KeepingSettings()
    {
        _f.AddChannel(PrivateChat, c =>
        {
            c.PartId = "right-bank";
            c.IsActive = false;
            c.Filter = AlertFilter.EmergencyOnly;
        });

        await SendAsync(Private, "/region kyiv");

        var channel = Row(PrivateChat);
        Assert.True(channel.IsActive);
        Assert.Null(channel.PartId);
        Assert.Equal(AlertFilter.EmergencyOnly, channel.Filter);
        Assert.Single(_f.Db.Channels);
    }

    [Fact]
    public async Task Region_UnknownRegion_RepliesWithError_AndCreatesNoRow()
    {
        await SendAsync(Private, "/region atlantis");

        Assert.Contains("Невідомий регіон", Reply());
        Assert.False(HasRow(PrivateChat));
    }

    [Fact]
    public async Task Region_UnknownRegion_LeavesExistingSubscriptionUntouched()
    {
        await SendAsync(Private, "/region kyiv right-bank");

        await SendAsync(Private, "/region atlantis");

        var channel = Row(PrivateChat);
        Assert.Equal("kyiv", channel.RegionId);
        Assert.Equal("right-bank", channel.PartId);
    }

    [Fact]
    public async Task Region_UnknownPart_RepliesWithError_AndChangesNothing()
    {
        await SendAsync(Private, "/region odesa");

        await SendAsync(Private, "/region kyiv nowhere");

        Assert.Contains("Невідома частина", _f.Bot.Sent.Last().Text);
        var channel = Row(PrivateChat);
        Assert.Equal("odesa", channel.RegionId);
        Assert.Null(channel.PartId);
    }

    [Fact]
    public async Task Region_WithoutArguments_AsksForRegion()
    {
        await SendAsync(Private, "/region");

        Assert.Contains("Вкажіть код регіону", Reply());
        Assert.False(HasRow(PrivateChat));
    }

    // ---- /stop ----

    [Fact]
    public async Task Stop_DeactivatesSubscription()
    {
        await SendAsync(Private, "/region kyiv");
        _f.Bot.Requests.Clear();

        await SendAsync(Private, "/stop");

        Assert.Equal("Відписано.", Reply());
        Assert.False(Row(PrivateChat).IsActive);
    }

    [Fact]
    public async Task Stop_WithoutSubscription_SaysSo()
    {
        await SendAsync(Private, "/stop");

        Assert.Equal("Підписки немає.", Reply());
        Assert.False(HasRow(PrivateChat));
    }

    [Fact]
    public async Task Stop_Twice_SecondSaysNoSubscription()
    {
        await SendAsync(Private, "/region kyiv");
        await SendAsync(Private, "/stop");
        _f.Bot.Requests.Clear();

        await SendAsync(Private, "/stop");

        Assert.Equal("Підписки немає.", Reply());
    }

    // ---- authorization ----

    [Theory]
    [InlineData("/region kyiv")]
    [InlineData("/stop")]
    [InlineData("/template {message}")]
    public async Task Group_NonAdmin_IsRefused_AndNothingChanges(string command)
    {
        _f.AddChannel(GroupChat, c => c.PartId = "left-bank");

        await SendAsync(Group, command, PlainUser);

        Assert.Equal(NoAccess, Reply());
        var channel = Row(GroupChat);
        Assert.True(channel.IsActive);
        Assert.Equal("left-bank", channel.PartId);
        Assert.Null(channel.Template);
        Assert.Empty(_f.Db.ChannelAdmins);
    }

    [Fact]
    public async Task Group_NonAdmin_CannotSubscribe()
    {
        await SendAsync(Group, "/region kyiv", PlainUser);

        Assert.Equal(NoAccess, Reply());
        Assert.False(HasRow(GroupChat));
    }

    [Fact]
    public async Task Group_AdminSubscribes_AndIsLinkedAsChannelAdmin()
    {
        await SendAsync(Group, "/region kyiv", AdminUser);

        Assert.True(Row(GroupChat).IsActive);
        var admin = Assert.Single(_f.Db.ChannelAdmins.AsNoTracking());
        Assert.Equal(GroupChat, admin.ChatId);
        Assert.Equal(AdminUser, admin.UserId);
    }

    [Theory]
    [InlineData(ChatType.Group)]
    [InlineData(ChatType.Supergroup)]
    public async Task GroupAndSupergroupAdmins_AreLinked(ChatType type)
    {
        var chat = MakeChat(GroupChat, type, "G");

        await SendAsync(chat, "/region kyiv", AdminUser);

        Assert.Single(_f.Db.ChannelAdmins.AsNoTracking());
    }

    [Fact]
    public async Task GroupCreator_IsTreatedAsAdmin()
    {
        _f.Bot.SetStatus(PlainUser, ChatMemberStatus.Creator);

        await SendAsync(Group, "/region kyiv", PlainUser);

        Assert.True(Row(GroupChat).IsActive);
    }

    [Fact]
    public async Task Group_AdminSubscribingAgain_DoesNotDuplicateChannelAdmin()
    {
        await SendAsync(Group, "/region kyiv", AdminUser);
        await SendAsync(Group, "/region kyiv left-bank", AdminUser);

        Assert.Single(_f.Db.ChannelAdmins.AsNoTracking());
    }

    [Fact]
    public async Task Group_AdminCheckFailure_IsTreatedAsRefusal()
    {
        _f.Bot.Failure = r => r is GetChatMemberRequest ? new HttpRequestException("down") : null;

        await SendAsync(Group, "/region kyiv", AdminUser);

        Assert.Equal(NoAccess, Reply());
        Assert.False(HasRow(GroupChat));
    }

    [Fact]
    public async Task ChannelPost_WithoutSender_IsAllowed_AndCreatesNoChannelAdmin()
    {
        await _handler.HandleAsync(ChannelPostUpdate("/region kyiv"), CancellationToken.None);

        var channel = Row(ChannelChat);
        Assert.True(channel.IsActive);
        Assert.Equal("Канал", channel.Title);
        Assert.Empty(_f.Db.ChannelAdmins);
        Assert.Empty(_f.Bot.Requests.OfType<GetChatMemberRequest>());
    }

    // ---- callbacks: settings toggles ----

    [Fact]
    public async Task CfgFilter_TogglesBetweenAllAndEmergencyOnly()
    {
        _f.AddChannel(PrivateChat);

        await PressAsync(Private, "cfg:filter");
        Assert.Equal(AlertFilter.EmergencyOnly, Row(PrivateChat).Filter);

        await PressAsync(Private, "cfg:filter");
        Assert.Equal(AlertFilter.All, Row(PrivateChat).Filter);
    }

    [Fact]
    public async Task CfgEnd_TogglesNotifyOnEnd()
    {
        _f.AddChannel(PrivateChat);

        await PressAsync(Private, "cfg:end");
        Assert.False(Row(PrivateChat).NotifyOnEnd);

        await PressAsync(Private, "cfg:end");
        Assert.True(Row(PrivateChat).NotifyOnEnd);
    }

    [Fact]
    public async Task CfgEdit_TogglesEditInPlace()
    {
        _f.AddChannel(PrivateChat);

        await PressAsync(Private, "cfg:edit");
        Assert.False(Row(PrivateChat).EditInPlace);

        await PressAsync(Private, "cfg:edit");
        Assert.True(Row(PrivateChat).EditInPlace);
    }

    [Fact]
    public async Task CfgPause_TogglesIsPaused()
    {
        _f.AddChannel(PrivateChat);

        await PressAsync(Private, "cfg:pause");
        Assert.True(Row(PrivateChat).IsPaused);

        await PressAsync(Private, "cfg:pause");
        Assert.False(Row(PrivateChat).IsPaused);
    }

    [Theory]
    [InlineData("cfg:filter", "лише екстрені та аварії")]
    [InlineData("cfg:end", "Про завершення: ні")]
    [InlineData("cfg:edit", "щоразу нове повідомлення")]
    [InlineData("cfg:pause", "на паузі")]
    public async Task CfgToggle_AnswersCallback_AndEditsMessageWithSettingsScreen(string data, string expected)
    {
        _f.AddChannel(PrivateChat);

        await PressAsync(Private, data);

        var answer = Assert.Single(_f.Bot.Answers);
        Assert.False(string.IsNullOrEmpty(answer.Text));
        Assert.True(answer.ShowAlert is not true);

        var edit = Edit();
        Assert.Equal(PrivateChat, edit.ChatId.Identifier);
        Assert.Equal(77, edit.MessageId);
        Assert.Contains("⚙️ Налаштування", edit.Text);
        Assert.Contains(expected, edit.Text);
        Assert.Contains(Buttons(edit.ReplyMarkup), b => b.CallbackData == "cfg:filter");
        Assert.Contains(Buttons(edit.ReplyMarkup), b => b.CallbackData == "menu");
    }

    [Theory]
    [InlineData("cfg:filter")]
    [InlineData("cfg:end")]
    [InlineData("cfg:edit")]
    [InlineData("cfg:pause")]
    public async Task CfgToggle_ByGroupAdmin_Works(string data)
    {
        _f.AddChannel(GroupChat);

        await PressAsync(Group, data, AdminUser);

        var channel = Row(GroupChat);
        Assert.False(channel.Filter == AlertFilter.All && channel.NotifyOnEnd && channel.EditInPlace && !channel.IsPaused);
        Assert.Single(_f.Bot.Edits);
    }

    [Theory]
    [InlineData("cfg:filter")]
    [InlineData("cfg:end")]
    [InlineData("cfg:edit")]
    [InlineData("cfg:pause")]
    [InlineData("stop")]
    [InlineData("r:kyiv")]
    [InlineData("p:kyiv:all")]
    [InlineData("cfg")]
    public async Task Callbacks_ByNonAdminInGroup_AreRefusedWithAlert_AndNothingChanges(string data)
    {
        _f.AddChannel(GroupChat, c => c.PartId = "left-bank");
        var before = Row(GroupChat);

        await PressAsync(Group, data, PlainUser);

        var answer = Assert.Single(_f.Bot.Answers);
        Assert.Equal(NoAccess, answer.Text);
        Assert.True(answer.ShowAlert);
        Assert.Empty(_f.Bot.Edits);

        var after = Row(GroupChat);
        Assert.Equal(before.Filter, after.Filter);
        Assert.Equal(before.NotifyOnEnd, after.NotifyOnEnd);
        Assert.Equal(before.EditInPlace, after.EditInPlace);
        Assert.Equal(before.IsPaused, after.IsPaused);
        Assert.Equal(before.IsActive, after.IsActive);
        Assert.Equal(before.RegionId, after.RegionId);
        Assert.Equal(before.PartId, after.PartId);
    }

    [Theory]
    [InlineData("menu")]
    [InlineData("regions")]
    [InlineData("status")]
    public async Task ReadOnlyCallbacks_WorkForNonAdminsInGroup(string data)
    {
        _f.AddChannel(GroupChat);

        await PressAsync(Group, data, PlainUser);

        var answer = Assert.Single(_f.Bot.Answers);
        Assert.True(answer.ShowAlert is not true);
        Assert.NotEqual(NoAccess, answer.Text);
        Assert.Single(_f.Bot.Edits);
    }

    [Fact]
    public async Task Cfg_WithoutSubscription_AsksToChooseRegionFirst()
    {
        await PressAsync(Private, "cfg");

        Assert.Contains(ChooseRegionFirst, Edit().Text);
        Assert.Single(_f.Bot.Answers);
    }

    [Fact]
    public async Task CfgToggle_WithoutSubscription_AsksToChooseRegionFirst_AndCreatesNothing()
    {
        await PressAsync(Private, "cfg:filter");

        Assert.Contains(ChooseRegionFirst, Assert.Single(_f.Bot.Answers).Text);
        Assert.Contains(ChooseRegionFirst, Edit().Text);
        Assert.False(HasRow(PrivateChat));
    }

    [Fact]
    public async Task Cfg_WithSubscription_ShowsSettingsScreen()
    {
        _f.AddChannel(PrivateChat, c => c.PartId = "right-bank");

        await PressAsync(Private, "cfg");

        var edit = Edit();
        Assert.Contains("⚙️ Налаштування", edit.Text);
        Assert.Contains("Підписка: м. Київ (Правий берег)", edit.Text);
        Assert.Contains("Шаблон: стандартний", edit.Text);
    }

    [Fact]
    public async Task Cfg_ForInactiveSubscription_AsksToChooseRegionFirst()
    {
        _f.AddChannel(PrivateChat, c => c.IsActive = false);

        await PressAsync(Private, "cfg:pause");

        Assert.Contains(ChooseRegionFirst, Edit().Text);
        Assert.False(Row(PrivateChat).IsPaused);
    }

    [Fact]
    public async Task EditThrowingMessageNotModified_IsSwallowed()
    {
        _f.Bot.Failure = r => r is EditMessageTextRequest ? FakeTelegramBotClient.NotModified() : null;

        await PressAsync(Private, "menu");

        Assert.Single(_f.Bot.Answers);
    }

    [Fact]
    public async Task Callback_WithoutMessage_IsJustAnswered()
    {
        var update = CallbackUpdate(Private, "menu", PrivateChat);
        update.CallbackQuery!.Message = null;

        await _handler.HandleAsync(update, CancellationToken.None);

        Assert.Single(_f.Bot.Answers);
        Assert.Empty(_f.Bot.Edits);
    }

    // ---- callbacks: menu / regions / region choice ----

    [Fact]
    public async Task Callback_Menu_ShowsMainMenu()
    {
        await PressAsync(Private, "menu");

        var edit = Edit();
        Assert.Contains("Підписки немає.", edit.Text);
        Assert.Contains(Buttons(edit.ReplyMarkup), b => b.CallbackData == "regions");
    }

    [Fact]
    public async Task Callback_UnknownData_FallsBackToMainMenu()
    {
        await PressAsync(Private, "whatever");

        Assert.Contains("Підписки немає.", Edit().Text);
    }

    [Fact]
    public async Task Callback_Regions_ListsEveryRegion_AndBackButton()
    {
        await PressAsync(Private, "regions");

        var edit = Edit();
        Assert.Equal("Оберіть регіон:", edit.Text);
        var data = Buttons(edit.ReplyMarkup).Select(b => b.CallbackData).ToList();
        Assert.Equal(_f.Settings.Regions.Select(r => "r:" + r.Id).Append("menu"), data);
    }

    [Fact]
    public async Task Callback_RegionWithParts_ShowsPartPicker_WithoutSubscribing()
    {
        await PressAsync(Private, "r:kyiv");

        var edit = Edit();
        Assert.Contains("оберіть частину", edit.Text);
        var data = Buttons(edit.ReplyMarkup).Select(b => b.CallbackData).ToList();
        Assert.Equal(["p:kyiv:all", "p:kyiv:right-bank", "p:kyiv:left-bank", "regions"], data);
        Assert.False(HasRow(PrivateChat));
    }

    [Fact]
    public async Task Callback_RegionWithoutParts_SubscribesImmediately()
    {
        await PressAsync(Private, "r:odesa");

        var channel = Row(PrivateChat);
        Assert.True(channel.IsActive);
        Assert.Equal("odesa", channel.RegionId);
        Assert.Null(channel.PartId);
        Assert.Contains("Підписка: Одеська область", Edit().Text);
    }

    [Fact]
    public async Task Callback_UnknownRegion_ShowsListWithToast()
    {
        await PressAsync(Private, "r:atlantis");

        Assert.Equal("Невідомий регіон.", Assert.Single(_f.Bot.Answers).Text);
        Assert.Equal("Оберіть регіон:", Edit().Text);
        Assert.False(HasRow(PrivateChat));
    }

    [Fact]
    public async Task Callback_PartAll_SubscribesToWholeRegion()
    {
        _f.AddChannel(PrivateChat, c => c.PartId = "left-bank");

        await PressAsync(Private, "p:kyiv:all");

        var channel = Row(PrivateChat);
        Assert.Equal("kyiv", channel.RegionId);
        Assert.Null(channel.PartId);
        Assert.Contains("Підписано", Assert.Single(_f.Bot.Answers).Text);
        Assert.Contains("Підписка: м. Київ", Edit().Text);
    }

    [Fact]
    public async Task Callback_PartSpecific_SubscribesToPart()
    {
        await PressAsync(Private, "p:kyiv:right-bank");

        var channel = Row(PrivateChat);
        Assert.Equal("kyiv", channel.RegionId);
        Assert.Equal("right-bank", channel.PartId);
        Assert.Contains("Підписка: м. Київ (Правий берег)", Edit().Text);
    }

    [Fact]
    public async Task Callback_Part_ByGroupAdmin_LinksChannelAdmin()
    {
        await PressAsync(Group, "p:kyiv:all", AdminUser);

        Assert.True(Row(GroupChat).IsActive);
        Assert.Equal(AdminUser, Assert.Single(_f.Db.ChannelAdmins.AsNoTracking()).UserId);
    }

    [Fact]
    public async Task Callback_UnknownPart_DoesNotSubscribe()
    {
        await PressAsync(Private, "p:kyiv:nowhere");

        Assert.Contains("Невідома частина", Assert.Single(_f.Bot.Answers).Text);
        Assert.False(HasRow(PrivateChat));
    }

    [Fact]
    public async Task Callback_Stop_DeactivatesAndShowsMenu()
    {
        _f.AddChannel(PrivateChat);

        await PressAsync(Private, "stop");

        Assert.False(Row(PrivateChat).IsActive);
        Assert.Equal("Відписано.", Assert.Single(_f.Bot.Answers).Text);
        Assert.Contains("Підписки немає.", Edit().Text);
    }

    // ---- /template ----

    [Fact]
    public async Task Template_WithoutSubscription_AsksToChooseRegionFirst()
    {
        await SendAsync(Private, "/template {message}");

        Assert.Contains(ChooseRegionFirst, Reply());
        Assert.False(HasRow(PrivateChat));
    }

    [Fact]
    public async Task Template_NoArguments_ShowsCurrentTemplateAndPlaceholders()
    {
        _f.AddChannel(PrivateChat);

        await SendAsync(Private, "/template");

        var request = Assert.Single(_f.Bot.Sent);
        Assert.Equal(ParseMode.Html, request.ParseMode);
        Assert.Contains("Поточний шаблон", request.Text);
        Assert.Contains("<pre>{emoji} {message}\n\nДеталі: {site}</pre>", request.Text);
        foreach (var placeholder in MessageTemplate.Placeholders)
        {
            Assert.Contains($"<code>{{{placeholder.Name}}}</code>", request.Text);
        }
    }

    [Fact]
    public async Task Template_NoArguments_ShowsCustomTemplateHtmlEncoded()
    {
        _f.AddChannel(PrivateChat, c => c.Template = "<b>{where}</b> {message}");

        await SendAsync(Private, "/template");

        Assert.Contains("<pre>&lt;b&gt;{where}&lt;/b&gt; {message}</pre>", Reply());
    }

    [Fact]
    public async Task Template_Valid_IsSaved_AndRepliesWithPreview()
    {
        _f.AddChannel(PrivateChat);

        await SendAsync(Private, "/template <b>{where}</b> — {title} {message}");

        Assert.Equal("<b>{where}</b> — {title} {message}", Row(PrivateChat).Template);
        var request = Assert.Single(_f.Bot.Sent);
        Assert.StartsWith("Збережено.", request.Text);
        Assert.Contains("<b>м. Київ</b> — Екстрені відключення", request.Text);
        Assert.Equal(ParseMode.Html, request.ParseMode);
    }

    [Fact]
    public async Task Template_Valid_PreservesNewlines()
    {
        _f.AddChannel(PrivateChat);

        await SendAsync(Private, "/template {emoji} {message}\nдругий рядок");

        Assert.Equal("{emoji} {message}\nдругий рядок", Row(PrivateChat).Template);
    }

    [Fact]
    public async Task Template_Invalid_IsNotSaved_AndListsErrors()
    {
        _f.AddChannel(PrivateChat, c => c.Template = "{message}");

        await SendAsync(Private, "/template <script>x</script> {nope}");

        var text = Reply();
        Assert.StartsWith("Шаблон не збережено:", text);
        Assert.Contains("• ", text);
        Assert.Contains("{nope}", text);
        Assert.Contains("&lt;script&gt;", text);
        Assert.DoesNotContain("<script>", text);
        Assert.Equal("{message}", Row(PrivateChat).Template);
    }

    [Theory]
    [InlineData("reset")]
    [InlineData("RESET")]
    public async Task Template_Reset_ClearsCustomTemplate(string argument)
    {
        _f.AddChannel(PrivateChat, c => c.Template = "<b>{message}</b>");

        await SendAsync(Private, "/template " + argument);

        Assert.Equal("Шаблон скинуто до стандартного.", Reply());
        Assert.Null(Row(PrivateChat).Template);
    }

    [Fact]
    public async Task Template_InGroup_ByAdmin_IsSaved()
    {
        _f.AddChannel(GroupChat);

        await SendAsync(Group, "/template {message}", AdminUser);

        Assert.Equal("{message}", Row(GroupChat).Template);
    }

    // ---- status ----

    [Fact]
    public async Task Status_WithoutSubscription_AsksToChooseRegionFirst()
    {
        await PressAsync(Private, "status");

        Assert.Contains(ChooseRegionFirst, Edit().Text);
    }

    [Fact]
    public async Task Status_WithoutStoredState_SaysNoDataYet()
    {
        _f.AddChannel(PrivateChat);

        await PressAsync(Private, "status");

        var text = Edit().Text;
        Assert.Contains("м. Київ", text);
        Assert.Contains("Даних ще немає.", text);
        Assert.DoesNotContain("Перервірено", text);
    }

    [Theory]
    [InlineData(PopupKind.Emergency, "🚨 Діють екстрені відключення.")]
    [InlineData(PopupKind.Accident, "⚠️ Повідомлено про аварію в енергосистемі.")]
    [InlineData(PopupKind.Unknown, "ℹ️ На сайті є невизначене повідомлення.")]
    [InlineData(PopupKind.Stabilization, "Діють стабілізаційні відключення (за графіками).")]
    [InlineData(PopupKind.Cancelled, "Екстрених відключень немає.")]
    [InlineData(PopupKind.None, "Екстрених відключень немає.")]
    public async Task Status_ShowsTextForStoredPopupKind(PopupKind kind, string expected)
    {
        _f.AddChannel(PrivateChat, c => c.PartId = "right-bank");
        _f.Db.RegionStates.Add(new RegionState
        {
            RegionId = "kyiv",
            Kind = kind,
            Hash = "h",
            ObservedAt = new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero),
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _f.Db.SaveChanges();

        await PressAsync(Private, "status");

        var edit = Edit();
        Assert.Contains("м. Київ (Правий берег)", edit.Text);
        Assert.Contains(expected, edit.Text);
        Assert.Contains("Перервірено: 15.07 15:00 (Київ)", edit.Text);
        Assert.Contains(Buttons(edit.ReplyMarkup), b => b.CallbackData == "menu");
    }

    [Fact]
    public async Task Status_UsesStateOfTheSubscribedRegionOnly()
    {
        _f.AddChannel(PrivateChat, regionId: "odesa");
        _f.Db.RegionStates.Add(new RegionState
        {
            RegionId = "kyiv",
            Kind = PopupKind.Emergency,
            Hash = "h",
            ObservedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _f.Db.SaveChanges();

        await PressAsync(Private, "status");

        Assert.Contains("Даних ще немає.", Edit().Text);
    }
}
