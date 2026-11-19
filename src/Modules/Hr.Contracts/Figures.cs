using TeamBeats.Primitives;

namespace TeamBeats.Modules.Hr.Contracts;

/// <summary>
/// HR figures for the analytics HR dashboard (spec 021 section 2, all aggregated): leave requests filed in the period
/// per leave type (amount: approved days), approved leave per ISO week of its start (amount: days), attendance requests
/// per kind, onboarding journeys started per template (amount: share of due steps done), exit interview reasons, and
/// letters, data changes and expense claims filed. Sensitive leave types (spec 022 section 6) are counted together
/// under the key <see cref="SensitiveLeave"/>, without their names.
/// </summary>
public sealed record HrSummary(
    long LeaveRequests,
    long LeavePeople,
    double LeaveDays,
    IReadOnlyList<GroupFigure> LeaveTypes,
    IReadOnlyList<GroupFigure> LeaveWeeks,
    IReadOnlyList<GroupFigure> Attendance,
    IReadOnlyList<GroupFigure> Onboarding,
    long ExitInterviews,
    IReadOnlyList<GroupFigure> ExitReasons,
    IReadOnlyList<GroupFigure> Requests)
{
    public const string SensitiveLeave = "sensitive";
}

/// <summary>HR figures for analytics; the caller decides who may see them. Current tenant only, no person named.</summary>
public interface IHrFigures
{
    Task<HrSummary> SummaryAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
