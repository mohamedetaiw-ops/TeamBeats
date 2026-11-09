using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Evaluation;

public sealed record PersonaRequest(Dictionary<string, string>? Names, Guid? OrgUnitId, List<string>? Roles, string? MembershipType);

public sealed record PersonaView(Guid Id, Dictionary<string, string> Names, Guid OrgUnitId, IReadOnlyList<string> Roles, string MembershipType, DateTimeOffset CreatedAt);

/// <summary>
/// Personas (US2 scenario 2, FR-006): a test profile with an org unit, roles and a membership type. Each one is backed by
/// a synthetic test identity that cannot sign in, appear in the directory or be notified. A designer can only make
/// personas in units where they hold <c>eval.design</c>, and never with an administrative role, so a persona never has
/// more access than the person who made it could test with.
/// </summary>
public sealed class PersonaService(IDbSession session, ITenantContext context, IAuthorizationService authz, IAuthorizationProvisioning roles,
    ITestIdentities identities, ITestIdentityMemberships memberships, IOrganizationHierarchy hierarchy, IAuditWriter audit, IClock clock)
{
    public const int MaxRoles = 5;

    public async Task<IReadOnlyList<PersonaView>> ListAsync(CancellationToken ct)
    {
        var design = await authz.CoverageAsync(Permissions.EvalDesign, ct);
        var view = await authz.CoverageAsync(Permissions.EvalView, ct);
        if (!design.Any && !view.Any)
        {
            throw AppException.Denied();
        }
        var all = design.WholeTenant || view.WholeTenant;
        var units = design.Units.Concat(view.Units).Distinct().ToArray();
        return await session.QueryAsync(
            "select id, names::text, org_unit_id, roles, membership_type, created_at from evaluation.persona where @all or org_unit_id = any(@units) order by created_at, id",
            new { all, units }, Map, ct);
    }

    private static PersonaView Map(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1))!, r.GetGuid(2), r.GetFieldValue<string[]>(3), r.GetString(4),
            r.GetFieldValue<DateTimeOffset>(5));

    public async Task<PersonaView> CreateAsync(PersonaRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var names = (request.Names ?? []).Where(n => !string.IsNullOrWhiteSpace(n.Value)).ToDictionary(n => n.Key, n => n.Value.Trim());
        if (names.Count == 0 || names.Any(n => !CaseRules.Locales.Contains(n.Key) || n.Value.Length > 80))
        {
            errors.Add("names", "validation.eval.persona_names");
        }
        if (request.MembershipType is null || !MembershipTypes.All.Contains(request.MembershipType))
        {
            errors.Add("membershipType", "validation.eval.persona_membership");
        }
        if (request.OrgUnitId is null || !await hierarchy.UnitExistsAsync(request.OrgUnitId.Value, ct))
        {
            errors.Add("orgUnitId", "validation.eval.persona_unit");
        }
        var roleKeys = (request.Roles ?? []).Distinct().ToList();
        if (roleKeys.Count > MaxRoles)
        {
            errors.Add("roles", "validation.eval.persona_roles");
        }
        errors.ThrowIfAny();

        var unit = request.OrgUnitId!.Value;
        await authz.DemandAsync(Permissions.EvalDesign, AuthzTarget.OrgUnit(unit), ct);
        for (var i = 0; i < roleKeys.Count; i++)
        {
            if (!await roles.IsBuiltInRoleKeyAsync(roleKeys[i], ct))
            {
                errors.Add($"roles[{i}]", "validation.eval.persona_role_unknown");
            }
            else if ((await roles.RolePermissionsAsync(roleKeys[i], ct)).Overlaps(Permissions.Privileged))
            {
                errors.Add($"roles[{i}]", "validation.eval.persona_role_privileged");
            }
        }
        errors.ThrowIfAny();

        var id = Ids.New();
        var user = context.RequireUser();
        var now = clock.UtcNow;
        var display = names.GetValueOrDefault("en") ?? names.Values.First();
        var identity = await identities.CreateAsync(display, ct);
        await memberships.AddAsync(identity, request.MembershipType!, unit, ct);
        foreach (var role in roleKeys)
        {
            await roles.AssignBuiltInRoleAsync(identity, role, ScopeType.OrgUnit, unit, user, ct);
        }
        await session.ExecAsync(
            """
            insert into evaluation.persona (tenant_id, id, names, org_unit_id, roles, membership_type, user_id, created_by, created_at)
            values (platform.current_tenant_id(), @id, @names, @unit, @roles, @type, @identity, @user, @now)
            """, new { id, names = Jsonb.From(names), unit, roles = roleKeys.ToArray(), type = request.MembershipType, identity, user, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalPersonaCreated, "eval_persona", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { roles = roleKeys, membershipType = request.MembershipType, identity }), ct);
        return new PersonaView(id, names, unit, roleKeys, request.MembershipType!, now);
    }

    /// <summary>
    /// The default persona of a unit: an employee of it with no extra roles, used for platform cases and cases that name
    /// no persona. Made on first use by the person who starts the run, who already holds <c>eval.design</c> on the unit.
    /// </summary>
    internal async Task<(Guid Id, Guid UserId)> DefaultAsync(Guid unit, CancellationToken ct)
    {
        var found = await session.QueryAsync("select id, user_id from evaluation.persona where is_default and org_unit_id = @unit", new { unit },
            r => (r.GetGuid(0), r.GetGuid(1)), ct);
        if (found.Count > 0)
        {
            return found[0];
        }
        var id = Ids.New();
        var names = new Dictionary<string, string> { ["en"] = "Employee of the unit", ["ar"] = "موظف في الوحدة" };
        var identity = await identities.CreateAsync(names["en"], ct);
        await memberships.AddAsync(identity, MembershipTypes.Employee, unit, ct);
        await session.ExecAsync(
            """
            insert into evaluation.persona (tenant_id, id, names, org_unit_id, roles, membership_type, user_id, created_by, created_at, is_default)
            values (platform.current_tenant_id(), @id, @names, @unit, '{}', @type, @identity, @user, @now, true)
            """, new { id, names = Jsonb.From(names), unit, type = MembershipTypes.Employee, identity, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.EvalPersonaCreated, "eval_persona", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { roles = Array.Empty<string>(), membershipType = MembershipTypes.Employee, identity, isDefault = true }), ct);
        return (id, identity);
    }
}
