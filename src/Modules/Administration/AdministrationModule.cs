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

/// <summary>An admin shell section. <see cref="Badge"/> is a count that needs attention, such as open security alerts.</summary>
public sealed record NavigationSection(string Key, string LabelKey, string Path, bool WholeTenant, int? Badge = null);

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
        ("hr_integration", Permissions.HrIntegrationManage, "/admin/hr-integration"),
        ("pages", Permissions.PageEdit, "/admin/pages"),
        ("templates", Permissions.TemplateEdit, "/admin/templates"),
        ("branding", Permissions.ThemeEdit, "/admin/branding"),
        ("shell", Permissions.ShellEdit, "/admin/shell"),
        ("notifications", Permissions.NotificationsManage, "/admin/notifications"),
        ("calendars", Permissions.HrCalendarManage, "/admin/calendars"),
        ("leave", Permissions.HrManage, "/admin/leave"),
        ("journeys", Permissions.HrManage, "/admin/journeys"),
        ("attendance", Permissions.HrManage, "/admin/attendance"),
        ("letters", Permissions.HrManage, "/admin/letters"),
        ("data_changes", Permissions.HrManage, "/admin/data-changes"),
        ("expenses", Permissions.FinanceProcess, "/admin/expenses"),
        ("people", Permissions.PeopleManage, "/admin/people"),
        ("services", Permissions.ServiceManage, "/admin/services"),
        ("audit", Permissions.AuditView, "/admin/audit"),
        ("security", Permissions.SecurityView, "/admin/security"),
        ("ai", Permissions.AiPolicyManage, "/admin/ai"),
        ("search", Permissions.SearchManage, "/admin/search"),
        ("agents", Permissions.AgentView, "/admin/agents"),
        ("evaluation", Permissions.EvalView, "/admin/evaluation"),
        ("analytics", Permissions.AnalyticsView, "/admin/analytics"),
        ("kiosks", Permissions.DeviceManage, "/admin/kiosks"),
    ];

    /// <summary>A second permission that also opens a section: security officers read the AI pages with <c>ai.policy_view</c> (spec 013).</summary>
    public static readonly IReadOnlyDictionary<string, string> AlsoOpenedBy = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ai"] = Permissions.AiPolicyView,
    };

    /// <summary>
    /// Further permissions that each open a section in their own scope: evaluation pages (spec 034) serve agent designers
    /// on their units, evaluation reviewers who label calibration sets, and those who read results.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string[]> AlsoOpenedInScopeBy = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["evaluation"] = [Permissions.EvalDesign, Permissions.EvalReview, Permissions.EvalPolicyManage],
    };

    /// <summary>Sections whose screens work for the whole tenant only in R1: template designers (spec 032 US5) hold their right tenant-wide, and notification settings (spec 015 US4) the people directory policy (spec 010 US1), the shell with its footer and home views (spec 008), search tuning (spec 012 US4), adoption basics (spec 021) and the HR integration page (spec 019) are for the whole tenant.</summary>
    public static readonly IReadOnlySet<string> TenantOnly = new HashSet<string>(StringComparer.Ordinal) { "templates", "notifications", "people", "security", "search", "analytics", "shell", "hr_integration" };

    public static IServiceCollection AddAdministrationModule(this IServiceCollection services)
    {
        services.AddScoped<AdministrationNavigation>();
        services.AddScoped<TeamBeats.Modules.Experience.Contracts.IAdministrationEntry>(sp => sp.GetRequiredService<AdministrationNavigation>());
        return services;
    }

    public static IEndpointRouteBuilder MapAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin").RequireTenantUser().InAdminArea().WithTags("Administration");

        admin.MapGet("/navigation", async (AdministrationNavigation navigation, CancellationToken ct) =>
            Results.Ok(await navigation.SectionsAsync(ct)));

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
            var user = await users.GetAsync(id, ct) is { IsTest: false } found ? found : throw AppException.NotFound();
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

/// <summary>
/// The sections the caller may open (spec 006 FR-008), and the shell's question whether there is any (spec 008 FR-062).
/// Both use the same checks, so the shell offers Administration exactly when the admin shell would have a section to show.
/// </summary>
public sealed class AdministrationNavigation(IAuthorizationService authz, TeamBeats.Modules.Security.Contracts.ISecurityAlertSummary alerts)
    : TeamBeats.Modules.Experience.Contracts.IAdministrationEntry
{
    public async Task<IReadOnlyList<NavigationSection>> SectionsAsync(CancellationToken ct)
    {
        var result = new List<NavigationSection>();
        foreach (var (key, _, path) in AdministrationModule.Sections)
        {
            if (await OpensAsync(key, ct) is { } wholeTenant)
            {
                // The security section shows how many alerts are open (spec 026 US5); the section's own check already passed.
                var badge = key == "security" ? await alerts.OpenCountAsync(ct) : (int?)null;
                result.Add(new NavigationSection(key, $"admin.nav.{key}", path, wholeTenant, badge is > 0 ? badge : null));
            }
        }
        return result;
    }

    public async Task<bool> CanOpenAsync(CancellationToken ct = default)
    {
        foreach (var (key, _, _) in AdministrationModule.Sections)
        {
            if (await OpensAsync(key, ct) is not null)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Null when the caller may not open the section; otherwise whether they hold it for the whole tenant.</summary>
    private async Task<bool?> OpensAsync(string key, CancellationToken ct)
    {
        var permission = AdministrationModule.Sections.First(s => s.Key == key).Permission;
        var coverage = await authz.CoverageAsync(permission, ct);
        if (!coverage.WholeTenant && AdministrationModule.AlsoOpenedBy.TryGetValue(key, out var other) && await authz.CoverageAsync(other, ct) is { WholeTenant: true } wide)
        {
            coverage = wide;
        }
        if (AdministrationModule.AlsoOpenedInScopeBy.TryGetValue(key, out var others))
        {
            foreach (var also in others)
            {
                var more = await authz.CoverageAsync(also, ct);
                if (more.WholeTenant || (more.Any && !coverage.Any))
                {
                    coverage = more;
                }
            }
        }
        return (AdministrationModule.TenantOnly.Contains(key) ? coverage.WholeTenant : coverage.Any) ? coverage.WholeTenant : null;
    }
}
