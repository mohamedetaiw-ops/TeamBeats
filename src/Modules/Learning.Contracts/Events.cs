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

/// <summary>An assignment passed its due date and grace period without completion (spec 028 section 12). Sent once per assignment.</summary>
public sealed record CourseOverdue(Guid AssignmentId, Guid CourseId, Guid LearnerId, DateOnly DueOn) : IIntegrationEvent;

/// <summary>
/// Training that another module asks for (spec 028 FR-011): an onboarding step (022) or a policy version (025) gives
/// courses to people with a due date, and asks later whether they are done. <c>sourceKey</c> names what asked and makes
/// a repeat a no-op. Tenant comes from context. Without <c>learning.mandatory</c> in the plan nothing is assigned.
/// </summary>
public interface ILearningObligations
{
    /// <summary>
    /// Gives each published course to each person, due on the date (moved to their next working day). A completion that
    /// still counts, made at or after <paramref name="completedSince"/> when given, settles it at once; an older one means
    /// the course is taken again. <paramref name="source"/> is <c>onboarding</c> or <c>policy</c>. Returns how many of the
    /// source's obligations are open.
    /// </summary>
    Task<int> RequireAsync(IReadOnlyCollection<Guid> userIds, IReadOnlyCollection<Guid> courseIds, string source, string sourceKey, DateOnly dueOn,
        DateTimeOffset? completedSince = null, CancellationToken ct = default);

    /// <summary>How many obligations of the source are still open (a retired course no longer counts).</summary>
    Task<int> OpenCountAsync(string sourceKey, CancellationToken ct = default);

    /// <summary>Cancels the open obligations of the source and withdraws the assignments only they gave.</summary>
    Task CancelAsync(string sourceKey, CancellationToken ct = default);

    /// <summary>Which of the courses are published now, to check a template or a policy link when it is saved.</summary>
    Task<IReadOnlySet<Guid>> PublishedAsync(IReadOnlyCollection<Guid> courseIds, CancellationToken ct = default);

    /// <summary>Whether the person has a completion of the course that still counts, made at or after the time when given.</summary>
    Task<bool> CompletedAsync(Guid userId, Guid courseId, DateTimeOffset? since, CancellationToken ct = default);
}
