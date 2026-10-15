using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DtekBot.Shared;

namespace DtekBot.WebApp.Services;

public sealed record ApiResult<T>(T? Value, string? Error, IReadOnlyList<string> Details, HttpStatusCode? Status)
{
    public bool Ok => Error is null;

    public bool Unauthorized => Status == HttpStatusCode.Unauthorized;
}

public sealed class ApiClient(HttpClient http, TelegramService telegram)
{
    public Task<ApiResult<BootstrapDto>> GetBootstrapAsync() => SendAsync<BootstrapDto>(HttpMethod.Get, "api/bootstrap");

    public Task<ApiResult<List<ChatSummaryDto>>> GetChatsAsync() => SendAsync<List<ChatSummaryDto>>(HttpMethod.Get, "api/chats");

    public Task<ApiResult<ChatSummaryDto>> LinkChatAsync(string chat) =>
        SendAsync<ChatSummaryDto>(HttpMethod.Post, "api/chats/link", new LinkChatRequest(chat));

    public Task<ApiResult<ChatSummaryDto>> SaveChatAsync(long chatId, ChatSettingsDto settings) =>
        SendAsync<ChatSummaryDto>(HttpMethod.Put, $"api/chats/{chatId}", settings);

    private async Task<ApiResult<T>> SendAsync<T>(HttpMethod method, string url, object? body = null)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);
            request.Headers.TryAddWithoutValidation("Authorization", "tma " + await telegram.GetInitDataAsync());
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, body.GetType(), options: ApiJson.Options);
            }

            using var response = await http.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(ApiJson.Options);
                return value is null
                    ? Fail<T>("Порожня відповідь сервера.", [], response.StatusCode)
                    : new ApiResult<T>(value, null, [], response.StatusCode);
            }

            ApiError? error = null;
            try
            {
                error = await response.Content.ReadFromJsonAsync<ApiError>(ApiJson.Options);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                // Non-JSON error body: fall back to a status-based message below.
            }

            var message = !string.IsNullOrWhiteSpace(error?.Error)
                ? error.Error
                : response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => "Відкрийте застосунок із Telegram.",
                    HttpStatusCode.Forbidden => "Немає доступу.",
                    _ => $"Помилка сервера ({(int)response.StatusCode}).",
                };
            return Fail<T>(message, error?.Details ?? [], response.StatusCode);
        }
        catch (HttpRequestException)
        {
            return Fail<T>("Немає зв'язку із сервером. Спробуйте ще раз.", [], null);
        }
    }

    private static ApiResult<T> Fail<T>(string error, IReadOnlyList<string> details, HttpStatusCode? status) =>
        new(default, error, details, status);
}
