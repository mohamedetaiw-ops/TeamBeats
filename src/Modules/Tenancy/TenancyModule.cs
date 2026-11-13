using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Tenancy;

public static class TenancyModule
{
    public static readonly MigrationSource Migrations = new("tenancy", typeof(TenancyModule).Assembly);

    public static IServiceCollection AddTenancyModule(this IServiceCollection services)
    {
        services.AddScoped<TenantRegistry>();
        services.AddScoped<ITenantRegistry>(sp => sp.GetRequiredService<TenantRegistry>());
        services.AddScoped<ITenantLister>(sp => sp.GetRequiredService<TenantRegistry>());
        services.AddScoped<PlatformTenants>();
        services.AddScoped<TenantSettingsService>();
        services.AddScoped<ITenantSettingsReader>(sp => sp.GetRequiredService<TenantSettingsService>());
        services.AddScoped<OrgHierarchyService>();
        services.AddScoped<OrgMembershipService>();
        services.AddScoped<DirectoryMapping.DirectoryMappingService>();
        services.AddMessageHandler<TeamBeats.Modules.Identity.Contracts.DirectorySynced, DirectoryMapping.ApplyMappingAfterDirectorySync>("tenancy.directory_mapping_apply");
        services.AddMessageHandler<TeamBeats.Modules.Integrations.Contracts.ConnectionDeleted, DirectoryMapping.ReleaseMappingOnConnectionDeleted>("tenancy.directory_mapping_release");
        services.AddScoped<HrMemberships>();
        services.AddMessageHandler<TeamBeats.Modules.Integrations.Contracts.EmployeeTransferred, MoveMembershipOnTransfer>("tenancy.hr_transfer");
        services.AddScoped<TenancyContracts>();
        services.AddScoped<IOrganizationHierarchy>(sp => sp.GetRequiredService<TenancyContracts>());
        services.AddScoped<ITenantMemberships>(sp => sp.GetRequiredService<TenancyContracts>());
        services.AddScoped<ITestIdentityMemberships, TestIdentityMemberships>();
        return services;
    }

    public static IEndpointRouteBuilder MapTenancyEndpoints(this IEndpointRouteBuilder app)
    {
        MapPlatform(app);
        MapTenant(app);
        MapOrganization(app);
        app.MapOrgPublicEndpoints();
        return app;
    }

    private static void MapPlatform(IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/v1/platform/tenants").RequirePlatformOperator().WithTags("Platform: tenants");

        platform.MapPost("/", async (CreateTenantRequest request, HttpRequest http, PlatformTenants tenants, IdempotencyStore idempotency,
            ITenantContext context, IDbSession session, CancellationToken ct) =>
        {
            var key = http.Headers[IdempotencyStore.HeaderName].ToString();
            var scope = $"platform.tenant.create:{context.ActorId}";
            var hash = IdempotencyStore.Hash(request);
            if (!string.IsNullOrEmpty(key) && await idempotency.FindAsync(scope, key, hash, ct) is { } stored)
            {
                return Results.Json(stored.Body, statusCode: stored.StatusCode);
            }

            var created = await tenants.CreateAsync(request, ct);
            if (!string.IsNullOrEmpty(key))
            {
                await idempotency.SaveAsync(scope, key, hash, StatusCodes.Status201Created, created, ct);
            }
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/platform/tenants/{created.Id}", created);
        });

        platform.MapGet("/", async (string? status, string? q, PlatformTenants tenants, CancellationToken ct) =>
            Results.Ok(await tenants.ListAsync(status, q, ct)));

        platform.MapPost("/{id:guid}:suspend", (Guid id, StatusChangeRequest? body, PlatformTenants t, IDbSession s, CancellationToken ct) =>
            ChangeStatus(id, TenantLifecycleStatus.Suspended, body?.Reason, t, s, ct));
        platform.MapPost("/{id:guid}:reactivate", (Guid id, StatusChangeRequest? body, PlatformTenants t, IDbSession s, CancellationToken ct) =>
            ChangeStatus(id, TenantLifecycleStatus.Active, body?.Reason, t, s, ct));
        platform.MapPost("/{id:guid}:close", (Guid id, StatusChangeRequest? body, PlatformTenants t, IDbSession s, CancellationToken ct) =>
            ChangeStatus(id, TenantLifecycleStatus.Closed, body?.Reason, t, s, ct));
    }

