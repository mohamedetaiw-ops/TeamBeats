using TeamBeats.Primitives;

namespace TeamBeats.Modules.Policies.Contracts;

/// <summary>
/// A version of a policy came into force (spec 025 section 12). Search (spec 012) indexes its clauses by their anchors
/// and drops the version it replaced; <paramref name="Material"/> tells the owner and compliance whether to launch a
/// new attestation campaign.
/// </summary>
public sealed record PolicyPublished(Guid PolicyId, int Version, int? Superseded, bool Material, Guid? OrgUnitId) : IIntegrationEvent;

/// <summary>A version stopped being in force because a newer one took effect.</summary>
public sealed record PolicySuperseded(Guid PolicyId, int Version, int By) : IIntegrationEvent;

/// <summary>A person attested to a version (spec 025 FR-007).</summary>
public sealed record PolicyAttested(Guid PolicyId, int Version, Guid UserId, Guid? CampaignId, string Channel) : IIntegrationEvent;

/// <summary>The review window of a policy opened and its owner has the review task (spec 025 US5).</summary>
public sealed record PolicyReviewDue(Guid PolicyId, int Version, DateTimeOffset DueAt, Guid OwnerId) : IIntegrationEvent;

/// <summary>A campaign member's due date passed without an attestation.</summary>
public sealed record AttestationOverdue(Guid PolicyId, int Version, Guid CampaignId, Guid UserId) : IIntegrationEvent;

/// <summary>An exemption came into the register (spec 025 US7): for a person, or for a unit and its subtree, until <paramref name="EndsAt"/>.</summary>
public sealed record ExemptionApproved(Guid PolicyId, Guid ExemptionId, string Scope, Guid? UserId, Guid? OrgUnitId, DateTimeOffset EndsAt) : IIntegrationEvent;

/// <summary>An exemption reached its end date and closed on its own (spec 025 US7 scenario 3).</summary>
public sealed record ExemptionExpired(Guid PolicyId, Guid ExemptionId, string Scope, Guid? UserId, Guid? OrgUnitId) : IIntegrationEvent;

/// <summary>
/// Onboarding attestations (spec 025 FR-017, spec 022 FR-015). The onboarding journey asks a new joiner to attest to
/// every published policy flagged "onboarding required" that applies to them; each becomes the person's own attestation
/// task with the journey's deadline. <c>sourceKey</c> names what asked (a journey step) and makes a repeat a no-op.
/// Tenant comes from context.
/// </summary>
public interface IPolicyObligations
{
    /// <summary>Creates the obligations and tells the person. Policies they already attested to in their version in force are left out. Returns how many are open.</summary>
    Task<int> RequireOnboardingAsync(Guid userId, string sourceKey, DateTimeOffset dueAt, CancellationToken ct = default);

    /// <summary>How many obligations of the source are still open (a retired policy no longer counts).</summary>
    Task<int> OpenCountAsync(string sourceKey, CancellationToken ct = default);

    /// <summary>Cancels the open obligations of the source (the journey was cancelled).</summary>
    Task CancelAsync(string sourceKey, CancellationToken ct = default);
}

/// <summary>A policy another module links to: its code and its names by locale.</summary>
public sealed record PolicyReference(Guid PolicyId, string Code, IReadOnlyDictionary<string, string> Names);

/// <summary>
/// Policies other modules link to (spec 026 US7: the policies of a compliance control). The caller keeps the policy ID;
/// readers open the policy through the Policies routes, which check access again. Every call is in the current tenant,
/// as the current user.
/// </summary>
public interface IPolicyReferences
{
    /// <summary>The policy when the caller may see it, otherwise null.</summary>
    Task<PolicyReference?> VisibleAsync(Guid policyId, CancellationToken ct = default);
}
