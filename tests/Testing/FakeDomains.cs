using System.Collections.Concurrent;
using TeamBeats.Modules.Tenancy.Domains;

namespace TeamBeats.Testing;

/// <summary>Certificates the edge would present, per host; a host with none set has no readable certificate.</summary>
public sealed class FakeCertificates : ICertificateProbe
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> expiries = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string host, DateTimeOffset expires) => expiries[host] = expires;

    public Task<DateTimeOffset?> ExpiresAsync(string host, CancellationToken ct) =>
        Task.FromResult(expiries.TryGetValue(host, out var at) ? at : (DateTimeOffset?)null);
}
