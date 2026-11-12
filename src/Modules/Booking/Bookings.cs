using System.Globalization;
using System.Text;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Booking.Contracts;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Booking;

/// <summary>
/// Finding and booking resources (spec 031 US2, FR-003 to FR-006, FR-013, FR-017). Every booking passes the same server
/// checks whatever path made it; the database lets only one live booking hold a moment of a resource, so of two
/// parallel attempts exactly one succeeds. Times are in the site's time zone.
/// </summary>
public sealed class Bookings(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    Facilities facilities,
    BookingRules rules,
    BookingCancellations cancellations,
    IWorkingCalendar calendars,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    ITenantSettingsReader tenantSettings,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock)
{
    public const string ConfirmedTemplate = "booking.confirmed";
    public const string SeriesTemplate = "booking.series_confirmed";
    public const int MaxTitleLength = 200;
    public const int MaxCancelReasonLength = 500;

    public static string Link(Guid id) => $"/booking/bookings/{id}";

    /// <summary>A booked span: the local date and times turned into instants in the site's zone.</summary>
    internal sealed record Span(DateOnly Date, DateTimeOffset StartsAt, DateTimeOffset EndsAt, TimeOnly Start, TimeOnly End, bool Overnight);

    internal static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        // A local time skipped by a clock change moves forward to the first valid minute.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    internal static Span SpanOf(DateOnly date, TimeOnly start, TimeOnly end, TimeZoneInfo zone)
    {
        var overnight = end <= start;
        return new Span(date, At(date, start, zone), At(overnight ? date.AddDays(1) : date, end, zone), start, end, overnight);
    }

    private async Task<WorkingCalendarSnapshot> CalendarAsync(SiteRow site, CancellationToken ct) =>
        site.OrgUnitId is { } unit ? await calendars.ForUnitAsync(unit, ct) : await calendars.TenantDefaultAsync(ct);

    /// <summary>Whether the person is in one of the rule's units or below one (section 7: a parent unit includes its children).</summary>
    private async Task<bool> InUnitsAsync(Rule rule, Guid userId, Dictionary<string, IReadOnlySet<Guid>> subtrees, CancellationToken ct)
    {
        if (rule.UnitIds.Length == 0)
        {
            return true;
        }
        var key = string.Join(",", rule.UnitIds.Order());
        if (!subtrees.TryGetValue(key, out var units))
        {
            units = await hierarchy.GetSubtreesAsync(rule.UnitIds, ct);
            subtrees[key] = units;
        }
        var mine = await hierarchy.GetActiveMembershipsAsync(userId, ct);
        return mine.Any(m => units.Contains(m.OrgUnitId));
    }

    /// <summary>The rule checks that need no other booking (FR-003). Each failure is a field and a resource key.</summary>
    private static void CheckSpan(Span span, Rule rule, Settings settings, SiteRow site, WorkingCalendarSnapshot calendar, DateTimeOffset now, ValidationErrors errors,
        bool first = true)
    {
        if (span.Start.Minute % settings.SlotMinutes != 0 || span.End.Minute % settings.SlotMinutes != 0 || span.Start.Second != 0 || span.End.Second != 0)
        {
            errors.Add("start", "validation.booking.slot");
        }
        if (span.Overnight && !rule.Overnight)
        {
            errors.Add("end", "validation.booking.overnight");
        }
        var minutes = (span.EndsAt - span.StartsAt).TotalMinutes;
        if (minutes < rule.MinMinutes)
        {
            errors.Add("end", "validation.booking.too_short");
        }
        if (minutes > rule.MaxMinutes)
        {
            errors.Add("end", "validation.booking.too_long");
        }
        if (first)
        {
            if (span.EndsAt <= now || span.StartsAt < now.AddMinutes(-settings.SlotMinutes))
            {
                errors.Add("date", "validation.booking.past");
            }
            var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, site.Zone).DateTime);
            if (span.Date.DayNumber - today.DayNumber > rule.AdvanceDays)
            {
                errors.Add("date", "validation.booking.too_far");
            }
        }
        if (rule.WorkingHours)
        {
            if (!calendar.IsWorkingDay(span.Date))
            {
                errors.Add("date", "validation.booking.closed_day");
            }
            else if (span.Overnight || span.Start < site.OpensAt || span.End > site.ClosesAt)
            {
                errors.Add("start", "validation.booking.outside_hours");
            }
        }
    }

    private async Task DemandBookAsync(ResourceRow resource, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.BookingBook, AuthzTarget.Tenant, ct);
        await entitlements.DemandAsync(ResourceKinds.Capability(resource.Kind), ct);
    }

    // ---- Search and availability ----

    /// <summary>
    /// Resources at a site that are free for the time and that the viewer may book under their rules (FR-004), filtered
    /// by kind, capacity and attributes. Fixed desks of other people are left out.
    /// </summary>
    public async Task<SearchResult> SearchAsync(Guid siteId, string? kind, DateOnly? date, TimeOnly? start, TimeOnly? end, int? capacity, string? attributes, Guid? zoneId,
        CancellationToken ct)
    {
        await facilities.RequireAnyAsync(ct);
        await authz.DemandAsync(Permissions.BookingBook, AuthzTarget.Tenant, ct);
        var site = await facilities.ActiveSiteAsync(siteId, ct);
        var errors = new ValidationErrors();
        if (date is null) errors.Add("date", "validation.booking.date");
        if (start is null) errors.Add("start", "validation.booking.time");
        if (end is null) errors.Add("end", "validation.booking.time");
        if (kind is not null && !ResourceKinds.All.Contains(kind)) errors.Add("kind", "validation.booking.kind");
        errors.ThrowIfAny();
        var enabled = await facilities.KindsAsync(ct);
        var wanted = (kind is null ? enabled : enabled.Where(k => k == kind)).ToArray();
        var span = SpanOf(date!.Value, start!.Value, end!.Value, site.Zone);
        var wantedAttributes = (attributes ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(a => a.ToLowerInvariant()).ToArray();
        var user = context.RequireUser();
        var candidates = wanted.Length == 0 ? [] : await session.QueryAsync(
            $"""
            select {string.Join(", ", ResourceRow.Columns.Split(", ").Select(c => "r." + c))},
                   f.names::text as floor_names, z.names::text as zone_names
            from booking.resource r
            left join booking.floor f on f.id = r.floor_id
            left join booking.zone z on z.id = r.zone_id
            where r.site_id = @siteId and r.status = 'active' and r.kind = any(@wanted)
              and (@capacity::int is null or r.capacity >= @capacity)
              and r.attributes @> @wantedAttributes
              and (@zoneId::uuid is null or r.zone_id = @zoneId)
              and (r.fixed_user_id is null or r.fixed_user_id = @user)
            order by r.kind, f.sort nulls first, r.capacity nulls first, r.created_at, r.id
            limit 2000
            """, new { siteId, wanted, capacity, wantedAttributes, zoneId, user },
            r => (Row: ResourceRow.Read(r), Floor: r.IsDBNull(15) ? null : Names.Read(r, 15), Zone: r.IsDBNull(16) ? null : Names.Read(r, 16)), ct);
        if (candidates.Count == 0)
        {
            return new SearchResult(site.Id, site.TimeZone, span.StartsAt, span.EndsAt, []);
        }
        var resolve = await rules.ResolverAsync(ct);
        var settings = await rules.SettingsAsync(ct);
        var calendar = await CalendarAsync(site, ct);
        var now = clock.UtcNow;
        var subtrees = new Dictionary<string, IReadOnlySet<Guid>>();
        var bookable = new List<(ResourceRow Row, IReadOnlyDictionary<string, string>? Floor, IReadOnlyDictionary<string, string>? Zone, Rule Rule)>();
        foreach (var (row, floor, zone) in candidates)
        {
            var rule = resolve(row);
            var check = new ValidationErrors();
            CheckSpan(span, rule, settings, site, calendar, now, check);
            if (!check.Any && await InUnitsAsync(rule, user, subtrees, ct))
            {
                bookable.Add((row, floor, zone, rule));
            }
        }
        // What is taken around the time, with the buffer of the rule of the resource asked for.
        var maxBuffer = bookable.Count == 0 ? 0 : bookable.Max(b => b.Rule.BufferMinutes);
        var ids = bookable.Select(b => b.Row.Id).ToArray();
        var taken = ids.Length == 0 ? [] : await session.QueryAsync(
            """
            select resource_id, lower(held), upper(held) from booking.booking
            where resource_id = any(@ids) and status in ('tentative', 'confirmed', 'checked_in') and held && tstzrange(@from, @to, '[)')
            """, new { ids, from = span.StartsAt.AddMinutes(-maxBuffer), to = span.EndsAt.AddMinutes(maxBuffer) },
            r => (Resource: r.GetGuid(0), From: r.GetFieldValue<DateTimeOffset>(1), To: r.GetFieldValue<DateTimeOffset>(2)), ct);
        var busy = taken.ToLookup(t => t.Resource);
        var hits = bookable
            .Where(b => !busy[b.Row.Id].Any(t => t.From < span.EndsAt.AddMinutes(b.Rule.BufferMinutes) && t.To > span.StartsAt))
            .Select(b => new SearchHit(b.Row.Id, b.Row.Kind, b.Row.Names, b.Row.Capacity, b.Row.Attributes, b.Floor, b.Zone, b.Row.NeedsApproval))
            .ToList();
        return new SearchResult(site.Id, site.TimeZone, span.StartsAt, span.EndsAt, hits);
    }

    /// <summary>A resource's taken slots on a day of its site. Colleagues see "booked" without names unless the tenant shows them.</summary>
    public async Task<AvailabilityView> AvailabilityAsync(Guid resourceId, DateOnly date, CancellationToken ct)
    {
        var resource = await facilities.ResourceRowAsync(resourceId, ct) is { Active: true } r ? r : throw AppException.NotFound();
        await DemandBookAsync(resource, ct);
        var site = await facilities.ActiveSiteAsync(resource.SiteId, ct);
        var calendar = await CalendarAsync(site, ct);
        var from = At(date, TimeOnly.MinValue, site.Zone);
        var to = At(date.AddDays(1), TimeOnly.MinValue, site.Zone);
        var rows = await session.QueryAsync(
            """
            select starts_at, ends_at, status, holder_id from booking.booking
            where resource_id = @resourceId and status in ('tentative', 'confirmed', 'checked_in') and starts_at < @to and ends_at > @from
            order by starts_at
            """, new { resourceId, from, to },
            x => (Start: x.GetFieldValue<DateTimeOffset>(0), End: x.GetFieldValue<DateTimeOffset>(1), Status: x.GetString(2), Holder: x.GetGuid(3)), ct);
        var showNames = (await rules.SettingsAsync(ct)).ShowNames || await facilities.ManagesAsync(site, ct);
        var names = showNames ? await users.GetManyAsync(rows.Select(x => x.Holder).Distinct().ToList(), ct) : new Dictionary<Guid, UserInfo>();
        var me = context.RequireUser();
        return new AvailabilityView(resourceId, date, site.TimeZone, site.OpensAt, site.ClosesAt, calendar.IsWorkingDay(date),
            rows.Select(x => new BusySlot(x.Start, x.End, x.Status,
                x.Holder == me || showNames ? (names.TryGetValue(x.Holder, out var u) ? u.DisplayName : null) : null)).ToList());
    }

    // ---- Booking ----

    public async Task<BookResult> BookAsync(BookRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        return await PlaceAsync(request, user, user, ct);
    }

    /// <summary>
    /// Places a booking, or a recurring series, for <paramref name="holder"/>; <paramref name="bookedBy"/> differs for a
    /// booking made on someone's behalf or for a team, and the holder's own rules and limits apply (FR-012).
    /// </summary>
    internal async Task<BookResult> PlaceAsync(BookRequest request, Guid holder, Guid bookedBy, CancellationToken ct)
    {
        await facilities.RequireAnyAsync(ct);
        var resource = request.ResourceId is { } rid && await facilities.ResourceRowAsync(rid, ct) is { Active: true } found ? found : throw AppException.NotFound();
        await DemandBookAsync(resource, ct);
        var site = await facilities.ActiveSiteAsync(resource.SiteId, ct);
        if (resource.FixedUserId is { } owner && owner != holder)
        {
            throw AppException.Denied("authz.denied.booking_fixed");
        }
        var errors = new ValidationErrors();
        if (request.Date is null) errors.Add("date", "validation.booking.date");
        if (request.Start is null) errors.Add("start", "validation.booking.time");
        if (request.End is null) errors.Add("end", "validation.booking.time");
        var title = string.IsNullOrWhiteSpace(request.Title) ? null : request.Title.Trim();
        if (title is { Length: > MaxTitleLength }) errors.Add("title", "validation.booking.title");
        errors.ThrowIfAny();

        var rule = await rules.ForAsync(resource, ct);
        var settings = await rules.SettingsAsync(ct);
        if (!await InUnitsAsync(rule, holder, [], ct))
        {
            throw AppException.Denied("authz.denied.booking_units");
        }
        var calendar = await CalendarAsync(site, ct);
        var now = clock.UtcNow;
        var first = SpanOf(request.Date!.Value, request.Start!.Value, request.End!.Value, site.Zone);
        CheckSpan(first, rule, settings, site, calendar, now, errors);
        errors.ThrowIfAny();

        // The occurrences of a series (FR-006): non-working days of the site's calendar are skipped and listed.
        var spans = new List<Span> { first };
        var skipped = new List<SkippedDate>();
        string? pattern = null;
        if (request.Repeat is { } repeat)
        {
            pattern = repeat.Pattern;
            if (pattern is not ("daily" or "weekly"))
            {
                throw AppException.Validation("repeat.pattern", "validation.booking.repeat");
            }
            if (repeat.Until is not { } until || until <= first.Date || until.DayNumber - first.Date.DayNumber > rule.SeriesDays)
            {
                throw AppException.Validation("repeat.until", "validation.booking.repeat_until");
            }
            var step = pattern == "daily" ? 1 : 7;
            for (var d = first.Date.AddDays(step); d <= until; d = d.AddDays(step))
            {
                if (!calendar.IsWorkingDay(d))
                {
                    // A daily series runs on working days; weekends are not listed, holidays are.
                    if (pattern == "weekly" || calendar.IsHoliday(d))
                    {
                        skipped.Add(new SkippedDate(d, calendar.IsHoliday(d) ? "holiday" : "non_working_day"));
                    }
                    continue;
                }
                var span = SpanOf(d, first.Start, first.End, site.Zone);
                var check = new ValidationErrors();
                CheckSpan(span, rule, settings, site, calendar, now, check, first: false);
                if (check.Any)
                {
                    skipped.Add(new SkippedDate(d, "rules"));
                    continue;
                }
                spans.Add(span);
            }
        }

        // The person's limit on active bookings of this kind (US2 scenario 4); a series counts once.
        var active = await session.ScalarAsync<int>(
            """
            select count(distinct coalesce(b.series_id, b.id))::int from booking.booking b join booking.resource r on r.id = b.resource_id
            where b.holder_id = @holder and r.kind = @kind and b.status in ('tentative', 'confirmed', 'checked_in') and b.ends_at > @now
            """, new { holder, kind = resource.Kind, now }, ct);
        if (active >= rule.MaxActive)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.booking.limit", null, new Dictionary<string, object?> { ["limit"] = rule.MaxActive });
        }

        // Taken dates first, so a series names them all; the constraint still decides a race.
        var takenDates = new List<DateOnly>();
        foreach (var span in spans)
        {
            if (await session.ScalarAsync<bool>(
                    """
                    select exists (select 1 from booking.booking where resource_id = @id and status in ('tentative', 'confirmed', 'checked_in')
                      and held && tstzrange(@from, @to, '[)'))
                    """, new { id = resource.Id, from = span.StartsAt, to = span.EndsAt.AddMinutes(rule.BufferMinutes) }, ct))
            {
                takenDates.Add(span.Date);
            }
        }
        if (takenDates.Count > 0)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.booking.taken", null,
                new Dictionary<string, object?> { ["dates"] = takenDates.Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray() });
        }

        Guid? seriesId = null;
        if (pattern is not null)
        {
            seriesId = Ids.New();
            await session.ExecAsync(
                """
                insert into booking.series (tenant_id, id, resource_id, holder_id, pattern, until, skipped, created_by, created_at)
                values (platform.current_tenant_id(), @seriesId, @resource, @holder, @pattern, @until, @skipped, @bookedBy, @now)
                """, new { seriesId, resource = resource.Id, holder, pattern, until = request.Repeat!.Until!.Value, skipped = Jsonb.From(skipped), bookedBy, now }, ct);
        }
        var ids = new List<Guid>();
        try
        {
            foreach (var span in spans)
            {
                var id = Ids.New();
                await session.ExecAsync(
                    """
                    insert into booking.booking (tenant_id, id, resource_id, holder_id, booked_by, series_id, starts_at, ends_at, held, title, status, created_at)
                    values (platform.current_tenant_id(), @id, @resource, @holder, @bookedBy, @seriesId, @starts, @ends, tstzrange(@starts, @heldUntil, '[)'), @title,
                      'confirmed', @now)
                    """, new { id, resource = resource.Id, holder, bookedBy, seriesId, starts = span.StartsAt, ends = span.EndsAt, heldUntil = span.EndsAt.AddMinutes(rule.BufferMinutes), title, now },
                    ct);
                ids.Add(id);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
        {
            throw AppException.Conflict("conflict.booking.taken");
        }

        foreach (var (id, span) in ids.Zip(spans))
        {
            await outbox.EnqueueAsync(new BookingConfirmed(id, resource.Id, site.Id, resource.Kind, holder, span.StartsAt, span.EndsAt), ct);
        }
        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var parameters = BookingCancellations.Describe(resource.Names, site.TimeZone, first.StartsAt, first.EndsAt, locale);
        parameters["count"] = ids.Count.ToString(CultureInfo.InvariantCulture);
        await notifications.SendAsync(new NotificationRequest([holder], NotificationCategories.Bookings, seriesId is null ? ConfirmedTemplate : SeriesTemplate, Link(ids[0]),
            parameters, DedupeKey: $"booking:{ids[0]:N}:confirmed"), ct);

        var views = await ViewsAsync("b.id = any(@ids)", new { ids = ids.ToArray() }, ct);
        return new BookResult(views, seriesId, skipped);
    }

    // ---- Reading ----

    private async Task<List<BookingView>> ViewsAsync(string where, object args, CancellationToken ct)
    {
        var me = context.RequireUser();
        var rows = await session.QueryAsync(
            $"""
            select {string.Join(", ", BookingRow.Columns.Split(", ").Select(c => "b." + c))}, r.kind, r.names::text as resource_names, s.id as site, s.names::text as site_names,
                   s.time_zone, s.org_unit_id
            from booking.booking b
            join booking.resource r on r.id = b.resource_id
            join booking.site s on s.id = r.site_id
            where {where}
            order by b.starts_at, b.id
            limit 500
            """, args,
            r => (Row: BookingRow.Read(r), Kind: r.GetString(12), Resource: Names.Read(r, 13), Site: r.GetGuid(14), SiteNames: Names.Read(r, 15), Zone: r.GetString(16),
                Unit: r.Get<Guid?>("org_unit_id")), ct);
        var now = clock.UtcNow;
        var views = new List<BookingView>();
        var manages = new Dictionary<Guid, bool>();
        foreach (var x in rows)
        {
            if (!manages.TryGetValue(x.Unit ?? Guid.Empty, out var m))
            {
                m = (await authz.CheckAsync(Permissions.BookingManage, Facilities.Scope(x.Unit), ct)).Allowed;
                manages[x.Unit ?? Guid.Empty] = m;
            }
            var zone = TimeZoneInfo.FindSystemTimeZoneById(x.Zone);
            var start = TimeZoneInfo.ConvertTime(x.Row.StartsAt, zone);
            var end = TimeZoneInfo.ConvertTime(x.Row.EndsAt, zone);
            var own = x.Row.HolderId == me || x.Row.BookedBy == me;
            views.Add(new BookingView(x.Row.Id, x.Row.ResourceId, x.Kind, x.Resource, x.Site, x.SiteNames, x.Zone, x.Row.StartsAt, x.Row.EndsAt,
                DateOnly.FromDateTime(start.DateTime), TimeOnly.FromDateTime(start.DateTime), TimeOnly.FromDateTime(end.DateTime), x.Row.Status, x.Row.Title, x.Row.HolderId,
                x.Row.BookedBy, x.Row.SeriesId, x.Row.CheckedInAt, x.Row.EndReason, x.Row.Live && x.Row.EndsAt > now && (own || m), x.Row.Version));
        }
        return views;
    }

    /// <summary>The viewer's bookings from yesterday on, and those they made for others (My bookings).</summary>
    public async Task<IReadOnlyList<BookingView>> MineAsync(CancellationToken ct)
    {
        await facilities.RequireAnyAsync(ct);
        var me = context.RequireUser();
        return await ViewsAsync("(b.holder_id = @me or b.booked_by = @me) and b.ends_at > @since", new { me, since = clock.UtcNow.AddDays(-1) }, ct);
    }

    /// <summary>A booking its holder, the person who made it, or a facilities administrator of its site may open; others are told it is not found.</summary>
    public async Task<BookingView> GetAsync(Guid id, CancellationToken ct)
    {
        await facilities.RequireAnyAsync(ct);
        var view = (await ViewsAsync("b.id = @id", new { id }, ct)).SingleOrDefault() ?? throw AppException.NotFound();
        var me = context.RequireUser();
        if (view.HolderId != me && view.BookedBy != me && !await facilities.ManagesAsync((await facilities.SiteRowAsync(view.SiteId, ct))!, ct))
        {
            throw AppException.NotFound();
        }
        return view;
    }

    /// <summary>
    /// Cancels a booking, or with <paramref name="series"/> every occurrence of its series that has not ended. Holders
    /// and the person who made it cancel their own; a facilities administrator cancels any at their sites with a reason,
    /// which is audited and told to the holder.
    /// </summary>
    public async Task<BookingView> CancelAsync(Guid id, bool series, CancelRequest? request, CancellationToken ct)
    {
        var view = await GetAsync(id, ct);
        if (!view.CanCancel)
        {
            throw AppException.Conflict("conflict.booking.not_live");
        }
        var me = context.RequireUser();
        var own = view.HolderId == me || view.BookedBy == me;
        var where = series && view.SeriesId is not null ? "series_id = @key" : "id = @key";
        var key = series && view.SeriesId is { } s ? s : id;
        if (own)
        {
            await cancellations.CancelFutureAsync(where, key, "cancelled", ct);
        }
        else
        {
            var reason = request?.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length > MaxCancelReasonLength)
            {
                throw AppException.Validation("reason", "validation.booking.cancel_reason");
            }
            var site = (await facilities.SiteRowAsync(view.SiteId, ct))!;
            var count = await cancellations.CancelFutureAsync(where, key, "cancelled_by_admin", ct, reason);
            await audit.WriteAsync(new AuditEntry(AuditActions.BookingCancelledByAdmin, "booking", id.ToString(), TargetOrgUnitId: site.OrgUnitId,
                Changes: new { reason, series, cancelled = count }), ct);
        }
        return await GetAsync(id, ct);
    }

    /// <summary>The booking as an iCalendar file (FR-013), times in UTC with the site's zone in the location line.</summary>
    public async Task<(string Name, byte[] Content)> CalendarFileAsync(Guid id, string locale, CancellationToken ct)
    {
        var view = await GetAsync(id, ct);
        static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\r", "").Replace("\n", "\\n");
        static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var resource = Names.Pick(view.ResourceNames, locale);
        var site = Names.Pick(view.SiteNames, locale);
        var text = new StringBuilder()
            .Append("BEGIN:VCALENDAR\r\nVERSION:2.0\r\nPRODID:-//TeamBeats//Booking//EN\r\nCALSCALE:GREGORIAN\r\nMETHOD:PUBLISH\r\n")
            .Append("BEGIN:VEVENT\r\n")
            .Append($"UID:booking-{view.Id:N}@teambeats\r\n")
            .Append($"DTSTAMP:{Utc(clock.UtcNow)}\r\n")
            .Append($"DTSTART:{Utc(view.StartsAt)}\r\n")
            .Append($"DTEND:{Utc(view.EndsAt)}\r\n")
            .Append($"SUMMARY:{Esc(view.Title ?? resource)}\r\n")
            .Append($"LOCATION:{Esc($"{resource}, {site} ({view.TimeZone})")}\r\n")
            .Append(view.Status is BookingStatuses.Cancelled or BookingStatuses.Released or BookingStatuses.Rejected or BookingStatuses.Expired ? "STATUS:CANCELLED\r\n" : "STATUS:CONFIRMED\r\n")
            .Append("END:VEVENT\r\nEND:VCALENDAR\r\n");
        return ($"booking-{view.LocalDate:yyyy-MM-dd}.ics", Encoding.UTF8.GetBytes(text.ToString()));
    }

    /// <summary>A person left the tenant: their future bookings are cancelled (FR-016).</summary>
    internal Task<int> CancelForLeaverAsync(Guid userId, CancellationToken ct) => cancellations.CancelFutureAsync("holder_id = @key", userId, "left", ct);
}
