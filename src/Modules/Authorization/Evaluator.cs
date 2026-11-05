using TeamBeats.Authorization.Contracts;

namespace TeamBeats.Modules.Authorization;

public sealed record AclEntry(string ResourceType, Guid ResourceId, string SubjectType, Guid SubjectId, string Permission);

/// <summary>
/// Pure decision logic (spec 004 plan "Evaluation"). Union of grants only, no deny in R0. Nothing is inherited
/// up or down the hierarchy except through an explicit subtree scope or an enabled inheritance rule (FR-004).
/// </summary>
public static class Evaluator
{
    public static bool IsViewPermission(string permission) => permission.EndsWith(".view", StringComparison.Ordinal);

    public static Decision Evaluate(EffectiveGrants g, string permission, AuthzTarget target, IReadOnlyList<AclEntry> acl)
    {
        if (!g.ActiveMember)
        {
            return Decision.Deny;
        }

        var reasons = new List<DecisionReason>();
        Guid? unit = target switch
        {
            OrgUnitTarget u => u.UnitId,
            ResourceTarget r => r.Descriptor.OwningOrgUnitId,
            _ => null,
        };

        foreach (var grant in g.Grants.Where(x => x.Permissions.Contains(permission)))
        {
            var covers = grant.ScopeType switch
            {
                ScopeType.Tenant => true,
                ScopeType.OrgUnit or ScopeType.OrgSubtree => unit is { } u && grant.CoveredUnits.Contains(u),
                ScopeType.Resource => target is ResourceTarget rt && grant.ScopeId == rt.Descriptor.ResourceId
                                      && grant.ScopeResourceType == rt.Descriptor.ResourceType,
                _ => false,
            };
            if (covers)
            {
                reasons.Add(new DecisionReason("role_assignment", grant.RoleKey, grant.ScopeType, grant.ScopeId, Subject: grant.Via));
            }
        }

        if (target is ResourceTarget resource)
        {
            var d = resource.Descriptor;
            if (IsViewPermission(permission) && d.Visibility == ResourceVisibility.Tenant)
            {
                reasons.Add(new DecisionReason("tenant_visibility"));
            }

            foreach (var entry in acl.Where(a => a.Permission == permission && a.ResourceId == d.ResourceId && a.ResourceType == d.ResourceType))
            {
                var matches = entry.SubjectType switch
                {
                    "user" => entry.SubjectId == g.UserId,
                    "group" => g.Groups.Contains(entry.SubjectId),
                    "org_unit" => g.MemberUnits.Contains(entry.SubjectId),
                    "org_subtree" => g.MemberUnitsAndAncestors.Contains(entry.SubjectId),
                    _ => false,
                };
                if (matches)
                {
                    reasons.Add(new DecisionReason("resource_acl", Subject: $"{entry.SubjectType}:{entry.SubjectId}"));
                }
                else if (IsViewPermission(permission) && entry.SubjectType is "org_unit" or "org_subtree")
                {
                    // Explicit rule: members of an ancestor unit may view resources restricted to its descendants.
                    foreach (var rule in g.Rules.Where(r => r.Descendants.Contains(entry.SubjectId)))
                    {
                        reasons.Add(new DecisionReason("inheritance_rule", ScopeType: ScopeType.OrgUnit, ScopeId: rule.UnitId, Rule: rule.Rule,
                            Subject: $"{entry.SubjectType}:{entry.SubjectId}"));
                    }
                }
            }
        }

        return new Decision(reasons.Count > 0, reasons);
    }

    public static OrgCoverage Coverage(EffectiveGrants g, string permission)
    {
        if (!g.ActiveMember)
        {
            return OrgCoverage.None;
        }

        var units = new HashSet<Guid>();
        foreach (var grant in g.Grants.Where(x => x.Permissions.Contains(permission)))
        {
            if (grant.ScopeType == ScopeType.Tenant)
            {
                return new OrgCoverage(true, new HashSet<Guid>());
            }
            units.UnionWith(grant.CoveredUnits);
        }
        return new OrgCoverage(false, units);
    }
}