    private static async Task<IResult> ChangeStatus(Guid id, TenantLifecycleStatus to, string? reason, PlatformTenants tenants, IDbSession session, CancellationToken ct)
    {
        var view = await tenants.ChangeStatusAsync(id, to, reason, ct);
        await session.CommitAsync(ct);
        return Results.Ok(view);
    }

    private static void MapTenant(IEndpointRouteBuilder app)
    {
        var tenant = app.MapGroup("/api/v1/tenant").RequireTenantUser().InAdminArea().WithTags("Tenant: settings");

        tenant.MapGet("/settings", async (IAuthorizationService authz, TenantSettingsService settings, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.TenantSettingsView, AuthzTarget.Tenant, ct);
            return Results.Ok(await settings.GetAsync(ct));
        });

        tenant.MapPut("/settings", async (UpdateTenantSettingsRequest request, IAuthorizationService authz, TenantSettingsService settings,
            IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct);
            var result = await settings.UpdateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        tenant.MapPut("/branding/logo", async (HttpRequest http, IAuthorizationService authz, TenantSettingsService settings,
            IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct);
            using var buffer = new MemoryStream();
            var limited = new byte[ImageValidator.MaxBytes + 1];
            int read, total = 0;
            while ((read = await http.Body.ReadAsync(limited.AsMemory(total, limited.Length - total), ct)) > 0)
            {
                total += read;
                if (total > ImageValidator.MaxBytes)
                {
                    throw AppException.Validation("logo", "validation.logo.size");
                }
            }
            await settings.SetLogoAsync(http.ContentType, limited[..total], ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        }).DisableAntiforgery();

        tenant.MapGet("/branding/logo", async (TenantSettingsService settings, CancellationToken ct) =>
        {
            var logo = await settings.GetLogoAsync(ct) ?? throw AppException.NotFound();
            return Results.File(logo.Content, logo.ContentType);
        });

        tenant.MapGet("/admins", async (IAuthorizationService authz, TenantSettingsService settings, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.TenantAdminsManage, AuthzTarget.Tenant, ct);
            return Results.Ok(await settings.ListAdminsAsync(ct));
        });

        tenant.MapPost("/admins", async (AppointAdminRequest request, IAuthorizationService authz, TenantSettingsService settings,
            IDbSession session, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.TenantAdminsManage, AuthzTarget.Tenant, ct);
            var result = await settings.AppointAdminAsync(request.Email, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/tenant/admins/{result.Id}", result);
        }).RequireStepUp();

