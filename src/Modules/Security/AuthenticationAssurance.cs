using TeamBeats.Modules.Security.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Security;

/// <summary>Checks MFA claims against the tenant mapping for the provider and records that a sign-in showed them.</summary>
public sealed class AuthenticationAssurance(IDbSession session) : IAuthenticationAssurance
{
    public static readonly IReadOnlyList<string> DefaultAmr = ["mfa"];

    public async Task<bool> CheckMfaAsync(string provider, IReadOnlyList<string> amr, string? acr, CancellationToken ct)
    {
        var mapping = await session.QuerySingleOrDefaultAsync("select amr, acr from security.mfa_claim_mapping where provider = @p", new { p = provider },
            r => (Amr: r.GetFieldValue<string[]>(0), Acr: r.GetFieldValue<string[]>(1)), ct);
        var mfaAmr = mapping == default ? DefaultAmr : mapping.Amr;
        var mfaAcr = mapping == default ? [] : mapping.Acr;
        var shown = amr.Any(a => mfaAmr.Contains(a, StringComparer.OrdinalIgnoreCase))
            || (acr is not null && mfaAcr.Contains(acr, StringComparer.Ordinal));
        if (shown)
        {
            await session.ExecAsync(
                """
                insert into security.mfa_claim_mapping (tenant_id, provider, seen_at) values (platform.current_tenant_id(), @p, now())
                on conflict (tenant_id, provider) do update set seen_at = now()
                """, new { p = provider }, ct);
        }
        return shown;
    }
}
