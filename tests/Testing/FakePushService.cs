using System.Collections.Concurrent;
using System.Net;

namespace TeamBeats.Testing;

/// <summary>
/// A push service for tests (spec 030 US2): records every message the platform posts, with its headers and encrypted body,
/// and answers with <see cref="Status"/> per endpoint, 201 by default.
/// </summary>
public sealed class FakePushService : HttpMessageHandler
{
    public sealed record Received(string Endpoint, IReadOnlyDictionary<string, string> Headers, byte[] Body);

    public ConcurrentQueue<Received> Messages { get; } = new();

    public ConcurrentDictionary<string, HttpStatusCode> Status { get; } = new(StringComparer.Ordinal);

    public IReadOnlyList<Received> To(string endpoint) => Messages.Where(m => m.Endpoint == endpoint).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var endpoint = request.RequestUri!.ToString();
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        Messages.Enqueue(new Received(endpoint, headers, body));
        return new HttpResponseMessage(Status.GetValueOrDefault(endpoint, HttpStatusCode.Created));
    }
}
