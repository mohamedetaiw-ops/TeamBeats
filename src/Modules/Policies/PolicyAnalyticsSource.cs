using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Policies;

/// <summary>
/// Policies for item analytics (spec 021 US1, spec 025). The analytics of a policy are for its owner and the compliance
/// officers who manage it; a reader the policy applies to is denied, anyone else gets not found. While an attestation
/// campaign is open for the version in force, the audience is the campaign's members still in it and acknowledgement is
/// their attestation; otherwise it is the people the policy applies to now.
/// </summary>
internal sealed class PolicyAnalyticsSource(IDbSession session, ITenantContext context, IEntitlementService entitlements, PolicyRegister register,
    IAudiences audiences, INotificationSender notifications) : IAnalyticsItemSource
{
    public const string Reminder = "policies.analytics.reminder";

    public string Source => "policies";

    private async Task<PolicyRow> LookedAfterAsync(Guid policyId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.PolicyCore, ct);
        var user = context.RequireUser();
        var policy = await register.RowAsync(policyId, false, ct);
        if (policy is not { CurrentVersion: not null })
        {
            throw AppException.NotFound();
        }
        if (policy.Owner == user || await register.ManagesAsync(policy, ct))
        {
            return policy;
        }
        if (await register.ReadsPublishedAsync(policy, ct))
        {
            throw AppException.Denied();
        }
        throw AppException.NotFound();
    }

    public async Task<AnalyticsItem> DescribeAsync(Guid itemId, CancellationToken ct = default)
    {
        var policy = await LookedAfterAsync(itemId, ct);
        var members = await session.QueryAsync(
            """
            select m.user_id, m.status = 'attested' from policies.campaign_member m
            join policies.campaign c on c.tenant_id = m.tenant_id and c.id = m.campaign_id
            where c.policy_id = @itemId and c.version = @version and c.status = 'open' and m.status <> 'left_audience'
            """, new { itemId, version = policy.CurrentVersion!.Value }, r => (User: r.GetGuid(0), Attested: r.GetBoolean(1)), ct);
        if (members.Count > 0)
        {
            return new AnalyticsItem(itemId, policy.Names, policy.EffectiveFrom, members.Select(m => m.User).ToHashSet(), true,
                members.Where(m => m.Attested).Select(m => m.User).ToHashSet(), 0, 0);
        }
        return new AnalyticsItem(itemId, policy.Names, policy.EffectiveFrom, await audiences.MembersAsync(policy.AppliesTo, ct), false, new HashSet<Guid>(), 0, 0);
    }

    public async Task<int> RemindAsync(Guid itemId, IReadOnlyCollection<Guid> people, CancellationToken ct = default)
    {
        var described = await DescribeAsync(itemId, ct);
        var targets = people.Where(described.Audience.Contains).Distinct().ToArray();
        var category = described.AcknowledgementRequired ? NotificationCategories.AttestationDue : NotificationCategories.Announcement;
        var sent = 0;
        foreach (var chunk in targets.Chunk(500))
        {
            var result = await notifications.SendAsync(new NotificationRequest(chunk, category, Reminder, PolicySearchSource.Link(itemId),
                DedupeKey: $"policies:{itemId:N}:remind"), ct);
            sent += result.Created + result.Grouped;
        }
        return sent;
    }
}
