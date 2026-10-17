using Telegram.Bot;
using Telegram.Bot.Args;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Requests;
using Telegram.Bot.Requests.Abstractions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace DtekBot.Tests;

// Records every request the code under test makes; tests can inject Telegram API failures.
public sealed class FakeTelegramBotClient : ITelegramBotClient
{
    private int _nextMessageId = 1000;

    public List<object> Requests { get; } = [];

    public List<CancellationToken> RequestTokens { get; } = [];

    // Return an exception to make that request fail (the request is still recorded).
    public Func<object, Exception?>? Failure { get; set; }

    public IEnumerable<SendMessageRequest> Sent => Requests.OfType<SendMessageRequest>();

    public IEnumerable<EditMessageTextRequest> Edits => Requests.OfType<EditMessageTextRequest>();

    public IEnumerable<SendMessageRequest> SentTo(long chatId) => Sent.Where(r => r.ChatId.Identifier == chatId);

    public static ApiRequestException Forbidden() =>
        new("Forbidden: bot was blocked by the user", 403);

    public static ApiRequestException CantParseEntities() =>
        new("Bad Request: can't parse entities: Unsupported start tag at byte offset 5", 400);

    public static ApiRequestException NotModified() =>
        new("Bad Request: message is not modified: specified new message content and reply markup are exactly the same", 400);

    public static ApiRequestException MessageToEditNotFound() =>
        new("Bad Request: message to edit not found", 400);

    public Task<TResponse> SendRequest<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        RequestTokens.Add(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var failure = Failure?.Invoke(request);
        if (failure is not null)
        {
            throw failure;
        }

        object response = request switch
        {
            SendMessageRequest => new Message { Id = _nextMessageId++, Chat = new Chat { Id = 0 } },
            EditMessageTextRequest edit => new Message { Id = edit.MessageId, Chat = new Chat { Id = 0 } },
            AnswerCallbackQueryRequest => true,
            SetChatMenuButtonRequest => true,
            DeleteMessageRequest => true,
            SetMyCommandsRequest => true,
            SendPhotoRequest => NewPhotoMessage(),
            EditMessageMediaRequest edit => new Message { Id = edit.MessageId, Chat = new Chat { Id = 0 } },
            EditMessageCaptionRequest edit => new Message { Id = edit.MessageId, Chat = new Chat { Id = 0 } },
            GetMeRequest => new User { Id = BotId, IsBot = true, FirstName = "DtekBot", Username = "dtek_test_bot" },
            GetChatMemberRequest member => MemberFor(member.UserId),
            GetChatAdministratorsRequest => MemberStatuses
                .Where(p => p.Value is ChatMemberStatus.Creator or ChatMemberStatus.Administrator)
                .Select(p => MemberFor(p.Key))
                .ToArray(),
            GetChatRequest chat => Chats.TryGetValue(chat.ChatId.Identifier ?? 0, out var info)
                ? info
                : throw new ApiRequestException("Bad Request: chat not found", 400),
            _ => throw new NotSupportedException($"Fake bot does not handle {request.GetType().Name}."),
        };

        return Task.FromResult((TResponse)response);
    }

    // Everyone is a plain member unless configured otherwise.
    public Dictionary<long, ChatMemberStatus> MemberStatuses { get; } = [];

    public void SetStatus(long userId, ChatMemberStatus status) => MemberStatuses[userId] = status;

    // Chats that GetChat can find, by id.
    public Dictionary<long, ChatFullInfo> Chats { get; } = [];

    public IEnumerable<AnswerCallbackQueryRequest> Answers => Requests.OfType<AnswerCallbackQueryRequest>();

    public IEnumerable<SendPhotoRequest> Photos => Requests.OfType<SendPhotoRequest>();

    public IEnumerable<EditMessageMediaRequest> MediaEdits => Requests.OfType<EditMessageMediaRequest>();

    public IEnumerable<EditMessageCaptionRequest> CaptionEdits => Requests.OfType<EditMessageCaptionRequest>();

    public IEnumerable<DeleteMessageRequest> Deletes => Requests.OfType<DeleteMessageRequest>();

    private Message NewPhotoMessage() => new()
    {
        Id = _nextMessageId++,
        Chat = new Chat { Id = 0 },
        Photo = [new PhotoSize { FileId = "f", FileUniqueId = "u", Width = 10, Height = 10 }],
    };

    private ChatMember MemberFor(long userId)
    {
        var user = new User { Id = userId, FirstName = "U" + userId };
        var status = MemberStatuses.GetValueOrDefault(userId, ChatMemberStatus.Member);
        return status switch
        {
            ChatMemberStatus.Creator => new ChatMemberOwner { User = user },
            ChatMemberStatus.Administrator => new ChatMemberAdministrator { User = user },
            ChatMemberStatus.Left => new ChatMemberLeft { User = user },
            ChatMemberStatus.Kicked => new ChatMemberBanned { User = user },
            _ => new ChatMemberMember { User = user },
        };
    }

    public bool LocalBotServer => false;

    public long BotId => 1;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(100);

    public IExceptionParser ExceptionsParser { get; set; } = new DefaultExceptionParser();

    public event AsyncEventHandler<ApiRequestEventArgs>? OnMakingApiRequest
    {
        add { }
        remove { }
    }

    public event AsyncEventHandler<ApiResponseEventArgs>? OnApiResponseReceived
    {
        add { }
        remove { }
    }

    public Task<bool> TestApi(CancellationToken cancellationToken = default) => Task.FromResult(true);

    public Task DownloadFile(string filePath, Stream destination, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task DownloadFile(TGFile file, Stream destination, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
