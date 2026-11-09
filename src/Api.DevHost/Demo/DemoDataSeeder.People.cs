using TeamBeats.Primitives;
using TeamBeats.Modules.Authorization;
using TeamBeats.Modules.People;
using TeamBeats.Modules.Tenancy;
using TeamBeats.Modules.Tenancy.DirectoryMapping;

namespace TeamBeats.Api.DevHost.Demo;

public sealed partial class DemoDataSeeder
{
    /// <summary>
    /// Maps the organization from the synced directory's attributes (company, then department) once, when the tenant has a
    /// directory and no mapping yet, then gives the units their names in the other locales.
    /// </summary>
    private async Task SeedOrgUnitsAsync(Run run, CancellationToken ct)
    {
        if (run.Pack.OrgMapping is not { } plan)
        {
            return;
        }
        var mapped = await AsAsync(run, AdminPersona, 0, async sp =>
        {
            var mapping = sp.GetRequiredService<DirectoryMappingService>();
            if (await mapping.GetAsync(ct) is not null)
            {
                return false;
            }
            try
            {
                await mapping.SaveAsync(new OrgMappingRequest(MappingStrategies.Attributes, plan.Levels, null,
                    plan.Aliases?.Select(a => new AliasRule(a.Key, a.Value)).ToList(), null, null, 100), ct);
            }
            catch (AppException e) when (e.DetailKey == "conflict.org_mapping.no_directory")
            {
                run.Report.Skipped.Add("org units: no directory synced");
                return false;
            }
            await mapping.PreviewAsync(ct);
            await mapping.ApproveAsync(ct);
            return true;
        }, ct);
        if (mapped)
        {
            run.Report.Add("org mapping");
            await DispatchAsync(ct);
        }

        if (plan.UnitNames is not { Count: > 0 } names)
        {
            return;
        }
        var units = await AsAsync(run, AdminPersona, 0, sp => sp.GetRequiredService<OrgHierarchyService>().ListAsync(null, null, ct), ct) ?? [];
        foreach (var unit in units)
        {
            var name = unit.Names.Values.FirstOrDefault(n => names.ContainsKey(n));
            if (name is null)
            {
                continue;
            }
            var missing = names[name].Where(n => unit.Names.GetValueOrDefault(n.Key) != n.Value).ToDictionary(n => n.Key, n => n.Value);
            if (missing.Count == 0)
            {
                continue;
            }
            await ItemAsync(run, $"unit name {name}", () => AsAsync(run, AdminPersona, 0,
                sp => sp.GetRequiredService<OrgHierarchyService>().UpdateAsync(unit.Id, new UpdateOrgUnitRequest(missing, null), ct), ct));
            run.Report.Add("unit names");
        }
    }

    /// <summary>Arabic and English names (admin fields) and each person's own time zone (a field people set themselves).</summary>
    private async Task SeedProfilesAsync(Run run, CancellationToken ct)
    {
        foreach (var profile in run.Pack.Profiles)
        {
            if (run.Person(profile.Person) is not { } userId)
            {
                continue;
            }
            if (profile.Names is { Count: > 0 } names)
            {
                await ItemAsync(run, $"names of {profile.Person}", () => AsAsync(run, AdminPersona, 0, async sp =>
                {
                    var people = sp.GetRequiredService<PeopleService>();
                    var current = await people.GetAsync(userId, ct);
                    if (names.All(n => current.Names.GetValueOrDefault(n.Key) == n.Value))
                    {
                        return;
                    }
                    await people.UpdateAdminFieldsAsync(userId, new AdminFieldsRequest(null, names), ct);
                    run.Report.Add("profile names");
                }, ct));
            }
            if (profile.TimeZone is { } zone)
            {
                await ItemAsync(run, $"time zone of {profile.Person}", () => AsAsync(run, profile.Person, 0, async sp =>
                {
                    var people = sp.GetRequiredService<PeopleService>();
                    var own = await people.OwnAsync(ct);
                    if (own.Fields.FirstOrDefault(f => f.Key == ProfileFields.TimeZone) is not { Editable: true } field || field.Value == zone)
                    {
                        return;
                    }
                    await people.UpdateOwnAsync(new OwnProfileRequest(new Dictionary<string, string?> { [ProfileFields.TimeZone] = zone }, null), ct);
                    run.Report.Add("time zones");
                }, ct));
            }
        }
    }

    /// <summary>Custom roles the demo needs, then each role assignment for the whole tenant, each added once.</summary>
    private async Task SeedRolesAsync(Run run, CancellationToken ct)
    {
        if (run.Pack.Roles.Count > 0)
        {
            await ItemAsync(run, "roles", () => AsAsync(run, AdminPersona, 0, async sp =>
            {
                var admin = sp.GetRequiredService<AuthorizationAdmin>();
                var known = (await admin.ListRolesAsync(ct)).Select(r => r.Key).ToHashSet(StringComparer.Ordinal);
                foreach (var role in run.Pack.Roles.Where(r => !known.Contains(r.Key)))
                {
                    await admin.DefineRoleAsync(new DefineRoleRequest(role.Key, role.Names, role.Permissions, false), ct);
                    run.Report.Add("roles");
                }
            }, ct));
        }
        foreach (var assignment in run.Pack.Assignments)
        {
            if (run.Person(assignment.Person) is not { } userId)
            {
                continue;
            }
            await ItemAsync(run, $"role {assignment.Role} of {assignment.Person}", () => AsAsync(run, AdminPersona, 0, async sp =>
            {
                var admin = sp.GetRequiredService<AuthorizationAdmin>();
                if ((await admin.ListAssignmentsAsync(userId, ct)).Any(a => a.RoleKey == assignment.Role && a.ScopeType == "tenant"))
                {
                    return;
                }
                await admin.AssignAsync(new AssignRoleRequest("user", userId, assignment.Role, "tenant", null, null), ct);
                run.Report.Add("role assignments");
            }, ct));
        }
    }
}
