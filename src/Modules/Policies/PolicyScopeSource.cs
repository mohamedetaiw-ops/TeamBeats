using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Policies;

/// <summary>
/// Policy compliance for a location or unit dashboard (spec 021 US6): the given people's attestations in open campaigns
/// for the version in force, as one count out of the people in them. Analytics has decided the viewer may see the scope.
/// </summary>
internal sealed class PolicyScopeSource(IDbSession session, IEntitlementService entitlements) : IAnalyticsScopeSource
{
    public async Task<IReadOnlyList<AnalyticsScopeFigure>> FiguresAsync(IReadOnlyCollection<Guid> people, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.PolicyCore, ct))
        {
            return [];
        }
        var rows = await session.QueryAsync(
            """
            select count(*) filter (where m.status = 'attested'), count(*) from policies.campaign_member m
            join policies.campaign c on c.tenant_id = m.tenant_id and c.id = m.campaign_id
            join policies.policy p on p.tenant_id = c.tenant_id and p.id = c.policy_id
            where c.status = 'open' and c.version = p.current_version and m.status <> 'left_audience' and m.user_id = any(@people)
            """, new { people = people.ToArray() }, r => (Attested: (int)r.GetInt64(0), All: (int)r.GetInt64(1)), ct);
        var (attested, all) = rows.Count == 0 ? (0, 0) : rows[0];
        return all == 0 ? [] : [new AnalyticsScopeFigure(AnalyticsScopeFigures.Compliance, attested, all)];
    }
}
