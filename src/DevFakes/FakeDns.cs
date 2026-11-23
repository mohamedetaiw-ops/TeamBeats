using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace TeamBeats.DevFakes;

/// <summary>
/// Stands in for the DNS over HTTPS resolver in tests and on the development host (spec 033 R2 T021): answers TXT queries from records a test
/// publishes, in the JSON format the real resolvers use, or fails like a resolver that is down.
/// </summary>
public sealed class FakeDns : HttpMessageHandler
{
    private readonly ConcurrentDictionary<string, string[]> records = new(StringComparer.OrdinalIgnoreCase);

    public bool Down { get; set; }

    public void SetTxt(string name, params string[] values) => records[name] = values;

    public void Remove(string name) => records.TryRemove(name, out _);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (Down)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
        var name = Uri.UnescapeDataString(request.RequestUri!.Query.TrimStart('?').Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "name")?.ElementAtOrDefault(1) ?? "");
        var found = records.TryGetValue(name, out var values);
        var body = JsonSerializer.Serialize(new
        {
            Status = found ? 0 : 3,
            Answer = (values ?? []).Select(v => new { name, type = 16, TTL = 300, data = "\"" + v + "\"" }).ToArray(),
        });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/dns-json") });
    }
}
