using TeamBeats.Primitives;

namespace TeamBeats.Modules.Learning.Contracts;

/// <summary>A version of a course went live (spec 028 section 12). Retraining says whether earlier completions no longer count.</summary>
public sealed record CoursePublished(Guid CourseId, int Version, bool RequiresRetraining) : IIntegrationEvent;

/// <summary>A course was given to a learner (spec 028 section 12). Source is how: self enrolment, a rule, a manager, onboarding or a policy.</summary>
public sealed record CourseAssigned(Guid AssignmentId, Guid CourseId, Guid LearnerId, string Source, DateOnly? DueOn) : IIntegrationEvent;

/// <summary>A learner completed a course version (spec 028 section 12), for 022 onboarding steps and 025 policies.</summary>
public sealed record CourseCompleted(Guid AssignmentId, Guid CourseId, Guid LearnerId, int Version) : IIntegrationEvent;

/// <summary>A certificate was issued for a completion (spec 028 section 12). IDs only.</summary>
public sealed record CertificateIssued(Guid CertificateId, Guid CourseId, Guid LearnerId) : IIntegrationEvent;
