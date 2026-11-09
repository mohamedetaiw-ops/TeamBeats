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

/// <summary>A campaign member's due date passed without an attestation.</summary>
public sealed record AttestationOverdue(Guid PolicyId, int Version, Guid CampaignId, Guid UserId) : IIntegrationEvent;
