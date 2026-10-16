using System.Net;
using System.Text;

namespace DtekBot.Tests;

// Stands in for api.svitlobot.in.ua: records every request and answers "ok" unless told otherwise.
public sealed class FakeSvitlobotHandler : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string PathAndQuery)> _requests = [];

    public IReadOnlyList<(HttpMethod Method, string PathAndQuery)> Requests
    {
        get
        {
            lock (_requests)
            {
                return _requests.ToList();
            }
        }
    }

    public Func<Uri, HttpResponseMessage> Responder { get; set; } = _ => Text("ok");

    public static HttpResponseMessage Text(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    public void Reset()
    {
        lock (_requests)
        {
            _requests.Clear();
        }

        Responder = _ => Text("ok");
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add((request.Method, request.RequestUri!.PathAndQuery));
        }

        return Task.FromResult(Responder(request.RequestUri!));
    }
}
