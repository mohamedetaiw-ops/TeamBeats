using System.Collections.Concurrent;
using System.Net;
using System.Text;
using TeamBeats.Modules.Integrations.Runtime;

namespace TeamBeats.Testing;

/// <summary>
/// Stands in for external REST systems behind the connector HTTP client in tests. Each test registers its own hosts
/// with a responder and reads back what was sent. Hosts under <c>blocked.test</c> fail the way the real connector
/// handler fails for an address it refuses (the handler itself is tested in <c>SafeHttpTests</c>). Unknown hosts answer 404.
/// </summary>
public sealed class FakeRestApi : HttpMessageHandler
{
    public sealed record Sent(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body);

    private readonly ConcurrentDictionary<string, Func<Sent, HttpResponseMessage>> responders = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<Sent> sent = new();

    public void On(string host, Func<Sent, HttpResponseMessage> responder) => responders[host] = responder;

    public IReadOnlyList<Sent> SentTo(string host) => sent.Where(s => string.Equals(s.Uri.Host, host, StringComparison.OrdinalIgnoreCase)).ToList();

    public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host.EndsWith(".blocked.test", StringComparison.OrdinalIgnoreCase))
        {
            throw new HttpRequestException("refused", new BlockedAddressException(uri.Host));
        }
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        var s = new Sent(request.Method.Method, uri, headers, body);
        sent.Enqueue(s);
        return responders.TryGetValue(uri.Host, out var respond) ? respond(s) : new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}
