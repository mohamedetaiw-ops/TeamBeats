using TeamBeats.Audit.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Booking;

public sealed record ClosureRequest(DateOnly? Date, string? Reason);

public sealed record ClosureView(Guid SiteId, DateOnly Date, string Reason, DateTimeOffset ClosedAt, int Cancelled);

/// <summary>
/// A site closed for a day (spec 031 section 4 edge case, FR-015). A facilities administrator closes a site on a date,
/// for an emergency or anything its calendar does not know: that day's bookings are cancelled and the holders are told
/// why, and nobody can book the site that day until it is opened again. A holiday added to the site's working calendar
/// (spec 022) cancels that day's bookings of resources that keep to working days, with notice.
/// </summary>
public sealed class BookingClosures(
    IDbSession session,
    ITenantContext context,
    Facilities facilities,
    BookingRules rules,
    BookingCancellations cancellations,
    IWorkingCalendar calendars,
    IAuditWriter audit,
    IClock clock)
{
    public const string ClosedTemplate = "booking.site_closed";
    public const string HolidayTemplate = "booking.holiday";
    public const int MaxReasonLength = 500;
    public const int MaxDaysAhead = 365;

    private static DateOnly Today(SiteRow site, DateTimeOffset now) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, site.Zone).DateTime);

    /// <summary>The days ahead on which the site is closed, for its administrators.</summary>
    public async Task<IReadOnlyList<ClosureView>> ListAsync(Guid siteId, CancellationToken ct)
    {
        var site = await facilities.ManagedSiteAsync(siteId, ct);
        var today = Today(site, clock.UtcNow);
        return await session.QueryAsync(
            "select site_id, day, reason, created_at from booking.site_closure where site_id = @siteId and day >= @today order by day",
            new { siteId, today }, r => new ClosureView(r.GetGuid(0), r.GetFieldValue<DateOnly>(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), 0), ct);
    }

    public async Task<ClosureView> CloseAsync(Guid siteId, ClosureRequest request, CancellationToken ct)
    {
        var site = await facilities.ManagedSiteAsync(siteId, ct);
        if (site.Status != "active")
        {
            throw AppException.NotFound();
        }
        var now = clock.UtcNow;
        var today = Today(site, now);
        var reason = request.Reason?.Trim() ?? "";
        var errors = new ValidationErrors();
        if (request.Date is not { } date || date < today || date.DayNumber - today.DayNumber > MaxDaysAhead) errors.Add("date", "validation.booking.closure_date");
        if (reason.Length is 0 or > MaxReasonLength) errors.Add("reason", "validation.booking.closure_reason");
        errors.ThrowIfAny();
        var day = request.Date!.Value;
        var added = await session.ExecAsync(
            """
            insert into booking.site_closure (tenant_id, site_id, day, reason, created_by, created_at)
            values (platform.current_tenant_id(), @siteId, @day, @reason, @by, @now)
            on conflict (tenant_id, site_id, day) do nothing
            """, new { siteId, day, reason, by = context.RequireUser(), now }, ct);
        if (added == 0)
        {
            throw AppException.Conflict("conflict.booking.already_closed");
        }
        var cancelled = await cancellations.CancelFutureAsync(
            "resource_id in (select id from booking.resource where site_id = @key) and starts_at >= @from and starts_at < @to", siteId, "site_closed", ct,
            adminReason: reason, from: Bookings.At(day, TimeOnly.MinValue, site.Zone), to: Bookings.At(day.AddDays(1), TimeOnly.MinValue, site.Zone), template: ClosedTemplate);
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingSiteClosed, Facilities.SiteType, siteId.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { date = day, reason, cancelled }), ct);
        return new ClosureView(siteId, day, reason, now, cancelled);
    }

    /// <summary>Opens a closed day again. Bookings cancelled when it closed stay cancelled.</summary>
    public async Task ReopenAsync(Guid siteId, DateOnly date, CancellationToken ct)
    {
        var site = await facilities.ManagedSiteAsync(siteId, ct);
        if (await session.ExecAsync("delete from booking.site_closure where site_id = @siteId and day = @date", new { siteId, date }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.BookingSiteReopened, Facilities.SiteType, siteId.ToString(), TargetOrgUnitId: site.OrgUnitId,
            Changes: new { date }), ct);
    }

    /// <summary>
    /// A calendar changed (spec 022): on each changed date that is now a holiday or a non-working day for a site, the
    /// bookings there of resources that keep to working days are cancelled and their holders told. Returns how many.
    /// </summary>
    internal async Task<int> CalendarChangedAsync(WorkingCalendarChanged change, CancellationToken ct)
    {
        if (change.AffectedDates.Count == 0)
        {
            return 0;
        }
        var now = clock.UtcNow;
        var sites = await session.QueryAsync($"select {SiteRow.Columns} from booking.site where status = 'active' order by created_at, id", null, SiteRow.Read, ct);
        if (sites.Count == 0)
        {
            return 0;
        }
        var resolve = await rules.ResolverAsync(ct);
        var total = 0;
        foreach (var site in sites)
        {
            var today = Today(site, now);
            var dates = change.AffectedDates.Where(d => d >= today).Distinct().Order().ToList();
            if (dates.Count == 0)
            {
                continue;
            }
            var calendar = site.OrgUnitId is { } unit ? await calendars.ForUnitAsync(unit, ct) : await calendars.TenantDefaultAsync(ct);
            var closedDays = dates.Where(d => !calendar.IsWorkingDay(d)).ToList();
            if (closedDays.Count == 0)
            {
                continue;
            }
            var resources = await session.QueryAsync($"select {ResourceRow.Columns} from booking.resource where site_id = @siteId and status = 'active'",
                new { siteId = site.Id }, ResourceRow.Read, ct);
            var ids = resources.Where(r => resolve(r).WorkingHours).Select(r => r.Id).ToArray();
            if (ids.Length == 0)
            {
                continue;
            }
            foreach (var day in closedDays)
            {
                total += await cancellations.CancelFutureAsync("resource_id = any(@ids) and starts_at >= @from and starts_at < @to", site.Id, "holiday", ct,
                    ids: ids, from: Bookings.At(day, TimeOnly.MinValue, site.Zone), to: Bookings.At(day.AddDays(1), TimeOnly.MinValue, site.Zone), template: HolidayTemplate);
            }
        }
        return total;
    }
}

/// <summary>Cancels bookings on days that became holidays (spec 031 section 12: consumes holiday calendars from 022).</summary>
public sealed class BookingHolidayHandler(BookingClosures closures) : IMessageHandler<WorkingCalendarChanged>
{
    public Task HandleAsync(WorkingCalendarChanged message, CancellationToken ct) => closures.CalendarChangedAsync(message, ct);
}
