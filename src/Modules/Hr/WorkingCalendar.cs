using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;

namespace TeamBeats.Modules.Hr;

/// <summary>
/// Resolves the calendar that applies (spec 022 FR-007): the nearest assigned ancestor of the unit, else the tenant
/// default, else the built-in Monday to Friday week with no holidays. Snapshots are kept for the request scope, so a
/// change is seen by the next evaluation (US2 scenario 2).
/// </summary>
public sealed class WorkingCalendar(IDbSession session, IOrganizationHierarchy hierarchy, ITenantSettingsReader tenantSettings) : IWorkingCalendar
{
    private readonly Dictionary<Guid, WorkingCalendarSnapshot> _snapshots = [];
    private TimeZoneInfo? _zone;

    public async Task<WorkingCalendarSnapshot> ForUnitAsync(Guid orgUnitId, CancellationToken ct = default)
    {
        var ancestors = await hierarchy.GetAncestorsAndSelfAsync(orgUnitId, ct);
        if (ancestors.Count > 0)
        {
            var assigned = (await session.QueryAsync(
                    "select org_unit_id, calendar_id from hr.calendar_assignment where org_unit_id = any(@units)",
                    new { units = ancestors.ToArray() }, r => (Unit: r.GetGuid(0), Calendar: r.GetGuid(1)), ct))
                .ToDictionary(a => a.Unit, a => a.Calendar);
            // Ancestors come nearest first, starting with the unit itself.
            foreach (var unit in ancestors)
            {
                if (assigned.TryGetValue(unit, out var calendarId))
                {
                    return await ByIdAsync(calendarId, ct);
                }
            }
        }
        return await TenantDefaultAsync(ct);
    }

    public async Task<WorkingCalendarSnapshot> ForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var memberships = await hierarchy.GetActiveMembershipsAsync(userId, ct);
        return memberships.FirstOrDefault(m => m.IsPrimary) is { } primary
            ? await ForUnitAsync(primary.OrgUnitId, ct)
            : await TenantDefaultAsync(ct);
    }

    public async Task<WorkingCalendarSnapshot> TenantDefaultAsync(CancellationToken ct = default)
    {
        var id = await session.ScalarAsync<Guid?>("select id from hr.working_calendar where is_tenant_default", null, ct);
        return id is { } d
            ? await ByIdAsync(d, ct)
            : new WorkingCalendarSnapshot(null, 0, CalendarService.DefaultWeek.Select(ToDayOfWeek), [], await ZoneAsync(ct));
    }

    public async Task<WorkingCalendarSnapshot> ByIdAsync(Guid calendarId, CancellationToken ct)
    {
        if (_snapshots.TryGetValue(calendarId, out var cached))
        {
            return cached;
        }
        var row = await session.QuerySingleOrDefaultAsync(
            "select week_days, version from hr.working_calendar where id = @calendarId",
            new { calendarId }, r => new CalendarRow(r.GetFieldValue<short[]>(0), r.GetInt32(1)), ct)
            ?? throw new InvalidOperationException($"Calendar {calendarId} does not exist in this tenant.");
        var holidays = await session.QueryAsync(
            "select date, half_day from hr.calendar_holiday where calendar_id = @calendarId",
            new { calendarId }, r => new HolidayDay(DateOnly.FromDateTime(r.GetDateTime(0)), r.GetBoolean(1)), ct);
        var periods = await session.QueryAsync(
            "select start_date, end_date, hours from hr.calendar_period where calendar_id = @calendarId",
            new { calendarId }, r => new ReducedHoursPeriod(DateOnly.FromDateTime(r.GetDateTime(0)), DateOnly.FromDateTime(r.GetDateTime(1)), r.GetDecimal(2)), ct);
        var snapshot = new WorkingCalendarSnapshot(calendarId, row.Version, row.Week.Select(d => ToDayOfWeek(d)), holidays, await ZoneAsync(ct), periods);
        _snapshots[calendarId] = snapshot;
        return snapshot;
    }

    private sealed record CalendarRow(short[] Week, int Version);

    /// <summary>ISO day numbers, 1 for Monday to 7 for Sunday.</summary>
    public static DayOfWeek ToDayOfWeek(int iso) => (DayOfWeek)(iso % 7);

    private async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct)
    {
        if (_zone is null)
        {
            var id = (await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone;
            try
            {
                _zone = TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                _zone = TimeZoneInfo.Utc;
            }
        }
        return _zone;
    }
}
