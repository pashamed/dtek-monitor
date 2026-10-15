using Microsoft.JSInterop;

namespace DtekBot.WebApp.Services;

public sealed class TelegramService(IJSRuntime js) : IDisposable
{
    private DotNetObjectReference<TelegramService>? _self;
    private Action? _onBack;

    public async ValueTask<string> GetInitDataAsync() =>
        await js.InvokeAsync<string>("dtekTg.getInitData") ?? "";

    public ValueTask ReadyAsync() => js.InvokeVoidAsync("dtekTg.ready");

    public ValueTask ExpandAsync() => js.InvokeVoidAsync("dtekTg.expand");

    public ValueTask CloseAsync() => js.InvokeVoidAsync("dtekTg.close");

    // kind: "success" | "error" | "warning" | "light" | "medium" | "heavy"
    public ValueTask HapticAsync(string kind) => js.InvokeVoidAsync("dtekTg.haptic", kind);

    public async ValueTask ShowBackButtonAsync(Action onBack)
    {
        _onBack = onBack;
        _self ??= DotNetObjectReference.Create(this);
        await js.InvokeVoidAsync("dtekTg.showBackButton", _self);
    }

    public ValueTask HideBackButtonAsync()
    {
        _onBack = null;
        return js.InvokeVoidAsync("dtekTg.hideBackButton");
    }

    public ValueTask<string?> InsertAtCaretAsync(string elementId, string text, int maxLength) =>
        js.InvokeAsync<string?>("dtekTg.insertAtCaret", elementId, text, maxLength);

    [JSInvokable]
    public void OnBackPressed() => _onBack?.Invoke();

    public void Dispose() => _self?.Dispose();
}
