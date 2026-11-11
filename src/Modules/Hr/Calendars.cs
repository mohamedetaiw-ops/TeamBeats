using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr;

public sealed record HolidayView(DateOnly Date, IReadOnlyDictionary<string, string> Name, bool HalfDay);

public sealed record PeriodView(Guid Id, IReadOnlyDictionary<string, string> Name, DateOnly Start, DateOnly End, decimal Hours);

public sealed record CalendarView(
    Guid Id,
    IReadOnlyDictionary<string, string> Name,
    string? Country,
    IReadOnlyList<int> WeekDays,
    Guid? OwnerOrgUnitId,
    bool IsTenantDefault,
    int Version,
    IReadOnlyList<Guid> AssignedUnits,
    bool CanEdit,
    IReadOnlyList<HolidayView>? Holidays,
    IReadOnlyList<PeriodView>? Periods = null)
{
    public string ETag => CalendarService.ETag(Version);
}

public sealed record CalendarRequest(IReadOnlyDictionary<string, string>? Name, string? Country, IReadOnlyList<int>? WeekDays, Guid? OwnerOrgUnitId);

public sealed record HolidayInput(DateOnly Date, IReadOnlyDictionary<string, string>? Name, bool HalfDay);

public sealed record HolidaysRequest(int Year, IReadOnlyList<HolidayInput>? Holidays);

public sealed record PeriodInput(IReadOnlyDictionary<string, string>? Name, DateOnly Start, DateOnly End, decimal Hours);

public sealed record PeriodsRequest(IReadOnlyList<PeriodInput>? Periods);

public sealed record AssignmentRequest(Guid? CalendarId);

public sealed record AssignmentView(Guid OrgUnitId, Guid CalendarId);

public sealed record PreviewDay(DateOnly Date, decimal Fraction, decimal? ReducedHours = null);

public sealed record WorkingDaysPreview(Guid? CalendarId, DateOnly From, DateOnly To, decimal WorkingDays, IReadOnlyList<PreviewDay> Days);

