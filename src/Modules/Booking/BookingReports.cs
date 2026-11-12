using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Booking;

/// <summary>One row of a utilization report: counts and hours only, never who (FR-014).</summary>
public sealed record UsageRow(
    string Key,
    IReadOnlyDictionary<string, string>? Names,
    DateOnly? WeekStart,
    int Resources,
    int Bookings,
    int CheckedIn,
    int NoShows,
    int Cancelled,
    double BookedHours,
    double UsedHours,
    double AvailableHours,
    double? Utilization,
    double? NoShowRate);

public sealed record ReportSite(Guid Id, IReadOnlyDictionary<string, string> Names, string TimeZone, string Status);

public sealed record UsageReport(Guid SiteId, IReadOnlyDictionary<string, string> SiteNames, string TimeZone, DateOnly From, DateOnly To, string GroupBy, UsageRow Total,
    IReadOnlyList<UsageRow> Rows);

/// <summary>
/// How space is used (spec 031 US6, FR-014): for a site and a date range, utilization (hours used over hours available)
/// and no-show rates by resource, zone, site or week. Used hours are checked-in hours, or booked hours for resources
/// with check-in off. Available hours are the site's opening hours on working days of its calendar. Only counts are
/// shown; analysts and facilities administrators hold <c>booking.report</c> in the site's scope.
/// </summary>
public sealed class BookingReports(IDbSession session, IAuthorizationService authz, IEntitlementService entitlements, Facilities facilities, IWorkingCalendar calendars)
{
    public const int MaxDays = 92;
    public static readonly IReadOnlySet<string> Groups = new HashSet<string>(StringComparer.Ordinal) { "resource", "zone", "site", "week" };

    private sealed record Line(Guid ResourceId, Guid? ZoneId, Dictionary<string, string> ResourceNames, Dictionary<string, string>? ZoneNames, bool CheckIn, DateOnly Day,
        string Status, string? EndReason, double Hours);

