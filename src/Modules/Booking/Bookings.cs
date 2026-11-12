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
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
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
    BookingApprovals approvals,
    IWorkingCalendar calendars,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    ITenantMemberships memberships,
    IDelegations delegations,
    IReportingLine reporting,
    ITenantSettingsReader tenantSettings,
    INotificationSender notifications,
    IAuditWriter audit,
    IOutbox outbox,
    BookingActivity activity,
    IClock clock)
{
    public const string ConfirmedTemplate = "booking.confirmed";
    public const string SeriesTemplate = "booking.series_confirmed";
    public const string BookedForTemplate = "booking.booked_for_you";
    public const int MaxTeam = 50;
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

    /// <summary>The days in a range on which a facilities administrator closed the site.</summary>
    internal async Task<IReadOnlySet<DateOnly>> ClosedAsync(Guid siteId, DateOnly from, DateOnly to, CancellationToken ct) =>
        (await session.QueryAsync("select day from booking.site_closure where site_id = @siteId and day between @from and @to", new { siteId, from, to },
            r => r.GetFieldValue<DateOnly>(0), ct)).ToHashSet();

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
    private static void CheckSpan(Span span, Rule rule, Settings settings, SiteRow site, WorkingCalendarSnapshot calendar, IReadOnlySet<DateOnly> closed, DateTimeOffset now,
        ValidationErrors errors, bool first = true)
    {
        // A day the site was closed (section 4 edge case) refuses every booking, whatever the rule says about working hours.
        if (closed.Contains(span.Date))
        {
            errors.Add("date", "validation.booking.site_closed");
        }
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

    /// <summary>
    /// The person's rule with the no-show restriction applied (US4 scenario 3): while restricted, they book at most
    /// the restricted number of days ahead.
    /// </summary>
    private async Task<Func<Rule, Rule>> RestrictionAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        var days = await session.QuerySingleOrDefaultAsync(
            "select advance_days from booking.no_show_notice where user_id = @userId and restricted_until > @now and advance_days is not null",
            new { userId, now }, r => (int?)r.GetInt32(0), ct);
        return days is { } d ? rule => rule with { AdvanceDays = Math.Min(rule.AdvanceDays, d) } : rule => rule;
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
            r => (Row: ResourceRow.Read(r), Floor: r.IsDBNull(ResourceRow.Count) ? null : Names.Read(r, ResourceRow.Count),
                Zone: r.IsDBNull(ResourceRow.Count + 1) ? null : Names.Read(r, ResourceRow.Count + 1)), ct);
        if (candidates.Count == 0)
        {
            return new SearchResult(site.Id, site.TimeZone, span.StartsAt, span.EndsAt, []);
        }
        var resolve = await rules.ResolverAsync(ct);
        var settings = await rules.SettingsAsync(ct);
        var calendar = await CalendarAsync(site, ct);
        var closed = await ClosedAsync(site.Id, span.Date, span.Date, ct);
        var now = clock.UtcNow;
        var restrict = await RestrictionAsync(user, now, ct);
        var subtrees = new Dictionary<string, IReadOnlySet<Guid>>();
        var bookable = new List<(ResourceRow Row, IReadOnlyDictionary<string, string>? Floor, IReadOnlyDictionary<string, string>? Zone, Rule Rule)>();
        foreach (var (row, floor, zone) in candidates)
        {
            var rule = restrict(resolve(row));
            var check = new ValidationErrors();
            CheckSpan(span, rule, settings, site, calendar, closed, now, check);
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
        var closed = (await ClosedAsync(site.Id, date, date, ct)).Count > 0;
        return new AvailabilityView(resourceId, date, site.TimeZone, site.OpensAt, site.ClosesAt, calendar.IsWorkingDay(date) && !closed,
            rows.Select(x => new BusySlot(x.Start, x.End, x.Status,
                x.Holder == me || showNames ? (names.TryGetValue(x.Holder, out var u) ? u.DisplayName : null) : null)).ToList());
    }

    // ---- Booking ----

    public async Task<BookResult> BookAsync(BookRequest request, CancellationToken ct)
    {
        var user = context.RequireUser();
        if (request.ForUserId is { } holder && holder != user)
        {
            await facilities.RequireAnyAsync(ct);
            if (!await MayBookForAsync(holder, false, ct))
            {
                throw AppException.Denied("authz.denied.booking_for");
            }
            return await PlaceAsync(request, holder, user, ct);
        }
        return await PlaceAsync(request, user, user, ct);
    }

    /// <summary>
    /// Whether the caller may book for <paramref name="holder"/>, an active member: as their delegate today (FR-011,
    /// spec 016 delegation), or for a team booking also as their manager anywhere up the reporting line.
    /// </summary>
    private async Task<bool> MayBookForAsync(Guid holder, bool team, CancellationToken ct)
    {
        var me = context.RequireUser();
        if (holder == me)
        {
            return true;
        }
        if (await memberships.GetAsync(holder, ct) is not { Status: MembershipStatus.Active })
        {
            return false;
        }
        return (await delegations.DelegatorsOfAsync(me, clock.UtcNow, ct)).Contains(holder) || (team && await reporting.IsManagerOfAsync(me, holder, ct));
    }

    /// <summary>
    /// Books one resource of a kind for each person of a team (US5 scenario 2, FR-012), in the zone when one is given.
    /// Each person gets their own booking under their own rules and limits; a person who cannot be booked is listed
    /// with why, and the others are still booked.
    /// </summary>
    public async Task<TeamBookResult> BookTeamAsync(TeamBookRequest request, CancellationToken ct)
    {
        await facilities.RequireAnyAsync(ct);
        await authz.DemandAsync(Permissions.BookingBook, AuthzTarget.Tenant, ct);
        var people = (request.People ?? []).Distinct().ToList();
        var errors = new ValidationErrors();
        if (people.Count is 0 or > MaxTeam) errors.Add("people", "validation.booking.team");
        if (request.Kind is not { } kind || !ResourceKinds.All.Contains(kind)) errors.Add("kind", "validation.booking.kind");
        if (request.SiteId is null) errors.Add("siteId", "validation.booking.site");
        errors.ThrowIfAny();
        var site = await facilities.ActiveSiteAsync(request.SiteId!.Value, ct);
        await entitlements.DemandAsync(ResourceKinds.Capability(request.Kind!), ct);
        var me = context.RequireUser();
        var candidates = await session.QueryAsync(
            """
            select r.id, r.fixed_user_id from booking.resource r
            left join booking.floor f on f.id = r.floor_id
            where r.site_id = @siteId and r.kind = @kind and r.status = 'active' and (@zoneId::uuid is null or r.zone_id = @zoneId)
            order by f.sort nulls first, r.zone_id nulls last, r.created_at, r.id
            limit 500
            """, new { siteId = site.Id, kind = request.Kind, zoneId = request.ZoneId }, r => (Id: r.GetGuid(0), Fixed: r.Get<Guid?>("fixed_user_id")), ct);
        var names = await users.GetManyAsync(people, ct);
        var used = new HashSet<Guid>();
        var outcomes = new List<TeamOutcome>();
        foreach (var person in people)
        {
            var name = names.TryGetValue(person, out var u) ? u.DisplayName : null;
            if (!await MayBookForAsync(person, true, ct))
            {
                outcomes.Add(new TeamOutcome(person, name, null, "authz.denied.booking_for"));
                continue;
            }
            // A person with a fixed desk here gets that one; the others take the next free resource nobody in the team has.
            var mine = candidates.Where(c => !used.Contains(c.Id) && (c.Fixed is null || c.Fixed == person)).OrderBy(c => c.Fixed == person ? 0 : 1).ToList();
            BookingView? booked = null;
            var problem = "conflict.booking.none_free";
            foreach (var c in mine)
            {
                try
                {
                    booked = (await PlaceAsync(new BookRequest(c.Id, request.Date, request.Start, request.End, request.Title, null), person, me, ct)).Bookings[0];
                    used.Add(c.Id);
                    break;
                }
                catch (AppException ex) when (ex.DetailKey == "conflict.booking.taken")
                {
                    used.Add(c.Id);
                }
                catch (AppException ex) when (ex.Status is 400 or 403 or 409)
                {
                    // The person's own rules or limits refuse it; another resource of the same kind would too.
                    problem = ex.DetailKey ?? ex.Errors?.Values.FirstOrDefault()?.FirstOrDefault() ?? problem;
                    break;
                }
            }
            outcomes.Add(new TeamOutcome(person, name, booked, booked is null ? problem : null));
        }
        return new TeamBookResult(outcomes);
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

        var now = clock.UtcNow;
        var rule = (await RestrictionAsync(holder, now, ct))(await rules.ForAsync(resource, ct));
        var settings = await rules.SettingsAsync(ct);
        if (!await InUnitsAsync(rule, holder, [], ct))
        {
            throw AppException.Denied("authz.denied.booking_units");
        }
        var calendar = await CalendarAsync(site, ct);
        var first = SpanOf(request.Date!.Value, request.Start!.Value, request.End!.Value, site.Zone);
        var closed = await ClosedAsync(site.Id, first.Date, request.Repeat?.Until is { } last && last > first.Date ? last : first.Date, ct);
        CheckSpan(first, rule, settings, site, calendar, closed, now, errors);
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
                if (closed.Contains(d))
                {
                    skipped.Add(new SkippedDate(d, "closed"));
                    continue;
                }
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
                CheckSpan(span, rule, settings, site, calendar, closed, now, check, first: false);
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
        // A restricted resource is held as tentative until its approval, for the approval hold or until the start (FR-007).
        var restricted = resource.NeedsApproval;
        var status = restricted ? BookingStatuses.Tentative : BookingStatuses.Confirmed;
        DateTimeOffset? holdUntil = restricted ? Min(now.AddHours(settings.ApprovalHoldHours), first.StartsAt) : null;
        var ids = new List<Guid>();
        try
        {
            foreach (var span in spans)
            {
                var id = Ids.New();
                await session.ExecAsync(
                    """
                    insert into booking.booking (tenant_id, id, resource_id, holder_id, booked_by, series_id, starts_at, ends_at, held, title, status, hold_until, created_at)
                    values (platform.current_tenant_id(), @id, @resource, @holder, @bookedBy, @seriesId, @starts, @ends, tstzrange(@starts, @heldUntil, '[)'), @title,
                      @status, @holdUntil, @now)
                    """, new
                    {
                        id, resource = resource.Id, holder, bookedBy, seriesId, starts = span.StartsAt, ends = span.EndsAt, heldUntil = span.EndsAt.AddMinutes(rule.BufferMinutes), title,
                        status, holdUntil, now,
                    }, ct);
                ids.Add(id);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ExclusionViolation)
        {
            throw AppException.Conflict("conflict.booking.taken");
        }

        var locale = (await tenantSettings.GetLocaleSettingsAsync(ct)).DefaultLocale;
        var parameters = BookingCancellations.Describe(resource.Names, site.TimeZone, first.StartsAt, first.EndsAt, locale);
        parameters["count"] = ids.Count.ToString(CultureInfo.InvariantCulture);
        if (restricted)
        {
            var instance = await approvals.StartAsync(resource, site, ids[0], holder, bookedBy, ct);
            await session.ExecAsync("update booking.booking set instance_id = @instance where id = any(@ids)", new { instance, ids = ids.ToArray() }, ct);
            await notifications.SendAsync(new NotificationRequest([holder], NotificationCategories.Bookings, BookingApprovals.RequestedTemplate, Link(ids[0]),
                parameters, DedupeKey: $"booking:{ids[0]:N}:requested"), ct);
            await activity.RecordAsync("requested", resource.Kind, site.Id, ids.Count, ct);
        }
        else
        {
            foreach (var (id, span) in ids.Zip(spans))
            {
                await outbox.EnqueueAsync(new BookingConfirmed(id, resource.Id, site.Id, resource.Kind, holder, span.StartsAt, span.EndsAt), ct);
            }
            await activity.RecordAsync("booked", resource.Kind, site.Id, ids.Count, ct);
            if (bookedBy != holder)
            {
                // FR-011: the person booked for is told who booked it.
                parameters["by"] = (await users.GetAsync(bookedBy, ct))?.DisplayName ?? "";
            }
            await notifications.SendAsync(new NotificationRequest([holder], NotificationCategories.Bookings,
                bookedBy != holder ? BookedForTemplate : seriesId is null ? ConfirmedTemplate : SeriesTemplate, Link(ids[0]),
                parameters, DedupeKey: $"booking:{ids[0]:N}:confirmed"), ct);
        }

        var views = await ViewsAsync("b.id = any(@ids)", new { ids = ids.ToArray() }, ct);
        return new BookResult(views, seriesId, skipped);
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    /// <summary>
    /// The check-in window of a booking (FR-008): from the settings' minutes before the start until the grace period
    /// after it, or after the booking was made when that was later. Null when the resource needs no check-in.
    /// </summary>
    internal static (DateTimeOffset From, DateTimeOffset Until)? CheckInWindow(BookingRow row, bool checkIn, Settings settings) =>
        checkIn ? (row.StartsAt.AddMinutes(-settings.CheckInBeforeMinutes), Max(row.StartsAt, row.CreatedAt).AddMinutes(settings.CheckInGraceMinutes)) : null;

    // ---- Reading ----

    private async Task<List<BookingView>> ViewsAsync(string where, object args, CancellationToken ct)
    {
        var me = context.RequireUser();
        var rows = await session.QueryAsync(
            $"""
            select {string.Join(", ", BookingRow.Columns.Split(", ").Select(c => "b." + c))}, r.kind, r.names::text as resource_names, s.id as site, s.names::text as site_names,
                   s.time_zone, s.org_unit_id, r.check_in
            from booking.booking b
            join booking.resource r on r.id = b.resource_id
            join booking.site s on s.id = r.site_id
            where {where}
            order by b.starts_at, b.id
            limit 500
            """, args,
            r => (Row: BookingRow.Read(r), Kind: r.GetString(BookingRow.Count), Resource: Names.Read(r, BookingRow.Count + 1), Site: r.GetGuid(BookingRow.Count + 2),
                SiteNames: Names.Read(r, BookingRow.Count + 3), Zone: r.GetString(BookingRow.Count + 4), Unit: r.Get<Guid?>("org_unit_id"),
                CheckIn: r.GetBoolean(BookingRow.Count + 6)), ct);
        var now = clock.UtcNow;
        var settings = rows.Count == 0 ? Settings.Defaults : await rules.SettingsAsync(ct);
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
            var window = x.Row.Status is BookingStatuses.Confirmed or BookingStatuses.CheckedIn ? CheckInWindow(x.Row, x.CheckIn, settings) : null;
            var canCheckIn = window is { } w && x.Row.Status == BookingStatuses.Confirmed && x.Row.HolderId == me && now >= w.From && now <= w.Until;
            views.Add(new BookingView(x.Row.Id, x.Row.ResourceId, x.Kind, x.Resource, x.Site, x.SiteNames, x.Zone, x.Row.StartsAt, x.Row.EndsAt,
                DateOnly.FromDateTime(start.DateTime), TimeOnly.FromDateTime(start.DateTime), TimeOnly.FromDateTime(end.DateTime), x.Row.Status, x.Row.Title, x.Row.HolderId,
                x.Row.BookedBy, x.Row.SeriesId, x.Row.CheckedInAt, x.Row.EndReason, x.Row.Live && x.Row.EndsAt > now && (own || m), x.Row.Version, x.Row.HoldUntil,
                x.Row.DecisionNote, x.Row.CheckInMethod, window?.From, window?.Until, canCheckIn));
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

    // ---- Check-in (US4, FR-008) ----

    /// <summary>
    /// Checks the holder in to a confirmed booking whose window is open, by <paramref name="method"/>. Only the person
    /// booked for checks in: being there is the point. Checking in twice returns the booking as it is.
    /// </summary>
    public async Task<BookingView> CheckInAsync(Guid id, string? method, CancellationToken ct)
    {
        method ??= CheckInMethods.Booking;
        if (method is not (CheckInMethods.Booking or CheckInMethods.Kiosk))
        {
            throw AppException.Validation("via", "validation.booking.check_in_method");
        }
        var view = await GetAsync(id, ct);
        if (view.HolderId != context.RequireUser())
        {
            throw AppException.Denied("authz.denied.booking_check_in");
        }
        return await CheckInCoreAsync(view, method, ct);
    }

    private async Task<BookingView> CheckInCoreAsync(BookingView view, string method, CancellationToken ct)
    {
        if (view.Status == BookingStatuses.CheckedIn)
        {
            return view;
        }
        if (view.Status != BookingStatuses.Confirmed)
        {
            throw AppException.Conflict(view.Status == BookingStatuses.Tentative ? "conflict.booking.check_in_tentative" : "conflict.booking.not_live");
        }
        if (view.CheckInFrom is not { } from || view.CheckInUntil is not { } until)
        {
            throw AppException.Conflict("conflict.booking.check_in_off");
        }
        var now = clock.UtcNow;
        if (now < from)
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.booking.check_in_early", null, new Dictionary<string, object?> { ["opens"] = from });
        }
        if (now > until)
        {
            throw AppException.Conflict("conflict.booking.check_in_closed");
        }
        var changed = await session.ExecAsync(
            """
            update booking.booking set status = 'checked_in', checked_in_at = @now, check_in_method = @method, row_version = row_version + 1
            where id = @id and status = 'confirmed'
            """, new { id = view.Id, now, method }, ct);
        if (changed == 1)
        {
            await outbox.EnqueueAsync(new CheckedIn(view.Id, view.ResourceId, view.SiteId, view.Kind, view.HolderId, method), ct);
            await activity.RecordAsync("checked_in", view.Kind, view.SiteId, ct);
        }
        return await GetAsync(view.Id, ct);
    }

    private async Task<ResourceRow> ScannedAsync(string code, CancellationToken ct)
    {
        await facilities.RequireAnyAsync(ct);
        var clean = (code ?? "").Trim().ToLowerInvariant();
        // A code is 32 hex characters; anything else, or a code of another tenant, is not found.
        var resource = clean.Length == 32 && clean.All(Uri.IsHexDigit)
            ? await session.QuerySingleOrDefaultAsync($"select {ResourceRow.Columns} from booking.resource where qr_code = @clean and status = 'active'", new { clean },
                ResourceRow.Read, ct)
            : null;
        if (resource is null)
        {
            throw AppException.NotFound();
        }
        await DemandBookAsync(resource, ct);
        return resource;
    }

    /// <summary>
    /// How far ahead "today" reaches for a scan or a kiosk: the end of the day at the site, or later when a booking just after
    /// midnight already has its check-in window open, so it can be checked in to before the date changes.
    /// </summary>
    private async Task<DateTimeOffset> HorizonAsync(DateTimeOffset now, SiteRow site, CancellationToken ct)
    {
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, site.Zone).DateTime);
        var endOfDay = At(localToday.AddDays(1), TimeOnly.MinValue, site.Zone);
        var windowOpens = now.AddMinutes((await rules.SettingsAsync(ct)).CheckInBeforeMinutes).AddTicks(1);
        return windowOpens > endOfDay ? windowOpens : endOfDay;
    }

    /// <summary>The scanning person's booking of the resource for now: the one whose window is open, else their next one today.</summary>
    private async Task<BookingView?> NowAsync(ResourceRow resource, SiteRow site, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var endOfDay = await HorizonAsync(now, site, ct);
        var views = await ViewsAsync("b.resource_id = @resource and b.holder_id = @me and b.status in ('tentative', 'confirmed', 'checked_in') and b.ends_at > @now and b.starts_at < @endOfDay",
            new { resource = resource.Id, me = context.RequireUser(), now, endOfDay }, ct);
        return views.FirstOrDefault(v => v.CanCheckIn) ?? views.FirstOrDefault(v => v.Status == BookingStatuses.CheckedIn) ?? views.FirstOrDefault();
    }

    /// <summary>What a scanned code shows (FR-008): the resource and the person's booking of it for now, if any.</summary>
    public async Task<ScanView> ScanAsync(string code, CancellationToken ct)
    {
        var resource = await ScannedAsync(code, ct);
        var site = await facilities.ActiveSiteAsync(resource.SiteId, ct);
        return new ScanView(resource.Id, resource.Kind, resource.Names, site.Id, site.Names, site.TimeZone, resource.CheckIn, await NowAsync(resource, site, ct));
    }

    /// <summary>Checks in by the scanned code to the person's booking of the resource whose window is open.</summary>
    public async Task<BookingView> ScanCheckInAsync(string code, CancellationToken ct)
    {
        var resource = await ScannedAsync(code, ct);
        var site = await facilities.ActiveSiteAsync(resource.SiteId, ct);
        var booking = await NowAsync(resource, site, ct) ?? throw AppException.Conflict("conflict.booking.no_booking_now");
        return await CheckInCoreAsync(booking, CheckInMethods.Qr, ct);
    }

    /// <summary>A site's kiosk (FR-008): the signed-in person's bookings at the site today that have not ended.</summary>
    public async Task<KioskView> KioskAsync(Guid siteId, CancellationToken ct)
    {
        await facilities.RequireAnyAsync(ct);
        await authz.DemandAsync(Permissions.BookingBook, AuthzTarget.Tenant, ct);
        var site = await facilities.ActiveSiteAsync(siteId, ct);
        var now = clock.UtcNow;
        var views = await ViewsAsync(
            "s.id = @siteId and b.holder_id = @me and b.status in ('tentative', 'confirmed', 'checked_in') and b.ends_at > @now and b.starts_at < @endOfDay",
            new { siteId, me = context.RequireUser(), now, endOfDay = await HorizonAsync(now, site, ct) }, ct);
        return new KioskView(site.Id, site.Names, site.TimeZone, views);
    }

    /// <summary>A person left the tenant: their future bookings are cancelled (FR-016).</summary>
    internal Task<int> CancelForLeaverAsync(Guid userId, CancellationToken ct) => cancellations.CancelFutureAsync("holder_id = @key", userId, "left", ct);
}
