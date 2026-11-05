using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Entitlements;

public static class EntitlementsModule
{
    public static readonly MigrationSource Migrations = new("entitlements", typeof(EntitlementsModule).Assembly);

    public static IServiceCollection AddEntitlementsModule(this IServiceCollection services)
    {
        services.AddScoped<EntitlementService>();
        services.AddScoped<IEntitlementService>(sp => sp.GetRequiredService<EntitlementService>());
        services.AddScoped<ISubscriptionProvisioning>(sp => sp.GetRequiredService<EntitlementService>());
        services.AddScoped<PackageCatalogue>();
        return services;
    }

    public static IEndpointRouteBuilder MapEntitlementsEndpoints(this IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/v1/platform").RequirePlatformOperator().WithTags("Platform: packages");

        platform.MapGet("/packages", async (PackageCatalogue catalogue, CancellationToken ct) =>
            Results.Ok(await catalogue.ListAsync(ct)));

        platform.MapPost("/packages/{key}/versions", async (string key, PackageVersionRequest request, PackageCatalogue catalogue, IDbSession session, CancellationToken ct) =>
        {
            var version = await catalogue.CreateDraftAsync(key, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/platform/packages/{key}/versions/{version.Version}", version);
        });

        platform.MapPost("/packages/{key}/versions/{version:int}:publish", async (string key, int version, PackageCatalogue catalogue, IDbSession session, CancellationToken ct) =>
        {
            await catalogue.PublishAsync(key, version, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        platform.MapPut("/tenants/{tenantId:guid}/subscription", async (Guid tenantId, SubscriptionRequest request,
            PackageCatalogue catalogue, ITenantRegistry registry, TenantContextAccessor context, IDbSession session, CancellationToken ct) =>
        {
            var tenant = await registry.FindByIdAsync(tenantId, ct) ?? throw AppException.NotFound();
            context.SetTenant(tenant.Id);
            await catalogue.ChangeSubscriptionAsync(request.PackageKey, request.Version, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        app.MapGet("/api/v1/tenant/entitlements", async (IAuthorizationService authz, EntitlementService service, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.EntitlementsView, AuthzTarget.Tenant, ct);
            return Results.Ok(await service.DescribeAsync(ct));
        }).RequireTenantUser().WithTags("Tenant: plan");

        return app;
    }
}

public sealed record PackageVersionRequest(IReadOnlyDictionary<string, CapabilityValue> Capabilities);

public sealed record CapabilityValue(bool Enabled, long? Limit);

public sealed record SubscriptionRequest(string PackageKey, int? Version);

public sealed record PackageVersionView(string PackageKey, int Version, string Status, IReadOnlyDictionary<string, CapabilityValue> Capabilities);

public sealed record TenantEntitlementsView(string PackageKey, int Version, DateTimeOffset StartsAt, IReadOnlyDictionary<string, CapabilityValue> Capabilities);

/// <summary>
/// Resolves effective entitlements for the current tenant: active subscription, then its package version.
/// Add-ons and overrides (R2) layer on top of the same resolution. Unknown keys fail closed (ENT-005).
/// </summary>
public sealed class EntitlementService(IDbSession session, ITenantContext context, ILogger<EntitlementService> logger)
    : IEntitlementService, ISubscriptionProvisioning
{
    private Dictionary<string, CapabilityValue>? _resolved;
    private HashSet<string>? _catalogue;

    private async Task<Dictionary<string, CapabilityValue>> ResolveAsync(CancellationToken ct)
    {
        context.RequireTenant();
        if (_resolved is not null)
        {
            return _resolved;
        }

        _catalogue = (await session.QueryAsync("select key from entitlements.capability", null, r => r.GetString(0), ct)).ToHashSet();
        _resolved = (await session.QueryAsync(
            """
            select pc.capability_key, pc.enabled, pc.limit_value
            from entitlements.subscription s
            join entitlements.package_capability pc on pc.package_version_id = s.package_version_id
            where s.status = 'active' and s.starts_at <= now()
            """, null,
            r => (Key: r.GetString(0), Value: new CapabilityValue(r.GetBoolean(1), r.Get<long?>("limit_value"))), ct))
            .ToDictionary(x => x.Key, x => x.Value);
        return _resolved;
    }

    public async Task<bool> IsEnabledAsync(string capabilityKey, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(ct);
        if (!_catalogue!.Contains(capabilityKey))
        {
            logger.LogError("Unknown capability key {CapabilityKey} checked; failing closed", capabilityKey);
            return false;
        }
        return resolved.TryGetValue(capabilityKey, out var value) && value.Enabled;
    }

    public async Task<long?> GetLimitAsync(string capabilityKey, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(ct);
        return resolved.TryGetValue(capabilityKey, out var value) && value.Enabled ? value.Limit : null;
    }

    public async Task DemandAsync(string capabilityKey, CancellationToken ct = default)
    {
        if (!await IsEnabledAsync(capabilityKey, ct))
        {
            throw AppException.NotInPackage(capabilityKey);
        }
    }

    public async Task<TenantEntitlementsView> DescribeAsync(CancellationToken ct)
    {
        var resolved = await ResolveAsync(ct);
        var sub = await session.QuerySingleOrDefaultAsync(
            """
            select pv.package_key, pv.version, s.starts_at
            from entitlements.subscription s join entitlements.package_version pv on pv.id = s.package_version_id
            where s.status = 'active'
            """, null, r => new { Key = r.GetString(0), Version = r.GetInt32(1), Starts = r.GetFieldValue<DateTimeOffset>(2) }, ct)
            ?? throw AppException.NotFound();
        return new TenantEntitlementsView(sub.Key, sub.Version, sub.Starts, resolved);
    }

    public async Task<bool> PublishedPackageExistsAsync(string packageKey, CancellationToken ct = default) =>
        await session.ScalarAsync<bool>(
            "select exists (select 1 from entitlements.package_version where package_key = @k and status = 'published')",
            new { k = packageKey }, ct);

    public async Task AssignInitialPackageAsync(string packageKey, CancellationToken ct = default)
    {
        var versionId = await session.ScalarAsync<Guid?>(
            "select id from entitlements.package_version where package_key = @k and status = 'published' order by version desc limit 1",
            new { k = packageKey }, ct) ?? throw AppException.Validation("packageKey", "validation.package.unknown");
        await session.ExecAsync(
            "insert into entitlements.subscription (tenant_id, id, package_version_id, starts_at, status) values (@t, @id, @v, now(), 'active')",
            new { t = context.RequireTenant(), id = Ids.New(), v = versionId }, ct);
        _resolved = null;
    }
}

/// <summary>Platform-side package definitions and subscription changes. Every change is audited (ENT-008).</summary>
public sealed class PackageCatalogue(IDbSession session, ITenantContext context, IAuditWriter audit, IOutbox outbox)
{
    public async Task<IReadOnlyList<PackageVersionView>> ListAsync(CancellationToken ct)
    {
        var rows = await session.QueryAsync(
            """
            select pv.package_key, pv.version, pv.status, pc.capability_key, pc.enabled, pc.limit_value
            from entitlements.package_version pv
            left join entitlements.package_capability pc on pc.package_version_id = pv.id
            order by pv.package_key, pv.version
            """, null,
            r => (Key: r.GetString(0), Version: r.GetInt32(1), Status: r.GetString(2), Cap: r.Get<string>("capability_key"),
                Enabled: r.Get<bool?>("enabled"), Limit: r.Get<long?>("limit_value")), ct);
        return rows.GroupBy(r => (r.Key, r.Version, r.Status))
            .Select(g => new PackageVersionView(g.Key.Key, g.Key.Version, g.Key.Status,
                g.Where(x => x.Cap is not null).ToDictionary(x => x.Cap!, x => new CapabilityValue(x.Enabled ?? false, x.Limit))))
            .ToList();
    }

    public async Task<PackageVersionView> CreateDraftAsync(string packageKey, PackageVersionRequest request, CancellationToken ct)
    {
        var known = (await session.QueryAsync("select key from entitlements.capability", null, r => r.GetString(0), ct)).ToHashSet();
        var errors = new ValidationErrors();
        foreach (var key in request.Capabilities.Keys.Where(k => !known.Contains(k)))
        {
            errors.Add($"capabilities.{key}", "validation.package.unknown_capability");
        }
        if (!request.Capabilities.TryGetValue(Capabilities.AiAssistant, out var ai) || !ai.Enabled)
        {
            errors.Add($"capabilities.{Capabilities.AiAssistant}", "validation.package.ai_required");
        }
        errors.ThrowIfAny();

        var exists = await session.ScalarAsync<bool>("select exists (select 1 from entitlements.package where key = @k)", new { k = packageKey }, ct);
        if (!exists)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(packageKey, "^[a-z][a-z0-9_]{2,39}$"))
            {
                throw AppException.Validation("key", "validation.package.key_format");
            }
            await session.ExecAsync("insert into entitlements.package (key, name_key) values (@k, @n)", new { k = packageKey, n = "package." + packageKey }, ct);
        }

        var version = await session.ScalarAsync<int>(
            "select coalesce(max(version), 0) + 1 from entitlements.package_version where package_key = @k", new { k = packageKey }, ct);
        var id = Ids.New();
        await session.ExecAsync("insert into entitlements.package_version (id, package_key, version, status) values (@id, @k, @v, 'draft')",
            new { id, k = packageKey, v = version }, ct);
        foreach (var (key, value) in request.Capabilities)
        {
            await session.ExecAsync(
                "insert into entitlements.package_capability (package_version_id, capability_key, enabled, limit_value) values (@id, @c, @e, @l)",
                new { id, c = key, e = value.Enabled, l = value.Limit }, ct);
        }
        return new PackageVersionView(packageKey, version, "draft", request.Capabilities);
    }

    public async Task PublishAsync(string packageKey, int version, CancellationToken ct)
    {
        try
        {
            var updated = await session.ExecAsync(
                "update entitlements.package_version set status = 'published', published_at = now() where package_key = @k and version = @v and status = 'draft'",
                new { k = packageKey, v = version }, ct);
            if (updated == 0)
            {
                throw AppException.NotFound();
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.CheckViolation)
        {
            throw AppException.Validation($"capabilities.{Capabilities.AiAssistant}", "validation.package.ai_required");
        }

        await audit.WritePlatformAsync(new AuditEntry(AuditActions.PackagePublished, "package_version", $"{packageKey}/{version}"), null, ct);
        await outbox.EnqueueAsync(new PackagePublished(packageKey, version), ct);
    }

    /// <summary>Moves the current-context tenant to a package version. Tenants never change their own subscription.</summary>
    public async Task ChangeSubscriptionAsync(string packageKey, int? version, CancellationToken ct)
    {
        var target = await session.QuerySingleOrDefaultAsync(
            """
            select id, version from entitlements.package_version
            where package_key = @k and status = 'published' and (@v::int is null or version = @v)
            order by version desc limit 1
            """, new { k = packageKey, v = version }, r => (Id: r.GetGuid(0), Version: r.GetInt32(1)), ct);
        if (target == default)
        {
            throw AppException.Validation("packageKey", "validation.package.unknown");
        }

        var previous = await session.QuerySingleOrDefaultAsync(
            """
            select pv.package_key, pv.version from entitlements.subscription s
            join entitlements.package_version pv on pv.id = s.package_version_id where s.status = 'active'
            """, null, r => $"{r.GetString(0)}/{r.GetInt32(1)}", ct);

        await session.ExecAsync("update entitlements.subscription set status = 'ended', ends_at = now() where status = 'active'", null, ct);
        await session.ExecAsync(
            "insert into entitlements.subscription (tenant_id, id, package_version_id, starts_at, status) values (@t, @id, @v, now(), 'active')",
            new { t = context.RequireTenant(), id = Ids.New(), v = target.Id }, ct);

        var changes = new { from = previous, to = $"{packageKey}/{target.Version}" };
        await audit.WriteAsync(new AuditEntry(AuditActions.SubscriptionChanged, "subscription", context.TenantId.ToString(), Changes: changes), ct);
        await audit.WritePlatformAsync(new AuditEntry(AuditActions.SubscriptionChanged, "subscription", context.TenantId.ToString(), Changes: changes), context.TenantId, ct);
        await outbox.EnqueueAsync(new SubscriptionChanged(context.RequireTenant(), packageKey, target.Version), ct);
    }
}

[PlatformMessage]
public sealed record PackagePublished(string PackageKey, int Version) : IIntegrationEvent;

public sealed record SubscriptionChanged(Guid TenantId, string PackageKey, int Version) : IIntegrationEvent;