    /// <summary>The sites the viewer may report on, removed ones too, since their past use still counts.</summary>
    public async Task<IReadOnlyList<ReportSite>> SitesAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.BookingReports, ct);
        var rows = await session.QueryAsync($"select {SiteRow.Columns} from booking.site order by created_at, id", null, SiteRow.Read, ct);
        var sites = new List<ReportSite>();
        foreach (var row in rows)
        {
            if ((await authz.CheckAsync(Permissions.BookingReport, Facilities.Scope(row.OrgUnitId), ct)).Allowed)
            {
                sites.Add(new ReportSite(row.Id, row.Names, row.TimeZone, row.Status));
            }
        }
        return sites;
    }

    public async Task<UsageReport> UtilizationAsync(Guid siteId, DateOnly? from, DateOnly? to, string? groupBy, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.BookingReports, ct);
        var site = await facilities.SiteRowAsync(siteId, ct) ?? throw AppException.NotFound();
        if (!(await authz.CheckAsync(Permissions.BookingReport, Facilities.Scope(site.OrgUnitId), ct)).Allowed)
        {
            throw AppException.NotFound();
        }
        var group = groupBy ?? "resource";
        var errors = new ValidationErrors();
        if (!Groups.Contains(group)) errors.Add("groupBy", "validation.booking.group_by");
        if (from is null || to is null || to < from || to.Value.DayNumber - from.Value.DayNumber >= MaxDays) errors.Add("from", "validation.booking.report_range");
        errors.ThrowIfAny();
        var zone = site.Zone;
        var start = Bookings.At(from!.Value, TimeOnly.MinValue, zone);
        var end = Bookings.At(to!.Value.AddDays(1), TimeOnly.MinValue, zone);

        var resources = await session.QueryAsync(
            """
            select r.id, r.zone_id, r.names::text, z.names::text, r.check_in from booking.resource r left join booking.zone z on z.id = r.zone_id
            where r.site_id = @siteId and (r.status = 'active' or r.updated_at >= @start)
            """, new { siteId, start }, r => (Id: r.GetGuid(0), Zone: r.Get<Guid?>("zone_id"), Names: Names.Read(r, 2), ZoneNames: r.IsDBNull(3) ? null : Names.Read(r, 3),
                CheckIn: r.GetBoolean(4)), ct);
        var byId = resources.ToDictionary(r => r.Id);
        var ids = resources.Select(r => r.Id).ToArray();
        // Bookings that had their slot: cancelled, rejected and expired ones count only as cancelled.
        var rows = ids.Length == 0 ? [] : await session.QueryAsync(
            """
            select resource_id, starts_at, status, end_reason,
                   extract(epoch from (least(ends_at, @end) - greatest(starts_at, @start))) / 3600.0
            from booking.booking
            where resource_id = any(@ids) and starts_at < @end and ends_at > @start
            """, new { ids, start, end },
            r => (Resource: r.GetGuid(0), Starts: r.GetFieldValue<DateTimeOffset>(1), Status: r.GetString(2), EndReason: r.Get<string>("end_reason"), Hours: r.GetDouble(4)), ct);
        var lines = rows.Select(x =>
        {
            var res = byId[x.Resource];
            var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(x.Starts, zone).DateTime);
            return new Line(x.Resource, res.Zone, res.Names, res.ZoneNames, res.CheckIn, day, x.Status, x.EndReason, x.Hours);
        }).ToList();

        // Available hours per resource per working day.
        var calendar = site.OrgUnitId is { } unit ? await calendars.ForUnitAsync(unit, ct) : await calendars.TenantDefaultAsync(ct);
        var openHours = (site.ClosesAt - site.OpensAt).TotalHours;
        var workingDays = new List<DateOnly>();
        for (var d = from.Value; d <= to.Value; d = d.AddDays(1))
        {
            if (calendar.IsWorkingDay(d))
            {
                workingDays.Add(d);
            }
        }

        static DateOnly WeekOf(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));

        UsageRow Row(string key, IReadOnlyDictionary<string, string>? names, DateOnly? week, IReadOnlyCollection<Guid> resourceIds, IEnumerable<Line> mine, int days)
        {
            var list = mine.ToList();
            bool Held(Line l) => l.Status is BookingStatuses.Confirmed or BookingStatuses.CheckedIn || l.EndReason == "no_show";
            var checkedIn = list.Count(l => l.Status == BookingStatuses.CheckedIn);
            var noShows = list.Count(l => l.EndReason == "no_show");
            var used = list.Where(l => l.Status == BookingStatuses.CheckedIn || (!l.CheckIn && l.Status == BookingStatuses.Confirmed)).Sum(l => l.Hours);
            var available = resourceIds.Count * days * openHours;
            return new UsageRow(key, names, week, resourceIds.Count, list.Count(Held), checkedIn, noShows, list.Count(l => !Held(l)),
                Math.Round(list.Where(Held).Sum(l => l.Hours), 2), Math.Round(used, 2), Math.Round(available, 2),
                available > 0 ? Math.Round(used / available, 4) : null, checkedIn + noShows > 0 ? Math.Round((double)noShows / (checkedIn + noShows), 4) : null);
        }

        var all = resources.Select(r => r.Id).ToList();
        var total = Row("site", site.Names, null, all, lines, workingDays.Count);
        var result = group switch
        {
            "resource" => resources.Select(r => Row(r.Id.ToString(), r.Names, null, [r.Id], lines.Where(l => l.ResourceId == r.Id), workingDays.Count))
                .OrderBy(r => r.Key, StringComparer.Ordinal).ToList(),
            "zone" => resources.GroupBy(r => r.Zone).Select(g => Row(g.Key?.ToString() ?? "none", g.First().ZoneNames, null, g.Select(r => r.Id).ToList(),
                lines.Where(l => l.ZoneId == g.Key), workingDays.Count)).ToList(),
            "week" => workingDays.Concat(lines.Select(l => l.Day)).Select(WeekOf).Distinct().Order().Select(w => Row(w.ToString("yyyy-MM-dd"), null, w, all,
                lines.Where(l => WeekOf(l.Day) == w), workingDays.Count(d => WeekOf(d) == w))).ToList(),
            _ => [total],
        };
        return new UsageReport(site.Id, site.Names, site.TimeZone, from.Value, to.Value, group, total, result);
    }
}