/// <summary>
/// Working calendars (spec 022 US2, FR-007). A calendar owned by an org unit is managed by holders of
/// <c>hr.calendar_manage</c> on that unit; a calendar without an owner, and the tenant default, at tenant scope. A
/// calendar applies to the subtree of each unit it is assigned to; the nearest assigned ancestor wins.
/// </summary>
public sealed partial class CalendarService(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IAuditWriter audit,
    IOutbox outbox,
    IOrganizationHierarchy hierarchy)
{
    public const int MaxHolidaysPerYear = 100;
    public const int MaxPreviewDays = 366;
    public const int MaxPeriods = 50;
    public const int MaxPeriodDays = 100;
    public static readonly int[] DefaultWeek = [1, 2, 3, 4, 5];

    public static string ETag(int version) => $"\"v{version}\"";

    private sealed record Row(Guid Id, Dictionary<string, string> Name, string? Country, int[] WeekDays, Guid? Owner, bool IsDefault, int Version);

    public async Task<IReadOnlyList<CalendarView>> ListAsync(CancellationToken ct)
    {
        var coverage = await RequireCoverageAsync(ct);
        await EnsureDefaultAsync(ct);
        var rows = await ReadAsync(null, ct);
        var assignments = await AssignmentsAsync(ct);
        return rows.Where(r => r.Owner is null || coverage.Covers(r.Owner))
            .OrderByDescending(r => r.IsDefault).ThenBy(r => r.Name.Values.FirstOrDefault(), StringComparer.CurrentCulture)
            .Select(r => View(r, coverage, assignments, null)).ToList();
    }

    public async Task<CalendarView> GetAsync(Guid id, CancellationToken ct)
    {
        var coverage = await RequireCoverageAsync(ct);
        var row = await VisibleAsync(id, coverage, ct);
        return View(row, coverage, await AssignmentsAsync(ct), await HolidaysAsync(id, ct)) with { Periods = await PeriodsAsync(id, ct) };
    }

    public async Task<CalendarView> CreateAsync(CalendarRequest request, CancellationToken ct)
    {
        await DemandManageAsync(request.OwnerOrgUnitId, ct);
        if (request.OwnerOrgUnitId is { } owner && (await hierarchy.GetAncestorsAndSelfAsync(owner, ct)).Count == 0)
        {
            throw AppException.Validation("ownerOrgUnitId", "validation.calendar.owner_unknown");
        }
        var (name, country, week) = Validate(request);
        await EnsureDefaultAsync(ct);
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into hr.working_calendar (tenant_id, id, name, country, week_days, owner_org_unit_id, created_at, updated_at, updated_by)
            values (platform.current_tenant_id(), @id, @name, @country, @week, @owner, @now, @now, @actor)
            """,
            new { id, name = Jsonb.From(name), country, week, owner = request.OwnerOrgUnitId, now, actor = context.ActorId }, ct);
        await ChangedAsync(id, "created", [], [], new { name, country, weekDays = week, owner = request.OwnerOrgUnitId }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<CalendarView> UpdateAsync(Guid id, string? ifMatch, CalendarRequest request, CancellationToken ct)
    {
        var coverage = await RequireCoverageAsync(ct);
        var row = await VisibleAsync(id, coverage, ct);
        await DemandManageAsync(row.Owner, ct);
        RequireVersion(row, ifMatch);
        var (name, country, week) = Validate(request, allowEmptyName: row.IsDefault);
        await session.ExecAsync(
            """
            update hr.working_calendar set name = @name, country = @country, week_days = @week, version = version + 1, updated_at = @now, updated_by = @actor
            where id = @id
            """,
            new { id, name = Jsonb.From(name), country, week, now = clock.UtcNow, actor = context.ActorId }, ct);
        var weekChanged = !week.Order().SequenceEqual(row.WeekDays.Order());
        await ChangedAsync(id, "updated", [], weekChanged ? await UnitsOfAsync(id, row.IsDefault, ct) : [],
            new { name, country, weekDays = week, previousWeekDays = row.WeekDays }, ct);
        return await GetAsync(id, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var coverage = await RequireCoverageAsync(ct);
        var row = await VisibleAsync(id, coverage, ct);
        await DemandManageAsync(row.Owner, ct);
        if (row.IsDefault)
        {
            throw AppException.Conflict("conflict.calendar.default");
        }
        if (await session.ScalarAsync<bool>("select exists (select 1 from hr.calendar_assignment where calendar_id = @id)", new { id }, ct))
        {
            throw AppException.Conflict("conflict.calendar.in_use");
        }
        await session.ExecAsync("delete from hr.working_calendar where id = @id", new { id }, ct);
        await ChangedAsync(id, "deleted", [], [], new { name = row.Name }, ct);
    }

    /// <summary>Replaces the holidays of one year (US2 scenarios 1 and 2). Returns the calendar with its holidays.</summary>
    public async Task<CalendarView> SetHolidaysAsync(Guid id, string? ifMatch, HolidaysRequest request, CancellationToken ct)
    {
        var coverage = await RequireCoverageAsync(ct);
        var row = await VisibleAsync(id, coverage, ct);
        await DemandManageAsync(row.Owner, ct);
        RequireVersion(row, ifMatch);

        var errors = new ValidationErrors();
        if (request.Year is < 2000 or > 2100)
        {
            errors.Add("year", "validation.calendar.year");
        }
        var holidays = request.Holidays ?? [];
        if (holidays.Count > MaxHolidaysPerYear)
        {
            errors.Add("holidays", "validation.calendar.too_many_holidays");
        }
        if (holidays.Any(h => h.Date.Year != request.Year) || holidays.GroupBy(h => h.Date).Any(g => g.Count() > 1))
        {
            errors.Add("holidays", "validation.calendar.holiday_date");
        }
        var named = holidays.Select(h => (Holiday: h, Name: CleanName(h.Name))).ToList();
        if (named.Any(n => n.Name is null))
        {
            errors.Add("holidays", "validation.calendar.holiday_name");
        }
        errors.ThrowIfAny();

        var before = (await HolidaysAsync(id, ct)).Where(h => h.Date.Year == request.Year).ToDictionary(h => h.Date);
        await session.ExecAsync(
            "delete from hr.calendar_holiday where calendar_id = @id and date >= @from and date <= @to",
            new { id, from = new DateOnly(request.Year, 1, 1), to = new DateOnly(request.Year, 12, 31) }, ct);
        foreach (var (h, name) in named)
        {
            await session.ExecAsync(
                "insert into hr.calendar_holiday (tenant_id, calendar_id, date, name, half_day) values (platform.current_tenant_id(), @id, @date, @name, @half)",
                new { id, date = h.Date, name = Jsonb.From(name!), half = h.HalfDay }, ct);
        }
        await session.ExecAsync(
            "update hr.working_calendar set version = version + 1, updated_at = @now, updated_by = @actor where id = @id",
            new { id, now = clock.UtcNow, actor = context.ActorId }, ct);

        // Dates whose working status changed: added, removed, or switched between half and full day.
        var after = named.ToDictionary(n => n.Holiday.Date, n => n.Holiday.HalfDay);
        var affected = before.Keys.Union(after.Keys)
            .Where(d => !before.TryGetValue(d, out var b) || !after.TryGetValue(d, out var a) || b.HalfDay != a)
            .Order().ToList();
        await ChangedAsync(id, "holidays_replaced", affected, affected.Count > 0 ? await UnitsOfAsync(id, row.IsDefault, ct) : [],
            new { year = request.Year, count = after.Count, affectedDates = affected }, ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Replaces the reduced hours periods of a calendar (US2 scenario 3, for example Ramadan). Working days stay the
    /// same, so the change moves no due date and recounts no leave; attendance reads the hours.
    /// </summary>
    public async Task<CalendarView> SetPeriodsAsync(Guid id, string? ifMatch, PeriodsRequest request, CancellationToken ct)
    {
        var coverage = await RequireCoverageAsync(ct);
        var row = await VisibleAsync(id, coverage, ct);
        await DemandManageAsync(row.Owner, ct);
        RequireVersion(row, ifMatch);

        var errors = new ValidationErrors();
        var periods = request.Periods ?? [];
        if (periods.Count > MaxPeriods)
        {
            errors.Add("periods", "validation.calendar.too_many_periods");
        }
        var named = periods.Select(p => (Period: p, Name: CleanName(p.Name))).ToList();
        if (named.Any(n => n.Name is null))
        {
            errors.Add("periods", "validation.calendar.period_name");
        }
        if (periods.Any(p => p.End < p.Start || p.End.DayNumber - p.Start.DayNumber >= MaxPeriodDays || p.Start.Year is < 2000 or > 2100))
        {
            errors.Add("periods", "validation.calendar.period_dates");
        }
        if (periods.Any(p => p.Hours is <= 0 or >= 24 || decimal.Round(p.Hours * 4) != p.Hours * 4))
        {
            errors.Add("periods", "validation.calendar.period_hours");
        }
        var ordered = periods.OrderBy(p => p.Start).ToList();
        if (ordered.Zip(ordered.Skip(1)).Any(pair => pair.Second.Start <= pair.First.End))
        {
            errors.Add("periods", "validation.calendar.period_overlap");
        }
        errors.ThrowIfAny();

        await session.ExecAsync("delete from hr.calendar_period where calendar_id = @id", new { id }, ct);
        foreach (var (p, name) in named)
        {
            await session.ExecAsync(
                """
                insert into hr.calendar_period (tenant_id, id, calendar_id, name, start_date, end_date, hours)
                values (platform.current_tenant_id(), @pid, @id, @name, @start, @end, @hours)
                """, new { pid = Ids.New(), id, name = Jsonb.From(name!), start = p.Start, end = p.End, hours = p.Hours }, ct);
        }
        await session.ExecAsync(
            "update hr.working_calendar set version = version + 1, updated_at = @now, updated_by = @actor where id = @id",
            new { id, now = clock.UtcNow, actor = context.ActorId }, ct);
        await ChangedAsync(id, "periods_replaced", [], [],
            new { periods = ordered.Select(p => new { start = p.Start, end = p.End, hours = p.Hours }) }, ct);
        return await GetAsync(id, ct);
    }

    public async Task<IReadOnlyList<AssignmentView>> ListAssignmentsAsync(CancellationToken ct)
    {
        var coverage = await RequireCoverageAsync(ct);
        return (await AssignmentsAsync(ct)).Where(a => coverage.Covers(a.OrgUnitId)).ToList();
    }

    /// <summary>Applies a calendar to a unit's subtree, or removes the unit's own assignment when the calendar is null.</summary>
    public async Task<AssignmentView?> AssignAsync(Guid orgUnitId, AssignmentRequest request, CancellationToken ct)
    {
        var ancestors = await hierarchy.GetAncestorsAndSelfAsync(orgUnitId, ct);
        if (ancestors.Count == 0)
        {
            throw AppException.NotFound(); // Not a unit of this tenant.
        }
        await authz.DemandAsync(Permissions.HrCalendarManage, AuthzTarget.OrgUnit(orgUnitId), ct);
        var previous = await session.ScalarAsync<Guid?>("select calendar_id from hr.calendar_assignment where org_unit_id = @orgUnitId", new { orgUnitId }, ct);

        if (request.CalendarId is not { } calendarId)
        {
            if (previous is not null)
            {
                await session.ExecAsync("delete from hr.calendar_assignment where org_unit_id = @orgUnitId", new { orgUnitId }, ct);
                await ChangedAsync(previous, "unassigned", [], [orgUnitId], new { orgUnitId, calendarId = previous }, ct);
            }
            return null;
        }

        var coverage = await RequireCoverageAsync(ct);
        var row = await VisibleAsync(calendarId, coverage, ct, notFoundAs: "calendarId");
        if (row.Owner is { } owner && !ancestors.Contains(owner))
        {
            throw AppException.Validation("calendarId", "validation.calendar.not_applicable");
        }
        if (previous != calendarId)
        {
            await session.ExecAsync(
                """
                insert into hr.calendar_assignment (tenant_id, org_unit_id, calendar_id, assigned_at, assigned_by)
                values (platform.current_tenant_id(), @orgUnitId, @calendarId, @now, @actor)
                on conflict (tenant_id, org_unit_id) do update set calendar_id = excluded.calendar_id, assigned_at = excluded.assigned_at, assigned_by = excluded.assigned_by
                """,
                new { orgUnitId, calendarId, now = clock.UtcNow, actor = context.ActorId }, ct);
            await ChangedAsync(calendarId, "assigned", [], [orgUnitId], new { orgUnitId, calendarId, previousCalendarId = previous }, ct);
        }
        return new AssignmentView(orgUnitId, calendarId);
    }

    /// <summary>Working days between two dates for a unit (its resolved calendar) or for one calendar.</summary>
    public async Task<WorkingDaysPreview> PreviewAsync(Guid? orgUnitId, Guid? calendarId, DateOnly from, DateOnly to, WorkingCalendar calendars, CancellationToken ct)
    {
        var coverage = await RequireCoverageAsync(ct);
        if (to < from || to.DayNumber - from.DayNumber >= MaxPreviewDays)
        {
            throw AppException.Validation("to", "validation.calendar.range");
        }
        WorkingCalendarSnapshot snapshot;
        if (calendarId is { } c)
        {
            await VisibleAsync(c, coverage, ct);
            snapshot = await calendars.ByIdAsync(c, ct);
        }
        else if (orgUnitId is { } u)
        {
            if ((await hierarchy.GetAncestorsAndSelfAsync(u, ct)).Count == 0)
            {
                throw AppException.NotFound();
            }
            snapshot = await calendars.ForUnitAsync(u, ct);
        }
        else
        {
            snapshot = await calendars.TenantDefaultAsync(ct);
        }
        var days = new List<PreviewDay>();
        for (var d = from; d <= to; d = d.AddDays(1))
        {
            days.Add(new PreviewDay(d, snapshot.WorkingFraction(d), snapshot.ReducedHours(d)));
        }
        return new WorkingDaysPreview(snapshot.CalendarId, from, to, snapshot.CountWorkingDays(from, to), days);
    }

    /// <summary>The tenant default exists from first use: Monday to Friday with no holidays (US2 scenario 4).</summary>
    private Task EnsureDefaultAsync(CancellationToken ct) =>
        session.ExecAsync(
            """
            insert into hr.working_calendar (tenant_id, id, name, week_days, is_tenant_default, created_at, updated_at)
            values (platform.current_tenant_id(), @id, '{}'::jsonb, @week, true, @now, @now)
            on conflict (tenant_id) where is_tenant_default do nothing
            """,
            new { id = Ids.New(), week = DefaultWeek, now = clock.UtcNow }, ct);

    private async Task<OrgCoverage> RequireCoverageAsync(CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.HrCalendarManage, ct);
        return coverage.Any ? coverage : throw AppException.Denied();
    }

    private async Task DemandManageAsync(Guid? owner, CancellationToken ct) =>
        await authz.DemandAsync(Permissions.HrCalendarManage, owner is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct);

    private async Task<Row> VisibleAsync(Guid id, OrgCoverage coverage, CancellationToken ct, string? notFoundAs = null)
    {
        var row = (await ReadAsync(id, ct)).SingleOrDefault();
        if (row is null || !(row.Owner is null || coverage.Covers(row.Owner)))
        {
            throw notFoundAs is null ? AppException.NotFound() : AppException.Validation(notFoundAs, "validation.calendar.unknown");
        }
        return row;
    }

    private static void RequireVersion(Row row, string? ifMatch)
    {
        if (ifMatch != ETag(row.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.calendar.changed", null,
                new Dictionary<string, object?> { ["etag"] = ETag(row.Version) });
        }
    }

    private async Task ChangedAsync(Guid? calendarId, string operation, IReadOnlyList<DateOnly> dates, IReadOnlyList<Guid> units, object changes, CancellationToken ct)
    {
        await audit.WriteAsync(new AuditEntry(AuditActions.HrCalendarChanged, "working_calendar", calendarId?.ToString() ?? "default",
            Changes: new { operation, details = changes }), ct);
        await outbox.EnqueueAsync(new WorkingCalendarChanged(calendarId, dates, units), ct);
    }

    /// <summary>Units whose people use the calendar: its assignments, or every unit for the tenant default.</summary>
    private async Task<IReadOnlyList<Guid>> UnitsOfAsync(Guid calendarId, bool isDefault, CancellationToken ct) =>
        isDefault ? [] : await session.QueryAsync("select org_unit_id from hr.calendar_assignment where calendar_id = @calendarId",
            new { calendarId }, r => r.GetGuid(0), ct);

    private Task<List<Row>> ReadAsync(Guid? id, CancellationToken ct) =>
        session.QueryAsync(
            """
            select id, name::text, country, week_days, owner_org_unit_id, is_tenant_default, version
            from hr.working_calendar where (@id::uuid is null or id = @id)
            """,
            new { id },
            r => new Row(r.GetGuid(0), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1)) ?? [], r.Get<string>("country"),
                r.GetFieldValue<short[]>(3).Select(d => (int)d).ToArray(), r.Get<Guid?>("owner_org_unit_id"), r.GetBoolean(5), r.GetInt32(6)),
            ct);

    private Task<List<HolidayView>> HolidaysAsync(Guid id, CancellationToken ct) =>
        session.QueryAsync(
            "select date, name::text, half_day from hr.calendar_holiday where calendar_id = @id order by date",
            new { id },
            r => new HolidayView(DateOnly.FromDateTime(r.GetDateTime(0)),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1)) ?? [], r.GetBoolean(2)),
            ct);

    private Task<List<PeriodView>> PeriodsAsync(Guid id, CancellationToken ct) =>
        session.QueryAsync(
            "select id, name::text, start_date, end_date, hours from hr.calendar_period where calendar_id = @id order by start_date",
            new { id },
            r => new PeriodView(r.GetGuid(0), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(1)) ?? [],
                DateOnly.FromDateTime(r.GetDateTime(2)), DateOnly.FromDateTime(r.GetDateTime(3)), r.GetDecimal(4)),
            ct);

    private Task<List<AssignmentView>> AssignmentsAsync(CancellationToken ct) =>
        session.QueryAsync("select org_unit_id, calendar_id from hr.calendar_assignment", null, r => new AssignmentView(r.GetGuid(0), r.GetGuid(1)), ct);

    private static CalendarView View(Row r, OrgCoverage coverage, IReadOnlyList<AssignmentView> assignments, IReadOnlyList<HolidayView>? holidays) =>
        new(r.Id, r.Name, r.Country, r.WeekDays.Order().ToList(), r.Owner, r.IsDefault, r.Version,
            assignments.Where(a => a.CalendarId == r.Id).Select(a => a.OrgUnitId).ToList(),
            r.Owner is null ? coverage.WholeTenant : coverage.Covers(r.Owner), holidays);

    private static (Dictionary<string, string> Name, string? Country, int[] Week) Validate(CalendarRequest request, bool allowEmptyName = false)
    {
        var errors = new ValidationErrors();
        // The tenant default is labelled by the platform until someone names it.
        var name = allowEmptyName && (request.Name is null || request.Name.Values.All(string.IsNullOrWhiteSpace)) ? [] : CleanName(request.Name);
        if (name is null)
        {
            errors.Add("name", "validation.calendar.name");
        }
        var country = string.IsNullOrWhiteSpace(request.Country) ? null : request.Country.Trim().ToUpperInvariant();
        if (country is not null && !CountryPattern().IsMatch(country))
        {
            errors.Add("country", "validation.calendar.country");
        }
        var week = (request.WeekDays ?? []).Distinct().Order().ToArray();
        if (week.Length == 0 || week.Any(d => d is < 1 or > 7))
        {
            errors.Add("weekDays", "validation.calendar.week_days");
        }
        errors.ThrowIfAny();
        return (name!, country, week);
    }

    private static Dictionary<string, string>? CleanName(IReadOnlyDictionary<string, string>? name)
    {
        var clean = (name ?? new Dictionary<string, string>()).Where(n => !string.IsNullOrWhiteSpace(n.Value))
            .ToDictionary(n => n.Key, n => n.Value.Trim(), StringComparer.Ordinal);
        return clean.Count == 0 || clean.Any(n => !LocaleKey().IsMatch(n.Key) || n.Value.Length > 100 || n.Value.Any(char.IsControl)) ? null : clean;
    }

    [GeneratedRegex("^[A-Z]{2}$")]
    private static partial Regex CountryPattern();

    [GeneratedRegex("^[a-z]{2}$")]
    private static partial Regex LocaleKey();
}