        // Default Employee access per membership type (spec 004 section 6). Guests never get it, so theirs is fixed.
        tenant.MapGet("/membership-policies", async (IAuthorizationService authz, ITenantMemberships memberships, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.TenantSettingsView, AuthzTarget.Tenant, ct);
            var policies = await memberships.GetMembershipTypePoliciesAsync(ct);
            return Results.Ok(MembershipTypes.All.Select(type => new MembershipPolicyView(type,
                type != MembershipTypes.Guest && (!policies.TryGetValue(type, out var on) || on), Fixed: type == MembershipTypes.Guest)));
        });

        tenant.MapPut("/membership-policies/{membershipType}", async (string membershipType, MembershipPolicyRequest request,
            IAuthorizationService authz, IDbSession session, IAuditWriter audit, ITenantContext context, CancellationToken ct) =>
        {
            await authz.DemandAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct);
            if (!MembershipTypes.All.Contains(membershipType))
            {
                throw AppException.NotFound();
            }
            if (membershipType == MembershipTypes.Guest && request.DefaultEmployeeRole)
            {
                throw AppException.Validation("defaultEmployeeRole", "validation.membership_policy.guest_fixed");
            }
            await session.ExecAsync(
                """
                insert into tenancy.membership_type_policy (tenant_id, membership_type, default_employee_role) values (@t, @m, @d)
                on conflict (tenant_id, membership_type) do update set default_employee_role = excluded.default_employee_role
                """, new { t = context.RequireTenant(), m = membershipType, d = request.DefaultEmployeeRole }, ct);
            await session.ExecAsync("update tenancy.hierarchy_version set version = version + 1", null, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.TenantSettingsChanged, "membership_type_policy", membershipType,
                Changes: new { fields = new[] { "defaultEmployeeRole" }, defaultEmployeeRole = request.DefaultEmployeeRole }), ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });
    }

    private static void MapOrganization(IEndpointRouteBuilder app)
    {
        var org = app.MapGroup("/api/v1/org").RequireTenantUser().WithTags("Organization");
        DirectoryMapping.DirectoryMappingEndpoints.MapDirectoryMappingEndpoints(app);

        org.MapGet("/units", async (Guid? parentId, int? depth, OrgHierarchyService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(parentId, depth, ct)));

        org.MapGet("/units/{id:guid}", async (Guid id, OrgHierarchyService service, CancellationToken ct) =>
            Results.Ok(await service.GetAsync(id, ct)));

        org.MapPost("/units", async (CreateOrgUnitRequest request, OrgHierarchyService service, IDbSession session, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/org/units/{result.Unit.Id}", result);
        });

        org.MapPatch("/units/{id:guid}", async (Guid id, UpdateOrgUnitRequest request, OrgHierarchyService service, IDbSession session, CancellationToken ct) =>
        {
            var result = await service.UpdateAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Ok(result);
        });

        org.MapPost("/units/{id:guid}:move", async (Guid id, MoveOrgUnitRequest request, OrgHierarchyService service, IDbSession session, CancellationToken ct) =>
        {
            var affected = await service.MoveAsync(id, request.NewParentId, ct);
            await session.CommitAsync(ct);
            return Results.Ok(new { affectedUnits = affected });
        });

        org.MapPost("/units/{id:guid}:deactivate", async (Guid id, DeactivateOrgUnitRequest request, OrgHierarchyService service,
            OrgMembershipService memberships, IDbSession session, CancellationToken ct) =>
        {
            await service.DeactivateAsync(id, request, memberships, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        org.MapGet("/units/{id:guid}/memberships", async (Guid id, OrgMembershipService service, CancellationToken ct) =>
            Results.Ok(await service.ListForUnitAsync(id, ct)));

        org.MapPost("/units/{id:guid}/memberships", async (Guid id, AddMembershipRequest request, OrgMembershipService service,
            IDbSession session, CancellationToken ct) =>
        {
            var result = await service.AddAsync(id, request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/org/memberships/{result.Id}", result);
        });

        org.MapPost("/memberships/{id:guid}:end", async (Guid id, OrgMembershipService service, IDbSession session, CancellationToken ct) =>
        {
            await service.EndAsync(id, ct);
            await session.CommitAsync(ct);
            return Results.NoContent();
        });

        org.MapGet("/unit-types", async (OrgHierarchyService service, IAuthorizationService authz, CancellationToken ct) =>
        {
            if (!await authz.HoldsAnywhereAsync(Permissions.OrgUnitView, ct))
            {
                throw AppException.Denied();
            }
            return Results.Ok(await service.ListTypesAsync(ct));
        });

        org.MapPost("/unit-types", async (CreateUnitTypeRequest request, OrgHierarchyService service, IDbSession session, CancellationToken ct) =>
        {
            await service.CreateTypeAsync(request, ct);
            await session.CommitAsync(ct);
            return Results.Created($"/api/v1/org/unit-types/{request.Key}", request);
        });
    }
}

public sealed record MembershipPolicyRequest(bool DefaultEmployeeRole);

/// <summary>Whether members of a membership type get the Employee role at tenant scope by default. <c>Fixed</c> marks a type whose answer the tenant cannot change.</summary>
public sealed record MembershipPolicyView(string MembershipType, bool DefaultEmployeeRole, bool Fixed);
