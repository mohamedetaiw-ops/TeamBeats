using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Learning.Contracts;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Policies;

/// <summary>
/// Policy linked training (spec 028 US4 scenario 2, spec 025): when a material version of a policy linked to a course
/// comes into force, everyone the policy applies to is given the course, due in <see cref="DueDays"/> days. A completion
/// made before the version came into force does not count, so a changed policy means the course is taken again.
/// </summary>
public sealed class LinkedTraining(PolicyRegister register, IEntitlementService entitlements, IAudiences audiences, ILearningObligations training, IClock clock)
    : IMessageHandler<PolicyPublished>
{
    public const int DueDays = 30;

    public static string SourceKey(Guid policyId, int version) => $"policy:{policyId:N}:{version}";

    public async Task HandleAsync(PolicyPublished message, CancellationToken ct)
    {
        if (!message.Material || !await entitlements.IsEnabledAsync(Capabilities.PolicyCore, ct)
            || await register.RowAsync(message.PolicyId, false, ct) is not { CourseId: { } course, Status: not PolicyStatuses.Retired } policy)
        {
            return;
        }
        var version = await register.VersionByNumberAsync(policy.Id, message.Version, ct);
        var since = version?.EffectiveFrom ?? clock.UtcNow;
        var people = await audiences.MembersAsync(policy.AppliesTo, ct);
        var due = DateOnly.FromDateTime(since.UtcDateTime).AddDays(DueDays);
        await training.RequireAsync(people, [course], "policy", SourceKey(policy.Id, message.Version), due, since, ct);
    }
}
