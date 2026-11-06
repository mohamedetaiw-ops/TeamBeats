using TeamBeats.Modules.Identity.DevAuth;
using TeamBeats.Modules.Tenancy;
using TeamBeats.Persistence;
using TeamBeats.TenantContext;

namespace TeamBeats.Api.DevHost;

/// <summary>
/// Creates the fictional test tenant on startup when <c>Dev:SeedTenant</c> is configured (spec 003 US7). The tenant is
/// created through the tenancy module's own use case, as a seeded platform operator, so it is audited like any other
/// tenant. The configured admins are appointed as tenant administrators and claim the appointment on first sign-in.
/// Runs only in the development host, which is never part of a production deployment (ADR-0004).
/// </summary>
public sealed class TestTenantSeeder(DevSeeder operators, PlatformTenants tenants, TenantContextAccessor context, IDbSession session)
{
    public const string Section = "Dev:SeedTenant";
    public const string OperatorEmail = "seed.operator@platform.example";

    /// <returns>True when the tenant was created, false when it already existed.</returns>
    public async Task<bool> SeedAsync(CreateTenantRequest request, CancellationToken ct)
    {
        var key = TenantRules.NormalizeKey(request.Key);
        var existing = await tenants.ListAsync(null, key, ct);
        if (existing.Any(t => t.Key == key))
        {
            return false;
        }

        var operatorId = await operators.EnsurePlatformOperatorAsync(OperatorEmail, "Seed operator", ct);
        context.SetHost(HostScope.Platform);
        context.SetActor(ActorType.PlatformOperator, operatorId);
        await tenants.CreateAsync(request, ct);
        await session.CommitAsync(ct);
        return true;
    }
}
