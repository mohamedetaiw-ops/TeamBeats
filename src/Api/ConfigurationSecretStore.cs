using TeamBeats.Primitives;

namespace TeamBeats.Api;

/// <summary>
/// Secret manager adapter that reads <c>Secrets:tenants:{tenantId}:{name}</c> from host configuration, which the
/// deployment fills from its secret store (for example Fly.io secrets as <c>Secrets__tenants__{id}__{name}</c>).
/// Stand-in until a secret manager product is chosen (recorded as an open decision); modules depend only on
/// <see cref="ISecretStore"/>, so swapping the adapter changes no module code.
/// </summary>
public sealed class ConfigurationSecretStore(IConfiguration configuration) : ISecretStore
{
    public Task<string?> GetTenantSecretAsync(Guid tenantId, string reference, CancellationToken ct = default)
    {
        var name = SecretReferences.NameFor(tenantId, reference);
        var value = name is null ? null : configuration[$"Secrets:tenants:{tenantId:D}:{name}"];
        return Task.FromResult(string.IsNullOrEmpty(value) ? null : value);
    }
}
