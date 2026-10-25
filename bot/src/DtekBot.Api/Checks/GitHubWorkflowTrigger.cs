using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace DtekBot.Api.Checks;

public interface IWorkflowTrigger
{
    // True when GitHub accepted the dispatch. Never throws; a failed dispatch is picked up by the next scheduled run.
    Task<bool> TriggerAsync(string eventType, CancellationToken ct, object? payload = null);
}

// Starts the monitor workflow through GitHub's repository_dispatch. The token lives in appsettings.Production.json.
public sealed class GitHubWorkflowTrigger(HttpClient http, IConfiguration config, ILogger<GitHubWorkflowTrigger> logger)
    : IWorkflowTrigger
{
    public async Task<bool> TriggerAsync(string eventType, CancellationToken ct, object? payload = null)
    {
        var repository = config["GitHub:Repository"];
        var token = config["GitHub:DispatchToken"];
        if (string.IsNullOrWhiteSpace(repository) || string.IsNullOrWhiteSpace(token))
        {
            logger.LogInformation("Workflow dispatch skipped: GitHub:Repository or GitHub:DispatchToken is not set");
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"repos/{repository}/dispatches")
            {
                Content = new StringContent(payload is null
                    ? JsonSerializer.Serialize(new { event_type = eventType })
                    : JsonSerializer.Serialize(new { event_type = eventType, client_payload = payload }), Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // GitHub's error message and the required permissions explain a 401/403; neither contains the token.
            var reason = (await ReadErrorMessageAsync(response, ct)).Replace(token, "***", StringComparison.Ordinal);
            var required = response.Headers.TryGetValues("X-Accepted-GitHub-Permissions", out var permissions)
                ? string.Join(",", permissions)
                : "n/a";
            logger.LogWarning(
                "Workflow dispatch {Event} was rejected with HTTP {Status}: {Reason} (required permissions: {Required})",
                eventType, (int)response.StatusCode, reason, required);
            return false;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning("Workflow dispatch failed: {Message}", ex.Message);
            return false;
        }
    }

    // Only GitHub's "message" field, truncated; the rest of the body is never logged.
    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var message = json.RootElement.TryGetProperty("message", out var value) ? value.GetString() : null;
            return string.IsNullOrWhiteSpace(message) ? "no message" : message.Length > 200 ? message[..200] : message;
        }
        catch (JsonException)
        {
            return "no message";
        }
    }
}
