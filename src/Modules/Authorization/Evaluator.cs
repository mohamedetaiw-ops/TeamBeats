using TeamBeats.Authorization.Contracts;

namespace TeamBeats.Modules.Authorization;

public sealed record AclEntry(string ResourceType, Guid ResourceId, string SubjectType, Guid SubjectId, string Permission, GrantCondition? Condition = null);

/// <summary>
/// Pure decision logic (spec 004 plan "Evaluation"). Union of grants only, with no deny. Nothing is inherited
/// up or down the hierarchy except through an explicit subtree scope or an enabled inheritance rule (FR-004).
/// A grant with a condition applies only when the condition holds (FR-014); conditions never add a grant.
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
        var notApplied = new List<DecisionReason>();
        var descriptor = (target as ResourceTarget)?.Descriptor;
        Guid? unit = target switch
        {
            OrgUnitTarget u => u.UnitId,
            ResourceTarget r => r.Descriptor.OwningOrgUnitId,
            _ => null,
        };

        void Add(DecisionReason reason, GrantCondition? condition)
        {
            if (condition is null)
            {
                reasons.Add(reason);
                return;
            }
            var trace = condition.Evaluate(key => Lookup(g, descriptor, key));
            (trace.Met ? reasons : notApplied).Add(reason with { Condition = trace });
        }

        foreach (var grant in g.Grants.Where(x => x.Permissions.Contains(permission)))
        {
            var covers = grant.ScopeType switch
            {
                ScopeType.Tenant => true,
                ScopeType.OrgUnit or ScopeType.OrgSubtree => unit is { } u && grant.CoveredUnits.Contains(u),
                ScopeType.Resource => descriptor is { } d && grant.ScopeId == d.ResourceId && grant.ScopeResourceType == d.ResourceType,
                _ => false,
            };
            if (covers)
            {
                Add(new DecisionReason("role_assignment", grant.RoleKey, grant.ScopeType, grant.ScopeId, Subject: grant.Via), grant.Condition);
            }
        }

        if (descriptor is { } res)
        {
            if (IsViewPermission(permission) && res.Visibility == ResourceVisibility.Tenant)
            {
                reasons.Add(new DecisionReason("tenant_visibility"));
            }

            foreach (var entry in acl.Where(a => a.Permission == permission && a.ResourceId == res.ResourceId && a.ResourceType == res.ResourceType))
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
                    Add(new DecisionReason("resource_acl", Subject: $"{entry.SubjectType}:{entry.SubjectId}"), entry.Condition);
                }
                else if (IsViewPermission(permission) && entry.SubjectType is "org_unit" or "org_subtree")
                {
                    // Explicit rule: members of an ancestor unit may view resources restricted to its descendants.
                    // The entry's condition still has to hold: the rule widens who the entry reaches, not what it allows.
                    foreach (var rule in g.Rules.Where(r => r.Descendants.Contains(entry.SubjectId)))
                    {
                        Add(new DecisionReason("inheritance_rule", ScopeType: ScopeType.OrgUnit, ScopeId: rule.UnitId, Rule: rule.Rule,
                            Subject: $"{entry.SubjectType}:{entry.SubjectId}"), entry.Condition);
                    }
                }
            }
        }

        return new Decision(reasons.Count > 0, reasons) { NotApplied = notApplied };
    }

    /// <summary>
    /// Org units (or the whole tenant) where the permission applies. A conditional grant counts only when its condition
    /// holds without a resource, so a condition on a resource attribute never widens coverage (fail closed).
    /// </summary>
    public static OrgCoverage Coverage(EffectiveGrants g, string permission)
    {
        if (!g.ActiveMember)
        {
            return OrgCoverage.None;
        }

        var units = new HashSet<Guid>();
        foreach (var grant in ApplicableWithoutResource(g).Where(x => x.Permissions.Contains(permission)))
        {
            if (grant.ScopeType == ScopeType.Tenant)
            {
                return new OrgCoverage(true, new HashSet<Guid>());
            }
            units.UnionWith(grant.CoveredUnits);
        }
        return new OrgCoverage(false, units);
    }

    /// <summary>Grants whose condition (if any) holds on user and request attributes alone.</summary>
    public static IEnumerable<Grant> ApplicableWithoutResource(EffectiveGrants g) =>
        g.Grants.Where(x => x.Condition is null || x.Condition.Evaluate(key => Lookup(g, null, key)).Met);

    /// <summary>
    /// Attribute values for a condition. User attributes come from the effective grants snapshot, resource attributes
    /// from the owning module's descriptor. Request attributes (spec 026) have no source yet, so they are absent.
    /// </summary>
    private static string? Lookup(EffectiveGrants g, ResourceDescriptor? resource, string key)
    {
        if (!ConditionAttributes.All.TryGetValue(key, out var attribute))
        {
            return null;
        }
        return attribute.Source switch
        {
            AttributeSource.User => g.UserAttributes.TryGetValue(key, out var v) ? v : null,
            AttributeSource.Resource => key switch
            {
                ConditionAttributes.Classification => resource?.Classification,
                ConditionAttributes.OwningOrgUnit => resource?.OwningOrgUnitId?.ToString(),
                _ => null,
            },
            _ => null,
        };
    }
}
