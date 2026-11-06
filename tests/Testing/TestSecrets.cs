using System.Collections.Concurrent;
using TeamBeats.Primitives;

namespace TeamBeats.Testing;

/// <summary>In-memory secret store for tests. Values are fictional.</summary>
public sealed class TestSecrets : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new(StringComparer.Ordinal);

    public void Set(string reference, string value) => _values[reference] = value;

    public Task<string?> GetTenantSecretAsync(Guid tenantId, string reference, CancellationToken ct = default) =>
        Task.FromResult(SecretReferences.NameFor(tenantId, reference) is null ? null : _values.GetValueOrDefault(reference));
}
