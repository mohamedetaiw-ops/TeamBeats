using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TeamBeats.Testing;

/// <summary>
/// The messaging service for tests (spec 030 R3 MOB-022): grants an access token for any signed assertion and records
/// each message sent, with its device token and body. <see cref="Status"/> sets the answer per device token, 200 by
/// default; 404 means the app was removed from the device.
/// </summary>
public sealed class FakeFcm : HttpMessageHandler
{
    public sealed record Received(string Token, string Authorization, JsonElement Message);

    public ConcurrentQueue<Received> Messages { get; } = new();

    public ConcurrentDictionary<string, HttpStatusCode> Status { get; } = new(StringComparer.Ordinal);

    public int TokenRequests;

    public IReadOnlyList<Received> To(string token) => Messages.Where(m => m.Token == token).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref TokenRequests);
            var ok = text.Contains("grant_type=urn", StringComparison.Ordinal) && text.Contains("assertion=", StringComparison.Ordinal);
            return new HttpResponseMessage(ok ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
            {
                Content = new StringContent(ok ? """{"access_token":"test-access","expires_in":3600}""" : "{}", Encoding.UTF8, "application/json"),
            };
        }
        var message = JsonDocument.Parse(text).RootElement.GetProperty("message").Clone();
        var token = message.GetProperty("token").GetString()!;
        Messages.Enqueue(new Received(token, request.Headers.Authorization?.ToString() ?? "", message));
        var status = Status.GetValueOrDefault(token, HttpStatusCode.OK);
        return new HttpResponseMessage(status) { Content = new StringContent(status == HttpStatusCode.OK ? """{"name":"projects/teambeats-dev/messages/1"}""" : "{}") };
    }
}
