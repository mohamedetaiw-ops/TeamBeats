using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Analytics.Contracts;

namespace TeamBeats.Modules.Hr.Attendance;

/// <summary>
/// Attendance exceptions for a location or unit dashboard (spec 021 US6): working days with an exception out of working
/// days of the given people in the period. Analytics has decided the viewer may see the scope; no person is answered.
/// A set larger than <see cref="MaxPeople"/> gets nothing rather than a slow page.
/// </summary>
internal sealed class AttendanceScopeSource(AttendanceService attendance, IEntitlementService entitlements) : IAnalyticsScopeSource
{
    public const int MaxPeople = 20_000;

    public async Task<IReadOnlyList<AnalyticsScopeFigure>> FiguresAsync(IReadOnlyCollection<Guid> people, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        if (people.Count > MaxPeople || !await entitlements.IsEnabledAsync(Capabilities.HrAttendance, ct))
        {
            return [];
        }
        var (exceptions, working) = await attendance.ExceptionDaysAsync(people, DateOnly.FromDateTime(from.UtcDateTime), DateOnly.FromDateTime(to.UtcDateTime).AddDays(-1), ct);
        return working == 0 ? [] : [new AnalyticsScopeFigure(AnalyticsScopeFigures.AttendanceExceptions, exceptions, working)];
    }
}
