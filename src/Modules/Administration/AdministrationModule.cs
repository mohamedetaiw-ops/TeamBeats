using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Administration;

public sealed record NavigationSection(string Key, string LabelKey, string Path, bool WholeTenant);

public sealed record UserSummary(Guid Id, string DisplayName, string Email, string Status, string? MembershipStatus, DateTimeOffset? LastSignInAt);

/// <summary>
/// Admin shell read models (spec 006 US4, FR-008, FR-009). Navigation is server-driven: a section appears only when the
/// backend would authorize it, and every section's API enforces the same permission independently.
/// </summary>
public static class AdministrationModule
{
    public static readonly IReadOnlyList<(string Key, string Permission, string Path)> Sections =
    [
        ("tenant_settings", Permissions.TenantSettingsView, "/admin/tenant"),
        ("organization", Permissions.OrgUnitManage, "/admin/organization"),
        ("users", Permissions.UsersView, "/admin/users"),
        ("roles", Permissions.RolesView, "/admin/roles"),
        ("plan", Permissions.EntitlementsView, "/admin/plan"),
        ("identity", Permissions.IdentityConfigManage, "/admin/identity"),
        ("connections", Permissions.IntegrationManage, "/admin/connections"),
        ("pages", Permissions.PageEdit, "/admin/pages"),
        ("templates", Permissions.TemplateEdit, "/admin/templates"),
        ("branding", Permissions.ThemeEdit, "/admin/branding"),
        ("notifications", Permissions.NotificationsManage, "/admin/notifications"),
        ("calendars", Permissions.HrCalendarManage, "/admin/calendars"),
        ("audit", Permissions.AuditView, "/admin/audit"),
    ];

    /// <summary>Sections whose screens work for the whole tenant only in R1: template designers (spec 032 US5) hold their right tenant-wide, and notification settings (spec 015 US4) are for the whole tenant.</summary>
    public static readonly IReadOnlySet<string> TenantOnly = new HashSet<string>(StringComparer.Ordinal) { "templates", "notifications" };

    public static IServiceCollection AddAdministrationModule(this IServiceCollection services) => services;

    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").RequireTenantUser().WithTags("Administration");

        admin.MapGet("/navigation", async (IAuthorizationService authz, CancellationToken ct) =>
        {
            var result = new List<NavigationSection>();
            foreach (var (key, permission, path) in Sections)
            {
                var coverage = await authz.CoverageAsync(permission, ct);
                if (TenantOnly.Contains(key) ? coverage.WholeTenant : coverage.Any)
                {
                    result.Add(new NavigationSection(key, $"admin.nav.{key}", path, coverage.WholeTenant));
                }
            }
            return Results.Ok(result);
        });

        admin.MapGet("/users", async (string? q, IAuthorizationService authz, IUserDirectory users, IOrganizationHierarchy hierarchy,
            ITenantMemberships memberships, CancellationToken ct) =>
        {
            var coverage = await authz.CoverageAsync(Permissions.UsersView, ct);
            if (!coverage.Any)
            {
                throw AppException.Denied();
            }
            var list = await users.ListAsync(q, 500, ct);
            if (!coverage.WholeTenant)
            {
                var visible = await hierarchy.GetMemberUserIdsAsync(coverage.Units, ct);
                list = list.Where(u => visible.Contains(u.Id)).ToList();
            }
            var result = new List<UserSummary>();
            foreach (var u in list)
            {
                var m = await memberships.GetAsync(u.Id, ct);
                result.Add(new UserSummary(u.Id, u.DisplayName, u.Email, u.Status, m is null ? null : Sql.ToSnake(m.Status.ToString()), u.LastSignInAt));
            }
            return Results.Ok(result);
        });

        admin.MapGet("/users/{id:guid}", async (Guid id, IAuthorizationService authz, IUserDirectory users, IOrganizationHierarchy hierarchy,
            ITenantMemberships memberships, IAuthorizationProvisioning roles, CancellationToken ct) =>
        {
            var user = await users.GetAsync(id, ct) ?? throw AppException.NotFound();
            var orgMemberships = await hierarchy.GetActiveMembershipsAsync(id, ct);
            var coverage = await authz.CoverageAsync(Permissions.UsersView, ct);
            if (!coverage.WholeTenant && !orgMemberships.Any(m => coverage.Units.Contains(m.OrgUnitId)))
            {
                throw AppException.NotFound();
            }
            return Results.Ok(new
            {
                user,
                tenantMembership = await memberships.GetAsync(id, ct),
                organizationMemberships = orgMemberships,
                roleAssignments = await roles.GetUserAssignmentsAsync(id, ct),
            });
        });

        admin.MapPost("/users/{id:guid}:disable", (Guid id, IAuthorizationService authz, IUserDirectory users, ITenantMemberships memberships,
            IAuthorizationProvisioning roles, ISessionRevocation sessions, IAuditWriter audit, IOutbox outbox, IDbSession session, ITenantContext context, CancellationToken ct) =>
            SetStatus(id, MembershipStatus.Disabled, authz, users, memberships, roles, sessions, audit, outbox, session, context, ct));

        admin.MapPost("/users/{id:guid}:enable", (Guid id, IAuthorizationService authz, IUserDirectory users, ITenantMemberships memberships,
            IAuthorizationProvisioning roles, ISessionRevocation sessions, IAuditWriter audit, IOutbox outbox, IDbSession session, ITenantContext context, CancellationToken ct) =>
            SetStatus(id, MembershipStatus.Active, authz, users, memberships, roles, sessions, audit, outbox, session, context, ct));

        return app;
    }

    private static async Task<IResult> SetStatus(Guid id, MembershipStatus status, IAuthorizationService authz, IUserDirectory users,
        ITenantMemberships memberships, IAuthorizationProvisioning roles, ISessionRevocation sessions, IAuditWriter audit, IOutbox outbox,
        IDbSession session, ITenantContext context, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.UsersManage, AuthzTarget.Tenant, ct);
        _ = await users.GetAsync(id, ct) ?? throw AppException.NotFound();

        if (status == MembershipStatus.Active && await users.IsDisabledByDirectoryAsync(id, ct))
        {
            // The directory still has the account disabled (spec 003 FR-013).
            throw AppException.Conflict("conflict.user.disabled_by_directory");
        }
        if (status == MembershipStatus.Disabled)
        {
            if (id == context.ActorId)
            {
                throw AppException.Conflict("conflict.user.cannot_disable_self");
            }
            if (await roles.IsLastTenantAdministratorAsync(id, ct))
            {
                throw AppException.Conflict("conflict.tenant.last_admin");
            }
        }

        await memberships.SetStatusAsync(id, status, ct);
        if (status == MembershipStatus.Disabled)
        {
            // Effective immediately: requests check membership status, and sessions are revoked too (spec 003 US3).
            await sessions.RevokeUserSessionsAsync(id, "membership_disabled", ct);
            await outbox.EnqueueAsync(new TenantMembershipDisabled(id), ct);
        }

        await audit.WriteAsync(new AuditEntry(AuditActions.MembershipDisabled, "user", id.ToString(),
            Changes: new { status = Sql.ToSnake(status.ToString()) }), ct);
        await session.CommitAsync(ct);
        return Results.NoContent();
    }
}
