using DtekBot.Api.Bot;
using DtekBot.Api.Checks;
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
    private const long PrivateChat = 500;
    private const long GroupChat = -1001;
    private const long ChannelChat = -1002;

    private const string OldButtonsAlert = "Бот тепер працює через редактор. Надішліть /start і відкрийте його.";

    private readonly DispatcherFixture _f = new();
    private readonly FakeWorkflowTrigger _trigger = new();
    private readonly FakeScheduleProvider _schedules = new();
    private readonly TestClock _clock = new(new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.FromHours(3)));
    private readonly BotUpdateHandler _handler;
    private int _updateId;

    public BotUpdateHandlerTests()
    {
        _handler = NewHandler("https://example.test");
    }

    public void Dispose() => _f.Dispose();

    private OutageAssessmentService NewAssessment() => new(
        _f.Db,
        new AddressCheckService(
            _f.Db, _f.Bot, _trigger, _f.Options, TimeProvider.System, NullLogger<AddressCheckService>.Instance),
        _schedules,
        _f.Options,
        _clock);

    private BotUpdateHandler NewHandler(string? publicUrl)
    {
        var values = new Dictionary<string, string?>();
        if (publicUrl is not null)
        {
            values["App:PublicUrl"] = publicUrl;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new BotUpdateHandler(_f.Bot, _f.Db, NewAssessment(), config, TimeProvider.System, NullLogger<BotUpdateHandler>.Instance);
    }

    // ---- helpers ----

    private static Chat MakeChat(long id, ChatType type, string? title = null, string? username = null) =>
        new() { Id = id, Type = type, Title = title, Username = username };

    private static Chat Private => MakeChat(PrivateChat, ChatType.Private, username: "pavlo");

    private static Chat Group => MakeChat(GroupChat, ChatType.Supergroup, "Мій будинок");

    private Update TextUpdate(Chat chat, string? text, long? from = null) => new()
    {
        Id = ++_updateId,
        Message = new Message
        {
            Id = ++_updateId,
            Chat = chat,
            Text = text,
            From = new User { Id = from ?? chat.Id, FirstName = "U" },
        },
    };

    private Update ChannelPostUpdate(string? text, long chatId = ChannelChat) => new()
    {
        Id = ++_updateId,
        ChannelPost = new Message { Id = ++_updateId, Chat = MakeChat(chatId, ChatType.Channel, "Канал"), Text = text },
    };

    private Update CallbackUpdate(Chat chat, string? data, long from = 5) => new()
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

    private Task HandleAsync(Update update) => _handler.HandleAsync(update, CancellationToken.None);

    private Task SendAsync(Chat chat, string? text, long? from = null) => HandleAsync(TextUpdate(chat, text, from));

    private static List<InlineKeyboardButton> Buttons(ReplyMarkup? markup) =>
        ((InlineKeyboardMarkup)markup!).InlineKeyboard.SelectMany(r => r).ToList();

    private Channel Row(long chatId) => _f.LoadChannel(chatId);

    private int CheckRows() => _f.Db.AddressChecks.AsNoTracking().Count();

    // ---- welcome in private chats ----

    [Theory]
    [InlineData("/start")]
    [InlineData("/help")]
    [InlineData("/menu")]
    [InlineData("/start@dtek_test_bot")]
    [InlineData("/region kyiv")]
    [InlineData("/stop")]
    [InlineData("/template {message}")]
    [InlineData("/check")]
    [InlineData("привіт")]
    [InlineData("Світло зникло")]
    [InlineData("  /start  ")]
    [InlineData("12345")]
    public async Task AnyTextInAPrivateChat_GetsTheWelcomeWithOneWebAppButton(string text)
    {
        await SendAsync(Private, text);

        var message = Assert.Single(_f.Bot.Sent);
        Assert.Single(_f.Bot.Requests);
        Assert.Equal(PrivateChat, message.ChatId.Identifier);
        Assert.Equal(default, message.ParseMode);
        Assert.Null(message.ReplyParameters);

        var paragraphs = message.Text.Split("\n\n");
        Assert.Equal(3, paragraphs.Length);
        Assert.Contains("Вітаю! Я повідомляю про екстрені відключення", paragraphs[0]);
        Assert.Contains("Усе налаштовується в редакторі", paragraphs[1]);
        Assert.Contains("додайте мене до нього адміністратором", paragraphs[2]);

        var button = Assert.Single(Buttons(message.ReplyMarkup));
        Assert.Contains("Відкрити редактор", button.Text);
        Assert.Equal("https://example.test/lite/", button.WebApp!.Url);
        Assert.Null(button.CallbackData);
        Assert.Single(((InlineKeyboardMarkup)message.ReplyMarkup!).InlineKeyboard);
    }

    [Fact]
    public async Task Welcome_ButtonTextIsTheEditorLabel()
    {
        await SendAsync(Private, "/start");

        Assert.Equal("🧩 Відкрити редактор", Assert.Single(Buttons(Assert.Single(_f.Bot.Sent).ReplyMarkup)).Text);
    }

    [Theory]
    [InlineData("https://example.test/")]
    [InlineData("https://example.test///")]
    [InlineData("https://example.test")]
    public async Task Welcome_TrimsTheTrailingSlashOfThePublicUrl(string publicUrl)
    {
        var handler = NewHandler(publicUrl);

        await handler.HandleAsync(TextUpdate(Private, "/start"), CancellationToken.None);

        var button = Assert.Single(Buttons(Assert.Single(_f.Bot.Sent).ReplyMarkup));
        Assert.Equal("https://example.test/lite/", button.WebApp!.Url);
    }

    [Fact]
    public async Task Welcome_WithAPathInThePublicUrl_KeepsIt()
    {
        var handler = NewHandler("https://example.test/bot/");

        await handler.HandleAsync(TextUpdate(Private, "/start"), CancellationToken.None);

        Assert.Equal("https://example.test/bot/lite/", Assert.Single(Buttons(Assert.Single(_f.Bot.Sent).ReplyMarkup)).WebApp!.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/")]
    public async Task Welcome_WithoutPublicUrl_HasNoKeyboard(string? publicUrl)
    {
        var handler = NewHandler(publicUrl);

        await handler.HandleAsync(TextUpdate(Private, "/start"), CancellationToken.None);

        var message = Assert.Single(_f.Bot.Sent);
        Assert.Null(message.ReplyMarkup);
        Assert.Contains("Вітаю! Я повідомляю про екстрені відключення", message.Text);
        Assert.Equal(3, message.Text.Split("\n\n").Length);
    }

    [Fact]
    public async Task Welcome_DoesNotTouchTheDatabase()
    {
        _f.AddChannel(PrivateChat);
        var before = Row(PrivateChat);

        await SendAsync(Private, "/region odesa");

        var after = Row(PrivateChat);
        Assert.Equal(before.RegionId, after.RegionId);
        Assert.Equal(before.IsActive, after.IsActive);
        Assert.Single(_f.Db.Channels);
        Assert.Empty(_f.Db.ChannelAdmins);
    }

    [Fact]
    public async Task PrivateMessageWithThePhrase_GetsTheWelcome_NotAnAssessment()
    {
        _f.AddChannel(PrivateChat, c =>
        {
            c.OutageTrigger = true;
            c.AddressStreet = "вул. Драгоманова";
            c.AddressHouse = "12";
        });

        await SendAsync(Private, "🔴 14:24 Світло зникло");

        Assert.Contains("Вітаю!", Assert.Single(_f.Bot.Sent).Text);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
    }

    [Theory]
    [InlineData(ChatType.Group)]
    [InlineData(ChatType.Supergroup)]
    public async Task MessagesInGroups_AreIgnored(ChatType type)
    {
        var chat = MakeChat(GroupChat, type, "Чат");

        await SendAsync(chat, "/start", 42);
        await SendAsync(chat, "привіт", 42);
        await SendAsync(chat, "Світло зникло", 42);

        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task MessagesInGroups_WithAnActiveTriggerChannelRow_AreStillIgnored()
    {
        _f.AddChannel(GroupChat, c =>
        {
            c.OutageTrigger = true;
            c.AddressStreet = "вул. Драгоманова";
            c.AddressHouse = "12";
        });

        await SendAsync(Group, "🔴 14:24 Світло зникло", 42);

        Assert.Empty(_f.Bot.Requests);
        Assert.Equal(0, CheckRows());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t")]
    public async Task UpdatesWithoutText_AreIgnored(string? text)
    {
        await SendAsync(Private, text);

        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task UpdateWithoutAnyPayload_IsIgnored()
    {
        await HandleAsync(new Update { Id = 1 });

        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task EachPrivateMessage_GetsItsOwnWelcome()
    {
        await SendAsync(Private, "/start");
        await SendAsync(Private, "/start");

        Assert.Equal(2, _f.Bot.Sent.Count());
    }

    // ---- callbacks from old messages ----

    [Theory]
    [InlineData("menu")]
    [InlineData("regions")]
    [InlineData("r:kyiv")]
    [InlineData("p:kyiv:all")]
    [InlineData("status")]
    [InlineData("stop")]
    [InlineData("cfg")]
    [InlineData("cfg:filter")]
    [InlineData("cfg:sched")]
    [InlineData("chk")]
    [InlineData("whatever")]
    [InlineData(null)]
    public async Task CallbackFromAnOldMessage_GetsExactlyOneAlert_AndNothingElse(string? data)
    {
        _f.AddChannel(PrivateChat);
        var before = Row(PrivateChat);

        await HandleAsync(CallbackUpdate(Private, data));

        var answer = Assert.Single(_f.Bot.Requests);
        var request = Assert.IsType<AnswerCallbackQueryRequest>(answer);
        Assert.StartsWith("cb", request.CallbackQueryId);
        Assert.Equal(OldButtonsAlert, request.Text);
        Assert.True(request.ShowAlert);

        var after = Row(PrivateChat);
        Assert.Equal(before.Filter, after.Filter);
        Assert.Equal(before.NotifyOnEnd, after.NotifyOnEnd);
        Assert.Equal(before.EditInPlace, after.EditInPlace);
        Assert.Equal(before.IsPaused, after.IsPaused);
        Assert.Equal(before.IsActive, after.IsActive);
        Assert.Equal(before.ScheduleNotify, after.ScheduleNotify);
        Assert.Equal(before.OutageTrigger, after.OutageTrigger);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
    }

    [Theory]
    [InlineData(ChatType.Group)]
    [InlineData(ChatType.Supergroup)]
    [InlineData(ChatType.Channel)]
    public async Task CallbackFromAnOldMessage_InAnyChat_GetsTheSameAlert(ChatType type)
    {
        _f.AddChannel(GroupChat);

        await HandleAsync(CallbackUpdate(MakeChat(GroupChat, type, "Чат"), "cfg:pause", 42));

        var request = Assert.IsType<AnswerCallbackQueryRequest>(Assert.Single(_f.Bot.Requests));
        Assert.Equal(OldButtonsAlert, request.Text);
        Assert.True(request.ShowAlert);
        Assert.False(Row(GroupChat).IsPaused);
    }

    [Fact]
    public async Task CallbackWithoutAMessage_GetsTheAlertToo()
    {
        var update = CallbackUpdate(Private, "menu");
        update.CallbackQuery!.Message = null;

        await HandleAsync(update);

        var request = Assert.IsType<AnswerCallbackQueryRequest>(Assert.Single(_f.Bot.Requests));
        Assert.Equal(OldButtonsAlert, request.Text);
    }

    [Fact]
    public async Task CallbackFromAGroupNonAdmin_IsNotRefusedAnyMore_ItJustGetsTheAlert()
    {
        _f.AddChannel(GroupChat);
        _f.Bot.SetStatus(42, ChatMemberStatus.Member);

        await HandleAsync(CallbackUpdate(Group, "cfg:filter", 42));

        Assert.Empty(_f.Bot.Requests.OfType<GetChatMemberRequest>());
        Assert.Equal(OldButtonsAlert, Assert.IsType<AnswerCallbackQueryRequest>(Assert.Single(_f.Bot.Requests)).Text);
    }

    // ---- channel posts: «Світло зникло» ----

    private const string SamplePost = "🔴 14:24 Світло зникло\n🕓 Воно було 17год 23хв\n🗓 Очікуємо за графіком о 18:00";

    private static readonly System.Text.RegularExpressions.Regex PowerOutPattern =
        new(@"світло\s+зникло", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private Channel AddTriggerChannel(Action<Channel>? configure = null, bool withAddress = true) =>
        _f.AddChannel(ChannelChat, c =>
        {
            c.OutageTrigger = true;
            if (withAddress)
            {
                c.AddressStreet = "вул. Драгоманова";
                c.AddressHouse = "12";
                c.AddressLabel = "вул. Драгоманова, 12";
                c.GroupId = "45.1";
            }

            configure?.Invoke(c);
        });

    private async Task<Update> PostAsync(string text)
    {
        var update = ChannelPostUpdate(text);
        await HandleAsync(update);
        return update;
    }

    private void StoreRegionState(PopupKind kind, string regionId = "kyiv")
    {
        _f.Db.RegionStates.Add(new RegionState
        {
            RegionId = regionId,
            Kind = kind,
            Hash = "h",
            ObservedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _f.Db.SaveChanges();
    }

    private void StoreGroupState(string today, string groupId = "45.1", string regionId = "kyiv")
    {
        _f.Db.GroupStates.Add(new GroupState
        {
            RegionId = regionId,
            GroupId = groupId,
            Today = today,
            Tomorrow = "ScheduleApplies",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        _f.Db.SaveChanges();
    }

    private static void AssertDoesNotSelfTrigger(string text) =>
        Assert.DoesNotMatch(PowerOutPattern, text);

    [Fact]
    public async Task PowerOutPost_WithAddress_RepliesToThePostWithQueuedCheck()
    {
        AddTriggerChannel();

        var post = await PostAsync(SamplePost);

        var reply = Assert.Single(_f.Bot.Sent);
        Assert.Equal(ChannelChat, reply.ChatId.Identifier);
        Assert.Equal(post.ChannelPost!.Id, reply.ReplyParameters?.MessageId);
        Assert.Contains("⏳", reply.Text);
        Assert.DoesNotContain("Для вашої черги", reply.Text);
        Assert.Equal(default, reply.ParseMode);
        Assert.Null(reply.LinkPreviewOptions);

        var check = Assert.Single(_f.Db.AddressChecks.AsNoTracking());
        Assert.Equal(ChannelChat, check.ChatId);
        Assert.Equal(AddressCheckStatus.Pending, check.Status);
        Assert.Equal(["check-addresses"], _trigger.Events);
        AssertDoesNotSelfTrigger(reply.Text);
    }

    [Theory]
    [InlineData("Світло зникло")]
    [InlineData("світло зникло")]
    [InlineData("СВІТЛО ЗНИКЛО")]
    [InlineData("СвІтЛо ЗнИкЛо")]
    [InlineData("Світло   зникло")]
    [InlineData("Світло\nзникло")]
    [InlineData("Світло\r\nзникло")]
    [InlineData("Світло\tзникло")]
    [InlineData("Увага! світло зникло!!!")]
    [InlineData("🔴 14:24 Світло зникло")]
    public async Task PowerOutPost_PhraseVariations_AreRecognised(string text)
    {
        AddTriggerChannel();

        await PostAsync(text);

        Assert.Contains("⏳", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task PowerOutPost_CustomPhrase_ReplacesTheDefault_AndPostsAreRemembered()
    {
        AddTriggerChannel(c => c.OutageTriggerPattern = "зараз світло зникло");

        await PostAsync("🔴 14:24 Світло зникло");
        Assert.Empty(_f.Bot.Sent);

        await PostAsync("Увага, зараз світло зникло!");
        Assert.Contains("⏳", Assert.Single(_f.Bot.Sent).Text);

        var recent = OutageTriggerMatcher.ParseRecent(Row(ChannelChat).RecentPosts);
        Assert.Equal(["🔴 14:24 Світло зникло", "Увага, зараз світло зникло!"], recent);
    }

    [Theory]
    [InlineData("🟢 15:10 Світло з'явилось\n🕓 Його не було 0год 46хв")]
    [InlineData("Світло є")]
    [InlineData("Світло не зникло")]
    [InlineData("Світла зникло")]
    [InlineData("Світлозникло")]
    [InlineData("Світло, зникло")]
    [InlineData("зникло світло")]
    [InlineData("привіт всім")]
    public async Task UnrelatedChannelPosts_AreIgnored(string text)
    {
        AddTriggerChannel();

        await PostAsync(text);

        Assert.Empty(_f.Bot.Requests);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
    }

    [Theory]
    [InlineData("/start")]
    [InlineData("/help")]
    [InlineData("/region kyiv")]
    [InlineData("/template {message} деталі")]
    [InlineData("/check")]
    [InlineData("/stop")]
    public async Task ChannelPostCommands_ProduceNoReply_AndChangeNothing(string text)
    {
        var channel = AddTriggerChannel();

        await PostAsync(text);

        Assert.Empty(_f.Bot.Requests);
        Assert.Equal(0, CheckRows());
        var after = Row(ChannelChat);
        Assert.Equal(channel.IsActive, after.IsActive);
        Assert.Equal(channel.RegionId, after.RegionId);
        Assert.Null(after.Template);
    }

    [Fact]
    public async Task SlashPrefixedPost_WithThePhrase_IsNotATrigger()
    {
        AddTriggerChannel();

        await PostAsync("/start Світло зникло");

        Assert.Empty(_f.Bot.Requests);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ChannelPostWithoutText_IsIgnored(string? text)
    {
        AddTriggerChannel();

        await HandleAsync(ChannelPostUpdate(text));

        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task PowerOutPost_WhenTriggerIsOff_DoesNothing()
    {
        AddTriggerChannel(c => c.OutageTrigger = false);

        await PostAsync(SamplePost);

        Assert.Empty(_f.Bot.Requests);
        Assert.Equal(0, CheckRows());
    }

    [Fact]
    public async Task PowerOutPost_WhenChannelIsInactive_DoesNothing()
    {
        AddTriggerChannel(c => c.IsActive = false);

        await PostAsync(SamplePost);

        Assert.Empty(_f.Bot.Requests);
        Assert.Equal(0, CheckRows());
    }

    [Fact]
    public async Task PowerOutPost_WhenChannelHasNoRow_DoesNothing()
    {
        await PostAsync(SamplePost);

        Assert.Empty(_f.Bot.Requests);
        Assert.Equal(0, CheckRows());
    }

    [Fact]
    public async Task PowerOutPost_OnlyConcernsTheChannelItWasPostedIn()
    {
        AddTriggerChannel();
        _f.AddChannel(-1999, c => { c.OutageTrigger = true; c.AddressStreet = "s"; c.AddressHouse = "1"; });

        await PostAsync(SamplePost);

        Assert.Equal(ChannelChat, Assert.Single(_f.Bot.Sent).ChatId.Identifier);
        Assert.Equal(ChannelChat, Assert.Single(_f.Db.AddressChecks.AsNoTracking()).ChatId);
    }

    [Theory]
    [InlineData(PopupKind.Emergency)]
    [InlineData(PopupKind.Accident)]
    [InlineData(PopupKind.Unknown)]
    public async Task PowerOutPost_WithRegionWideAlert_RepliesWithContext_AndSkipsAddressCheck(PopupKind kind)
    {
        AddTriggerChannel();
        StoreRegionState(kind);

        var post = await PostAsync(SamplePost);

        var reply = Assert.Single(_f.Bot.Sent);
        Assert.StartsWith("ℹ️ На сайті ДТЕК зараз діє загальне повідомлення", reply.Text);
        Assert.Contains("перевірку за адресою не запускаю", reply.Text);
        Assert.Equal(post.ChannelPost!.Id, reply.ReplyParameters?.MessageId);
        Assert.Equal(default, reply.ParseMode);
        Assert.DoesNotContain("⏳", reply.Text);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
        AssertDoesNotSelfTrigger(reply.Text);
    }

    [Fact]
    public async Task PowerOutPost_WithRegionWideAlert_IgnoresGroupStateAndMissingAddress()
    {
        AddTriggerChannel(withAddress: false);
        StoreRegionState(PopupKind.Emergency);
        StoreGroupState(GroupStatuses.Emergency);

        await PostAsync(SamplePost);

        var reply = Assert.Single(_f.Bot.Sent);
        Assert.StartsWith("ℹ️", reply.Text);
        Assert.DoesNotContain("Для вашої черги", reply.Text);
        Assert.Equal(0, CheckRows());
    }

    [Theory]
    [InlineData(PopupKind.None)]
    [InlineData(PopupKind.Stabilization)]
    [InlineData(PopupKind.Cancelled)]
    public async Task PowerOutPost_WithoutRegionWideAlert_RunsAddressCheck(PopupKind kind)
    {
        AddTriggerChannel();
        StoreRegionState(kind);

        await PostAsync(SamplePost);

        Assert.Contains("⏳", Assert.Single(_f.Bot.Sent).Text);
        Assert.Equal(1, CheckRows());
        Assert.Single(_trigger.Events);
    }

    [Fact]
    public async Task PowerOutPost_RegionStateOfAnotherRegion_IsIgnored()
    {
        AddTriggerChannel();
        StoreRegionState(PopupKind.Emergency, "odesa");

        await PostAsync(SamplePost);

        Assert.Contains("⏳", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task PowerOutPost_WithGroupEmergency_PrependsGroupLine_ThenQueuedMessage()
    {
        AddTriggerChannel();
        StoreGroupState(GroupStatuses.Emergency);

        await PostAsync(SamplePost);

        var text = Assert.Single(_f.Bot.Sent).Text;
        var parts = text.Split("\n\n");
        Assert.Equal(2, parts.Length);
        Assert.Equal("🚨 Для вашої черги 45.1 зараз діють екстрені відключення (за даними Yasno).", parts[0]);
        Assert.StartsWith("⏳", parts[1]);
        Assert.Equal(1, CheckRows());
        AssertDoesNotSelfTrigger(text);
    }

    [Fact]
    public async Task PowerOutPost_GroupEmergencyWithoutAddress_ShowsGroupLineAndNoAddressText()
    {
        AddTriggerChannel(c => { c.AddressStreet = null; c.AddressHouse = null; });
        StoreGroupState(GroupStatuses.Emergency);

        await PostAsync(SamplePost);

        var text = Assert.Single(_f.Bot.Sent).Text;
        Assert.StartsWith("🚨 Для вашої черги 45.1", text);
        Assert.Contains("Спершу оберіть адресу", text);
        Assert.Equal(0, CheckRows());
    }

    [Theory]
    [InlineData("ScheduleApplies", "45.1", "kyiv")]
    [InlineData("WaitingForSchedule", "45.1", "kyiv")]
    [InlineData(GroupStatuses.Emergency, "46.1", "kyiv")]
    [InlineData(GroupStatuses.Emergency, "45.1", "odesa")]
    public async Task PowerOutPost_GroupLine_OnlyForOwnGroupInEmergency(string today, string groupId, string regionId)
    {
        AddTriggerChannel();
        StoreGroupState(today, groupId, regionId);

        await PostAsync(SamplePost);

        var text = Assert.Single(_f.Bot.Sent).Text;
        Assert.DoesNotContain("Для вашої черги", text);
        Assert.StartsWith("⏳", text);
    }

    [Fact]
    public async Task PowerOutPost_WithoutGroupId_HasNoGroupLine()
    {
        AddTriggerChannel(c => c.GroupId = null);
        StoreGroupState(GroupStatuses.Emergency);

        await PostAsync(SamplePost);

        Assert.DoesNotContain("Для вашої черги", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task PowerOutPost_WithoutAddress_RepliesWithNoAddressText()
    {
        AddTriggerChannel(withAddress: false);

        await PostAsync(SamplePost);

        var text = Assert.Single(_f.Bot.Sent).Text;
        Assert.Contains("Спершу оберіть адресу", text);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
        AssertDoesNotSelfTrigger(text);
    }

    [Fact]
    public async Task PowerOutPost_ForRegionWithoutGroups_AsksForCity()
    {
        _f.AddChannel(ChannelChat, c =>
        {
            c.OutageTrigger = true;
            c.AddressStreet = "s";
            c.AddressHouse = "1";
        }, "odesa");

        await PostAsync(SamplePost);

        Assert.Contains("населений пункт", Assert.Single(_f.Bot.Sent).Text);
        Assert.Equal(0, CheckRows());
    }

    [Fact]
    public async Task PowerOutPost_Twice_SecondReportsPendingCheck()
    {
        AddTriggerChannel();
        await PostAsync(SamplePost);
        _f.Bot.Requests.Clear();

        await PostAsync(SamplePost);

        var text = Assert.Single(_f.Bot.Sent).Text;
        Assert.Contains("ще виконують", text);
        Assert.Equal(1, CheckRows());
        Assert.Single(_trigger.Events);
        AssertDoesNotSelfTrigger(text);
    }

    [Fact]
    public async Task PowerOutPost_WithFreshCachedResult_RepliesWithItWithoutQueueing()
    {
        AddTriggerChannel();
        _f.Db.AddressResults.Add(new AddressResult
        {
            RegionId = "kyiv",
            Street = "вул. Драгоманова",
            House = "12",
            Found = true,
            SubType = "Планове",
            StartDate = "14:00",
            EndDate = "18:00",
            CheckedAt = DateTimeOffset.UtcNow,
        });
        _f.Db.SaveChanges();

        await PostAsync(SamplePost);

        var text = Assert.Single(_f.Bot.Sent).Text;
        Assert.StartsWith("⏰", text);
        Assert.Equal(0, CheckRows());
        AssertDoesNotSelfTrigger(text);
    }

    // ---- assessment: the schedule explains the outage ----

    private static readonly DateTimeOffset ScheduledOff = ScheduleFixtures.Kyiv(15, 12);

    private const string NoticeStart = "🗓 За графіком (черга 45.1) зараз планове стабілізаційне відключення з 11:00.";

    [Fact]
    public async Task PowerOutPost_WhenScheduleSaysOff_RepliesWithTheNotice_InsteadOfQueuing()
    {
        _schedules.Snapshots["kyiv"] = ScheduleFixtures.RealSnapshot();
        _clock.Now = ScheduledOff;
        AddTriggerChannel();

        var post = await PostAsync(SamplePost);

        var reply = Assert.Single(_f.Bot.Sent);
        Assert.StartsWith(NoticeStart, reply.Text);
        Assert.Equal(post.ChannelPost!.Id, reply.ReplyParameters?.MessageId);
        Assert.Equal(0, CheckRows());
        Assert.Empty(_trigger.Events);
        AssertDoesNotSelfTrigger(reply.Text);
    }

    [Fact]
    public async Task PowerOutPost_WhenScheduleSaysOn_StillQueuesTheAddressCheck()
    {
        _schedules.Snapshots["kyiv"] = ScheduleFixtures.RealSnapshot();
        _clock.Now = ScheduleFixtures.Kyiv(15, 15);
        AddTriggerChannel();

        await PostAsync(SamplePost);

        Assert.Contains("⏳", Assert.Single(_f.Bot.Sent).Text);
        Assert.Equal(1, CheckRows());
    }

    [Fact]
    public async Task PowerOutPost_ScheduleOffAndRegionWideAlert_AppendsTheWarning()
    {
        _schedules.Snapshots["kyiv"] = ScheduleFixtures.RealSnapshot();
        _clock.Now = ScheduledOff;
        AddTriggerChannel();
        StoreRegionState(PopupKind.Emergency);

        await PostAsync(SamplePost);

        var text = Assert.Single(_f.Bot.Sent).Text;
        Assert.StartsWith(NoticeStart, text);
        Assert.EndsWith(
            "\n\n⚠️ Увага: на сайті ДТЕК зараз діє загальне повідомлення про екстрені відключення чи аварію по регіону, тому графік може не діяти.",
            text);
        Assert.Equal(0, CheckRows());
    }

    [Fact]
    public async Task PowerOutPost_ScheduleOnAndRegionWideAlert_RepliesOnlyWithTheContext()
    {
        _schedules.Snapshots["kyiv"] = ScheduleFixtures.RealSnapshot();
        _clock.Now = ScheduleFixtures.Kyiv(15, 15);
        AddTriggerChannel();
        StoreRegionState(PopupKind.Accident);
        StoreGroupState(GroupStatuses.Emergency);

        await PostAsync(SamplePost);

        var text = Assert.Single(_f.Bot.Sent).Text;
        Assert.StartsWith("ℹ️ На сайті ДТЕК зараз діє загальне повідомлення", text);
        Assert.DoesNotContain("Для вашої черги", text);
        Assert.Equal(0, CheckRows());
    }

    // ---- channel membership (my_chat_member) ----

    private const long NewChannel = -1003;
    private const long Adder = 77;

    private static Chat NewChannelChat(long id = NewChannel, string? title = "Мій канал", string? username = null) =>
        new() { Id = id, Type = ChatType.Channel, Title = title, Username = username };

    private static readonly User BotUser = new() { Id = 1, FirstName = "Bot", IsBot = true };

    private Update MembershipUpdate(Chat chat, ChatMember newMember, long fromId = Adder, bool fromIsBot = false, ChatMember? oldMember = null) => new()
    {
        Id = ++_updateId,
        MyChatMember = new ChatMemberUpdated
        {
            Chat = chat,
            From = new User { Id = fromId, FirstName = "A", IsBot = fromIsBot },
            Date = DateTime.UtcNow,
            OldChatMember = oldMember ?? new ChatMemberLeft { User = BotUser },
            NewChatMember = newMember,
        },
    };

    private static ChatMemberAdministrator AdminMember(bool canPost = true) =>
        new() { User = BotUser, CanPostMessages = canPost };

    private static ChatMemberLeft LeftMember() => new() { User = BotUser };

    private Task MembershipAsync(Chat chat, ChatMember newMember, long fromId = Adder, bool fromIsBot = false, ChatMember? oldMember = null) =>
        HandleAsync(MembershipUpdate(chat, newMember, fromId, fromIsBot, oldMember));

    private const string ConnectedText =
        "✅ Канал «Мій канал» підключено.\n\nВідкрийте редактор (кнопка меню біля поля вводу або /start), щоб обрати регіон і адресу.";

    [Fact]
    public async Task BotMadeChannelAdmin_CreatesAnInactiveRow_LinksTheAdder_AndSendsADm()
    {
        await MembershipAsync(NewChannelChat(), AdminMember());

        var channel = Row(NewChannel);
        Assert.False(channel.IsActive);
        Assert.Equal("Мій канал", channel.Title);
        Assert.Equal("", channel.RegionId);

        var link = Assert.Single(_f.Db.ChannelAdmins.AsNoTracking());
        Assert.Equal((NewChannel, Adder), (link.ChatId, link.UserId));

        var dm = Assert.Single(_f.Bot.Sent);
        Assert.Equal(Adder, dm.ChatId.Identifier);
        Assert.Equal(ConnectedText, dm.Text);
        Assert.Equal(default, dm.ParseMode);
        Assert.Null(dm.ReplyMarkup);
    }

    [Fact]
    public async Task BotWithoutPostRight_GetsAWarningInTheDm()
    {
        await MembershipAsync(NewChannelChat(), AdminMember(canPost: false));

        Assert.Equal(
            ConnectedText + "\n\n⚠️ Надайте боту право публікувати повідомлення, інакше він не зможе писати в канал.",
            Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task BotWithPostRight_GetsNoWarning()
    {
        await MembershipAsync(NewChannelChat(), AdminMember(true));

        Assert.DoesNotContain("⚠️", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task Title_FallsBackToUsername_ThenKeepsTheExistingTitle()
    {
        await MembershipAsync(NewChannelChat(title: null, username: "mychannel"), AdminMember());
        Assert.Equal("mychannel", Row(NewChannel).Title);
        Assert.Contains("«mychannel»", Assert.Single(_f.Bot.Sent).Text);

        _f.Bot.Requests.Clear();
        await MembershipAsync(NewChannelChat(title: null, username: null), AdminMember());

        Assert.Equal("mychannel", Row(NewChannel).Title);
        Assert.Contains("«mychannel»", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task Title_IsRefreshedFromTheChat()
    {
        _f.AddChannel(NewChannel, c => c.Title = "Старий");

        await MembershipAsync(NewChannelChat(title: "Новий"), AdminMember());

        Assert.Equal("Новий", Row(NewChannel).Title);
    }

    [Fact]
    public async Task ChannelWithoutAnyName_UsesTheIdInTheDm()
    {
        await MembershipAsync(NewChannelChat(title: null, username: null), AdminMember());

        Assert.Contains($"«{NewChannel}»", Assert.Single(_f.Bot.Sent).Text);
    }

    [Fact]
    public async Task ExistingChannelRowManagedByAnotherAdmin_KeepsItsSettings_AndItsActiveState()
    {
        _f.AddChannel(NewChannel, c =>
        {
            c.PartId = "right-bank";
            c.Template = "<b>{message}</b>";
            c.Filter = AlertFilter.EmergencyOnly;
            c.OutageTrigger = true;
            c.ScheduleNotify = true;
            c.GroupId = "45.1";
            c.IsActive = true;
        });
        _f.Db.ChannelAdmins.Add(new ChannelAdmin { ChatId = NewChannel, UserId = 78, LinkedAt = DateTimeOffset.UtcNow });
        _f.Db.SaveChanges();

        await MembershipAsync(NewChannelChat(), AdminMember());

        var channel = Row(NewChannel);
        Assert.True(channel.IsActive);
        Assert.Equal("kyiv", channel.RegionId);
        Assert.Equal("right-bank", channel.PartId);
        Assert.Equal("<b>{message}</b>", channel.Template);
        Assert.Equal(AlertFilter.EmergencyOnly, channel.Filter);
        Assert.True(channel.OutageTrigger);
        Assert.True(channel.ScheduleNotify);
        Assert.Equal("45.1", channel.GroupId);
        Assert.Single(_f.Db.Channels);
    }

    [Fact]
    public async Task ExistingInactiveRow_StaysInactive()
    {
        _f.AddChannel(NewChannel, c => c.IsActive = false);

        await MembershipAsync(NewChannelChat(), AdminMember());

        Assert.False(Row(NewChannel).IsActive);
    }

    [Fact]
    public async Task AdderAlreadyLinked_IsNotLinkedTwice()
    {
        _f.Db.ChannelAdmins.Add(new ChannelAdmin { ChatId = NewChannel, UserId = Adder, LinkedAt = DateTimeOffset.UtcNow });
        _f.Db.SaveChanges();

        await MembershipAsync(NewChannelChat(), AdminMember());

        Assert.Single(_f.Db.ChannelAdmins.AsNoTracking());
    }

    [Fact]
    public async Task AnotherAdderOfTheSameChannel_IsLinkedToo()
    {
        await MembershipAsync(NewChannelChat(), AdminMember(), fromId: Adder);
        await MembershipAsync(NewChannelChat(), AdminMember(), fromId: 78);

        var users = _f.Db.ChannelAdmins.AsNoTracking().Select(a => a.UserId).OrderBy(u => u).ToList();
        Assert.Equal([Adder, 78], users);
    }

    [Fact]
    public async Task Repeated_AdminUpdates_AreIdempotentForTheDatabase()
    {
        await MembershipAsync(NewChannelChat(), AdminMember());
        await MembershipAsync(NewChannelChat(), AdminMember());
        await MembershipAsync(NewChannelChat(), AdminMember());

        Assert.Single(_f.Db.Channels);
        Assert.Single(_f.Db.ChannelAdmins);
    }

    [Fact]
    public async Task LinkedAdder_CanThenManageTheChannel()
    {
        _f.Bot.SetStatus(Adder, ChatMemberStatus.Administrator);
        await MembershipAsync(NewChannelChat(), AdminMember());
        var access = new ChatAccessService(_f.Bot, _f.Db, NullLogger<ChatAccessService>.Instance);

        Assert.True(await access.CanManageAsync(NewChannel, Adder, CancellationToken.None));
        Assert.False(await access.CanManageAsync(NewChannel, 999, CancellationToken.None));
    }

    [Fact]
    public async Task AdderWhoIsABot_GetsNoDm_AndNoLink_ButTheRowIsCreated()
    {
        await MembershipAsync(NewChannelChat(), AdminMember(), fromId: 4242, fromIsBot: true);

        Assert.Empty(_f.Bot.Requests);
        Assert.Empty(_f.Db.ChannelAdmins);
        Assert.Equal("Мій канал", Row(NewChannel).Title);
    }

    [Fact]
    public async Task DmFailure_IsSwallowed_AndTheChannelIsStillConnected()
    {
        _f.Bot.Failure = _ => FakeTelegramBotClient.Forbidden();

        await MembershipAsync(NewChannelChat(), AdminMember());

        Assert.Single(_f.Bot.Requests);
        Assert.Equal("Мій канал", Row(NewChannel).Title);
        Assert.Single(_f.Db.ChannelAdmins);
    }

    [Theory]
    [InlineData(ChatMemberStatus.Left)]
    [InlineData(ChatMemberStatus.Kicked)]
    public async Task BotRemoved_ResetsTheChannelToANewUnsubscribedOne_AndDropsItsThreadsAndOpenDeliveries(ChatMemberStatus status)
    {
        _f.AddChannel(NewChannel, c =>
        {
            ChannelAssert.Customize(c);
            c.Title = "Мій канал";
        });
        _f.AddOpenDelivery(NewChannel, 1);
        _f.AddOpenDelivery(ChannelChat, 2);
        _f.Db.Deliveries.Add(new Delivery { ChatId = NewChannel, RegionId = "kyiv", TelegramMessageId = 3, ContentHash = "h", IsClosed = true, UpdatedAt = DateTimeOffset.UtcNow });
        foreach (var chat in new[] { NewChannel, ChannelChat })
        {
            _f.Db.ScheduleThreads.Add(new ScheduleThread { ChatId = chat, RegionId = "kyiv", GroupId = "45.1", MessageId = 5, UpdatedAt = DateTimeOffset.UtcNow });
            _f.Db.SchedulePostThreads.Add(new SchedulePostThread { ChatId = chat, RegionId = "kyiv", GroupId = "45.1", MessageId = 6, DateKey = "2026-07-15", UpdatedAt = DateTimeOffset.UtcNow });
        }

        _f.Db.SaveChanges();
        ChatMember removed = status == ChatMemberStatus.Left
            ? new ChatMemberLeft { User = BotUser }
            : new ChatMemberBanned { User = BotUser };

        await MembershipAsync(NewChannelChat(), removed);

        var channel = Row(NewChannel);
        ChannelAssert.IsCleanUnsubscribed(channel);
        Assert.Equal("Мій канал", channel.Title);
        Assert.Equal([ChannelChat], _f.Db.ScheduleThreads.AsNoTracking().Select(t => t.ChatId).ToList());
        Assert.Equal([ChannelChat], _f.Db.SchedulePostThreads.AsNoTracking().Select(t => t.ChatId).ToList());
        Assert.Empty(_f.Db.Deliveries.AsNoTracking().Where(d => d.ChatId == NewChannel && !d.IsClosed));
        Assert.Single(_f.Db.Deliveries.AsNoTracking().Where(d => d.ChatId == NewChannel && d.IsClosed));
        Assert.Single(_f.Db.Deliveries.AsNoTracking().Where(d => d.ChatId == ChannelChat));
        Assert.Empty(_f.Bot.Requests);
    }

    // Re-adding the bot after the channel's last admin removed it from the list gives a brand-new channel.
    [Fact]
    public async Task ReAdded_AfterTheLastAdminUnlinked_IsACleanUnsubscribedChannel()
    {
        _f.AddChannel(NewChannel, c =>
        {
            ChannelAssert.Customize(c);
            c.Title = "Старий";
        });

        await MembershipAsync(NewChannelChat(title: "Новий"), AdminMember());

        var channel = Row(NewChannel);
        ChannelAssert.IsCleanUnsubscribed(channel);
        Assert.Equal("Новий", channel.Title);
        Assert.Equal(Adder, Assert.Single(_f.Db.ChannelAdmins.AsNoTracking()).UserId);
    }

    [Fact]
    public async Task ReAdded_WhileAnotherAdminStillManagesIt_KeepsTheirSettings()
    {
        _f.AddChannel(NewChannel, ChannelAssert.Customize);
        _f.Db.ChannelAdmins.Add(new ChannelAdmin { ChatId = NewChannel, UserId = 78, LinkedAt = DateTimeOffset.UtcNow });
        _f.Db.SaveChanges();

        await MembershipAsync(NewChannelChat(), AdminMember());

        var channel = Row(NewChannel);
        Assert.True(channel.IsActive);
        Assert.Equal("kyiv", channel.RegionId);
        Assert.Equal("45.1", channel.GroupId);
        Assert.Equal(2, _f.Db.ChannelAdmins.AsNoTracking().Count());
    }

    // Telegram also sends my_chat_member when the bot's rights change; that must not bring a removed channel back.
    [Fact]
    public async Task RightsChange_OfAChannelNobodyManages_DoesNotRelinkTheActor_NorTouchSettings()
    {
        _f.AddChannel(NewChannel, c => c.Title = "Мій канал");

        await MembershipAsync(NewChannelChat(), AdminMember(), oldMember: AdminMember(canPost: false));

        Assert.Empty(_f.Db.ChannelAdmins);
        Assert.Empty(_f.Bot.Requests);
        Assert.Equal("Мій канал", Row(NewChannel).Title);
    }

    [Fact]
    public async Task RightsChange_OfAManagedChannel_KeepsEverything_WithoutADm()
    {
        _f.AddChannel(NewChannel, ChannelAssert.Customize);
        _f.Db.ChannelAdmins.Add(new ChannelAdmin { ChatId = NewChannel, UserId = 78, LinkedAt = DateTimeOffset.UtcNow });
        _f.Db.SaveChanges();

        await MembershipAsync(NewChannelChat(), AdminMember(), fromId: 79, oldMember: AdminMember(canPost: false));

        Assert.True(Row(NewChannel).IsActive);
        Assert.Equal([78L], _f.Db.ChannelAdmins.AsNoTracking().Select(a => a.UserId).ToList());
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task UnlinkedThenReAdded_EndToEnd_IsCleanAndUnsubscribed()
    {
        await MembershipAsync(NewChannelChat(), AdminMember());
        _f.Db.Channels.Single(c => c.ChatId == NewChannel).IsActive = true;
        _f.Db.SaveChanges();
        var access = new ChatAccessService(_f.Bot, _f.Db, NullLogger<ChatAccessService>.Instance);
        await access.UnlinkAsync(NewChannel, Adder, DateTimeOffset.UtcNow, CancellationToken.None);

        await MembershipAsync(NewChannelChat(), AdminMember(), oldMember: AdminMember(canPost: false));
        Assert.Empty(_f.Db.ChannelAdmins);

        await MembershipAsync(NewChannelChat(), AdminMember());

        Assert.False(Row(NewChannel).IsActive);
        Assert.Single(_f.Db.ChannelAdmins);
    }

    [Fact]
    public async Task BotRemoved_FromAnUnknownChannel_DoesNothing()
    {
        await MembershipAsync(NewChannelChat(), LeftMember());

        Assert.Empty(_f.Db.Channels);
        Assert.Empty(_f.Db.ChannelAdmins);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task BotRemoved_KeepsTheAdminLinks()
    {
        await MembershipAsync(NewChannelChat(), AdminMember());

        await MembershipAsync(NewChannelChat(), LeftMember());

        Assert.Single(_f.Db.ChannelAdmins.AsNoTracking());
    }

    [Fact]
    public async Task RemovedThenReAdded_ReconnectsWithoutActivating()
    {
        await MembershipAsync(NewChannelChat(), AdminMember());
        await MembershipAsync(NewChannelChat(), LeftMember());

        await MembershipAsync(NewChannelChat(), AdminMember());

        Assert.False(Row(NewChannel).IsActive);
        Assert.Single(_f.Db.Channels);
        Assert.Single(_f.Db.ChannelAdmins);
    }

    [Theory]
    [InlineData(ChatMemberStatus.Member)]
    [InlineData(ChatMemberStatus.Creator)]
    [InlineData(ChatMemberStatus.Restricted)]
    public async Task OtherStatuses_AreIgnored(ChatMemberStatus status)
    {
        ChatMember member = status switch
        {
            ChatMemberStatus.Member => new ChatMemberMember { User = BotUser },
            ChatMemberStatus.Creator => new ChatMemberOwner { User = BotUser },
            _ => new ChatMemberRestricted { User = BotUser },
        };

        await MembershipAsync(NewChannelChat(), member);

        Assert.Empty(_f.Db.Channels);
        Assert.Empty(_f.Db.ChannelAdmins);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task OtherStatuses_DoNotTouchAnExistingRow()
    {
        _f.AddChannel(NewChannel, c => c.OutageTrigger = true);

        await MembershipAsync(NewChannelChat(), new ChatMemberMember { User = BotUser });

        var channel = Row(NewChannel);
        Assert.True(channel.IsActive);
        Assert.True(channel.OutageTrigger);
    }

    [Theory]
    [InlineData(ChatType.Group)]
    [InlineData(ChatType.Supergroup)]
    [InlineData(ChatType.Private)]
    public async Task NonChannelChats_AreIgnored_ForAdminAndRemoval(ChatType type)
    {
        var chat = new Chat { Id = -2000, Type = type, Title = "Чат" };
        _f.AddChannel(-2000, c => c.OutageTrigger = true);

        await MembershipAsync(chat, AdminMember());
        await MembershipAsync(chat, LeftMember());

        var channel = Row(-2000);
        Assert.True(channel.IsActive);
        Assert.True(channel.OutageTrigger);
        Assert.Empty(_f.Db.ChannelAdmins);
        Assert.Empty(_f.Bot.Requests);
    }

    [Fact]
    public async Task MembershipUpdate_PostsNothingIntoTheChannelItself()
    {
        await MembershipAsync(NewChannelChat(), AdminMember());

        Assert.DoesNotContain(_f.Bot.Sent, s => s.ChatId.Identifier == NewChannel);
    }

    [Fact]
    public async Task MembershipUpdate_TakesPrecedenceOverEverythingElseInTheUpdate()
    {
        var update = MembershipUpdate(NewChannelChat(), AdminMember());
        update.Message = new Message { Id = 1, Chat = Private, Text = "/start" };

        await HandleAsync(update);

        Assert.Equal(Adder, Assert.Single(_f.Bot.Sent).ChatId.Identifier);
    }
}
