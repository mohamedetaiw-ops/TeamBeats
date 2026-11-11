using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Policies;

/// <summary>
/// Onboarding attestations (spec 025 FR-017): every published policy flagged "onboarding required" that applies to the
/// person becomes their own attestation task, outside any campaign, due when the journey says. It shows in their inbox
/// and on My policies like a campaign's, and one attestation to the version in force settles it. Without
/// <c>policy.core</c> in the plan nothing is asked.
/// </summary>
public sealed class PolicyObligations(IDbSession session, IEntitlementService entitlements, IAudiences audiences, INotificationSender notifications, IClock clock)
    : IPolicyObligations
{
    public const int MaxSourceKeyLength = 200;

    public async Task<int> RequireOnboardingAsync(Guid userId, string sourceKey, DateTimeOffset dueAt, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(sourceKey) || sourceKey.Length > MaxSourceKeyLength)
        {
            throw new ArgumentException("A source key of 1 to 200 characters is required.", nameof(sourceKey));
        }
        if (!await entitlements.IsEnabledAsync(Capabilities.PolicyCore, ct))
        {
            return 0;
        }
        var flagged = await session.QueryAsync($"select {PolicyRow.Columns} from policies.policy where onboarding_required and status <> 'retired' and current_version is not null",
            null, PolicyRow.Read, ct);
        var attested = (await session.QueryAsync(
            "select distinct on (policy_id) policy_id, version from policies.attestation where user_id = @userId order by policy_id, attested_at desc, id",
            new { userId }, r => (Policy: r.GetGuid(0), Version: r.GetInt32(1)), ct)).ToDictionary(a => a.Policy, a => a.Version);
        var now = clock.UtcNow;
        var created = 0;
        foreach (var policy in flagged)
        {
            if (attested.TryGetValue(policy.Id, out var version) && version == policy.CurrentVersion || !await audiences.IncludesAsync(policy.AppliesTo, userId, ct))
            {
                continue;
            }
            created += await session.ExecAsync(
                """
                insert into policies.obligation (tenant_id, id, user_id, policy_id, version, source_key, due_at, status, created_at)
                values (platform.current_tenant_id(), @id, @userId, @policy, @version, @sourceKey, @dueAt, 'open', @now)
                on conflict do nothing
                """, new { id = Ids.New(), userId, policy = policy.Id, version = policy.CurrentVersion!.Value, sourceKey, dueAt, now }, ct);
        }
        if (created > 0)
        {
            // No policy names in the notice: they stay behind each policy's read check.
            await notifications.SendAsync(new NotificationRequest([userId], NotificationCategories.AttestationDue, PolicyCampaigns.AttestationDueTemplate, "/policies/my",
                DedupeKey: $"policies.obligation:{sourceKey}"), ct);
        }
        return await OpenCountAsync(sourceKey, ct);
    }

    public async Task<int> OpenCountAsync(string sourceKey, CancellationToken ct = default) =>
        (int)await session.ScalarAsync<long>(
            """
            select count(*) from policies.obligation o join policies.policy p on p.id = o.policy_id
            where o.source_key = @sourceKey and o.status = 'open' and p.status <> 'retired'
            """, new { sourceKey }, ct);

    public Task CancelAsync(string sourceKey, CancellationToken ct = default) =>
        session.ExecAsync("update policies.obligation set status = 'cancelled' where source_key = @sourceKey and status = 'open'", new { sourceKey }, ct);
}
