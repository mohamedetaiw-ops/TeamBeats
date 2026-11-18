using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

/// <summary>
/// Open attestation campaigns for policy compliance analytics (spec 021 US5); people lists stay in the spec 025
/// compliance view. Only compliance officers count: the members placed in the units where the viewer holds
/// <c>policy.manage</c>, all of them at tenant scope. Line managers keep the compliance view of spec 025.
/// </summary>
internal sealed class PolicyComplianceSource(IDbSession session, ITenantContext context, IEntitlementService entitlements, IAuthorizationService authz,
    IOrganizationHierarchy hierarchy, IProfiles profiles) : IAnalyticsComplianceSource
{
    /// <summary>The most members read for one call, a guard far above a large tenant's open campaigns.</summary>
    public const int MaxMembers = 200_000;

    public async Task<IReadOnlyList<AnalyticsComplianceCampaign>> OpenCampaignsAsync(CancellationToken ct = default)
    {
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        context.RequireUser();
        var coverage = await authz.CoverageAsync(Permissions.PolicyManage, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        var campaigns = await session.QueryAsync(
            """
            select c.id, c.policy_id, c.version, p.names, c.launched_at, c.deadline from policies.campaign c
            join policies.policy p on p.tenant_id = c.tenant_id and p.id = c.policy_id
            where c.status = 'open' and c.version = p.current_version
            order by c.deadline, c.id
            """, null, r => (Id: r.GetGuid(0), Policy: r.GetGuid(1), Version: r.GetInt32(2), Names: PolicyJson.Read<Dictionary<string, string>>(r.GetString(3)),
                Launched: r.GetFieldValue<DateTimeOffset>(4), Deadline: r.GetFieldValue<DateTimeOffset>(5)), ct);
        if (campaigns.Count == 0)
        {
            return [];
        }
        var members = await session.QueryAsync(
            $"""
            select campaign_id, user_id, status, attested_at from policies.campaign_member
            where campaign_id = any(@ids) and status <> 'left_audience' limit {MaxMembers}
            """, new { ids = campaigns.Select(c => c.Id).ToArray() },
            r => (Campaign: r.GetGuid(0), User: r.GetGuid(1), Status: r.GetString(2), AttestedAt: r.Get<DateTimeOffset?>("attested_at")), ct);
        var memberships = (await hierarchy.ListActiveMembershipsAsync(ct)).ToLookup(m => m.UserId);
        var visible = members.Where(m => coverage.WholeTenant || memberships[m.User].Any(u => coverage.Units.Contains(u.OrgUnitId))).ToList();
        var placements = await profiles.PlacementsAsync(visible.Select(m => m.User).Distinct().ToList(), ct);
        Guid? Primary(Guid user)
        {
            var list = memberships[user].ToList();
            return (list.FirstOrDefault(m => m.IsPrimary) ?? list.FirstOrDefault())?.OrgUnitId;
        }
        var byCampaign = visible.ToLookup(m => m.Campaign);
        return campaigns
            .Select(c => new AnalyticsComplianceCampaign(c.Id, c.Policy, c.Version, c.Names, c.Launched, c.Deadline,
                byCampaign[c.Id].Select(m => new AnalyticsComplianceMember(Primary(m.User), placements.GetValueOrDefault(m.User)?.LocationKey, m.Status, m.AttestedAt)).ToList()))
            .Where(c => c.Members.Count > 0).ToList();
    }
}
