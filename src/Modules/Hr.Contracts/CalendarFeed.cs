namespace TeamBeats.Modules.Hr.Contracts;

/// <summary>A public holiday of a working calendar, with its name in each language. A half day is still half a working day.</summary>
public sealed record HolidayEntry(DateOnly Date, IReadOnlyDictionary<string, string> Names, bool HalfDay);

/// <summary>
/// One leave request as a date range for the unified calendar (spec 037 FR-014, FR-015). <see cref="End"/> is exclusive.
/// <see cref="TypeKey"/> and <see cref="TypeNames"/> are set only where the viewer may see the type (spec 022 section 6);
/// <see cref="RequestId"/> only where the viewer may open the request. Reasons, comments and documents never leave Hr.
/// </summary>
public sealed record LeaveRange(
    Guid UserId,
    DateOnly Start,
    DateOnly End,
    bool StartHalf,
    bool EndHalf,
    string Status,
    Guid? RequestId,
    string? TypeKey,
    IReadOnlyDictionary<string, string>? TypeNames);

/// <summary>
/// Holidays and leave for the unified calendar of spec 037. All calls are in the current tenant and for the signed-in
/// viewer; the visibility rules of spec 022 are applied here, never by the caller.
/// </summary>
public interface IHrCalendarFeed
{
    /// <summary>Holidays of the working calendar of the person's location (primary unit), between two dates, both included.</summary>
    Task<IReadOnlyList<HolidayEntry>> HolidaysAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken ct = default);

    /// <summary>
    /// Approved and pending leave of the given people overlapping the dates (both included). Approved leave is returned for
    /// anyone in the list; pending leave only for the viewer and people the viewer manages. Empty when <c>hr.leave</c> is off.
    /// </summary>
    Task<IReadOnlyList<LeaveRange>> LeaveAsync(IReadOnlyCollection<Guid> userIds, DateOnly from, DateOnly to, CancellationToken ct = default);
}
