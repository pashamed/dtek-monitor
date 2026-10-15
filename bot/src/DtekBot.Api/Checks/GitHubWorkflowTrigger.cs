using System.Net.Http.Headers;
using System.Text;

namespace DtekBot.Api.Checks;

public interface IWorkflowTrigger
{
    // True when GitHub accepted the dispatch. Never throws; a failed dispatch is picked up by the next scheduled run.
    Task<bool> TriggerAsync(string eventType, CancellationToken ct);
}

// Starts the monitor workflow through GitHub's repository_dispatch. The token lives in appsettings.Production.json.
public sealed class GitHubWorkflowTrigger(HttpClient http, IConfiguration config, ILogger<GitHubWorkflowTrigger> logger)
    : IWorkflowTrigger
{
    public async Task<bool> TriggerAsync(string eventType, CancellationToken ct)
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
                Content = new StringContent($$"""{"event_type":"{{eventType}}"}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode)
            {
                return true;
            }

            // Status only: the body and headers are never logged.
            logger.LogWarning("Workflow dispatch was rejected with HTTP {Status}", (int)response.StatusCode);
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
}
