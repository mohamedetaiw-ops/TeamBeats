using System.Text.Json;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Calendar.Contracts;
using TeamBeats.Modules.Content.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Calendar;

internal sealed record EventRow(
    Guid Id,
    Guid? OrgUnitId,
    string Kind,
    Dictionary<string, string> Titles,
    Dictionary<string, string> Descriptions,
    bool AllDay,
    DateTimeOffset? StartAt,
    DateTimeOffset? EndAt,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string TimeZone,
    string? Location,
    string? OnlineUrl,
    ContentAudience Audience,
    RecurrenceRule? Recurrence,
    bool Rsvp,
    string Status,
    DateTimeOffset? CancelledAt,
    int Version,
    DateTimeOffset UpdatedAt)
{
    public const string Columns =
        "id, org_unit_id, kind, titles::text, descriptions::text, all_day, start_at, end_at, start_date, end_date, time_zone, location, online_url, " +
        "audience::text, recurrence::text, rsvp, status, cancelled_at, version, updated_at";

    public static EventRow Read(NpgsqlDataReader r) => new(
        r.GetGuid(0), r.Get<Guid?>("org_unit_id"), r.GetString(2),
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3)) ?? [],
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(4)) ?? [],
        r.GetBoolean(5), r.Get<DateTimeOffset?>("start_at"), r.Get<DateTimeOffset?>("end_at"),
        r.IsDBNull(8) ? null : DateOnly.FromDateTime(r.GetDateTime(8)), r.IsDBNull(9) ? null : DateOnly.FromDateTime(r.GetDateTime(9)),
        r.GetString(10), r.Get<string>("location"), r.Get<string>("online_url"),
        JsonSerializer.Deserialize<ContentAudience>(r.GetString(13), JsonDefaults.Options) ?? ContentAudience.Everyone,
        r.IsDBNull(14) ? null : JsonSerializer.Deserialize<RecurrenceRule>(r.GetString(14), JsonDefaults.Options),
        r.GetBoolean(15), r.GetString(16), r.Get<DateTimeOffset?>("cancelled_at"), r.GetInt32(18), r.GetFieldValue<DateTimeOffset>(19));

    public EventView View() => new(Id, OrgUnitId, Kind, Titles, Descriptions, AllDay, StartAt, EndAt, StartDate, EndDate, TimeZone, Location, OnlineUrl, Audience,
        Recurrence, Rsvp, Status, CancelledAt, Version, UpdatedAt);

    /// <summary>A unit event with no audience is for its unit and everything below it, never a sibling (section 7).</summary>
    public ContentAudience AppliesTo => Audience.IsEveryone && OrgUnitId is { } unit
        ? new ContentAudience([new AudienceClause(OrgUnits: [new UnitTarget(unit)])])
        : Audience;

    public TimeZoneInfo Zone => TimeZoneInfo.TryFindSystemTimeZoneById(TimeZone, out var zone) ? zone : TimeZoneInfo.Utc;
}

/// <summary>One occurrence of a TeamBeats event in a range: timed (instants) or all-day (dates, end exclusive).</summary>
internal sealed record Occurrence(EventRow Event, string Key, DateTimeOffset? Start, DateTimeOffset? End, DateOnly? StartDate, DateOnly? EndDate);

/// <summary>
/// Company and department events and important dates (spec 037 US5, FR-017 to FR-020). Holders of <c>content.publish</c>
/// at the owning unit (tenant scope for whole tenant events) manage them; who sees one is the spec 009 audience inside
/// the owning subtree, checked on every read. Answers are kept per occurrence.
/// </summary>
public sealed class CompanyEvents(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IAudiences audiences,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    IAuditWriter audit,
    IOutbox outbox)
{
    public const int MaxTitle = 200;
    public const int MaxDescription = 4000;
    public const int MaxLocation = 200;
    public const int MaxUrl = 2000;
    public static readonly TimeSpan CancelledShownFor = TimeSpan.FromDays(7);
    public static readonly string[] Answers = ["going", "maybe", "declined"];

    // ---- Admin ----

    public async Task<IReadOnlyList<EventView>> ListAdminAsync(CancellationToken ct)
    {
        var coverage = await RequireCoverageAsync(ct);
        var rows = await session.QueryAsync($"select {EventRow.Columns} from calendar.event order by coalesce(start_at, start_date::timestamptz) desc limit 500",
            null, EventRow.Read, ct);
        return rows.Where(r => Manages(coverage, r)).Select(r => r.View()).ToList();
    }

    public async Task<EventView> GetAdminAsync(Guid id, CancellationToken ct)
    {
        var coverage = await RequireCoverageAsync(ct);
        var row = await RowAsync(id, ct);
        return row is not null && Manages(coverage, row) ? row.View() : throw AppException.NotFound();
    }

    public async Task<EventView> CreateAsync(EventRequest request, CancellationToken ct)
    {
        await DemandManageAsync(request.OrgUnitId, ct);
        var v = await ValidateAsync(request, ct);
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into calendar.event (tenant_id, id, org_unit_id, kind, titles, descriptions, all_day, start_at, end_at, start_date, end_date, time_zone,
              location, online_url, audience, recurrence, series_end, rsvp, created_by, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @unit, @kind, @titles, @descriptions, @allDay, @startAt, @endAt, @startDate, @endDate, @zone,
              @location, @url, @audience, @recurrence, @seriesEnd, @rsvp, @actor, @now, @now)
            """,
            new
            {
                id, unit = request.OrgUnitId, kind = v.Kind, titles = Jsonb.From(v.Titles), descriptions = Jsonb.From(v.Descriptions), allDay = request.AllDay,
                startAt = v.StartAt, endAt = v.EndAt, startDate = v.StartDate, endDate = v.EndDate, zone = v.Zone, location = v.Location, url = v.Url,
                audience = Jsonb.From(v.Audience), recurrence = v.Recurrence is null ? null : Jsonb.From(v.Recurrence), seriesEnd = v.SeriesEnd,
                rsvp = request.Rsvp, actor = context.RequireUser(), now,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.CalendarEventCreated, "calendar_event", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { kind = v.Kind, titles = v.Titles, allDay = request.AllDay, recurring = v.Recurrence is not null }), ct);
        await outbox.EnqueueAsync(new CalendarEventPublished(id, request.OrgUnitId, v.Kind, false), ct);
        return (await RowAsync(id, ct))!.View();
    }

    public async Task<EventView> UpdateAsync(Guid id, string? ifMatch, EventRequest request, CancellationToken ct)
    {
        var row = await ManagedAsync(id, ct);
        if (ifMatch is not null && ifMatch != $"\"v{row.Version}\"")
        {
            throw AppException.Conflict("conflict.calendar_event.changed");
        }
        if (request.OrgUnitId != row.OrgUnitId)
        {
            await DemandManageAsync(request.OrgUnitId, ct);
        }
        var v = await ValidateAsync(request, ct);
        var changed = await session.ExecAsync(
            """
            update calendar.event set org_unit_id = @unit, kind = @kind, titles = @titles, descriptions = @descriptions, all_day = @allDay, start_at = @startAt,
              end_at = @endAt, start_date = @startDate, end_date = @endDate, time_zone = @zone, location = @location, online_url = @url, audience = @audience,
              recurrence = @recurrence, series_end = @seriesEnd, rsvp = @rsvp, updated_at = @now, version = version + 1
            where id = @id and version = @version
            """,
            new
            {
                id, version = row.Version, unit = request.OrgUnitId, kind = v.Kind, titles = Jsonb.From(v.Titles), descriptions = Jsonb.From(v.Descriptions),
                allDay = request.AllDay, startAt = v.StartAt, endAt = v.EndAt, startDate = v.StartDate, endDate = v.EndDate, zone = v.Zone, location = v.Location,
                url = v.Url, audience = Jsonb.From(v.Audience), recurrence = v.Recurrence is null ? null : Jsonb.From(v.Recurrence), seriesEnd = v.SeriesEnd,
                rsvp = request.Rsvp, now = clock.UtcNow,
            }, ct);
        if (changed == 0)
        {
            throw AppException.Conflict("conflict.calendar_event.changed");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.CalendarEventChanged, "calendar_event", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { kind = v.Kind, titles = v.Titles, allDay = request.AllDay, recurring = v.Recurrence is not null }), ct);
        await outbox.EnqueueAsync(new CalendarEventPublished(id, request.OrgUnitId, v.Kind, true), ct);
        return (await RowAsync(id, ct))!.View();
    }

    public async Task<EventView> CancelAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, ct);
        if (row.Status == "cancelled")
        {
            return row.View();
        }
        await session.ExecAsync("update calendar.event set status = 'cancelled', cancelled_at = @now, updated_at = @now, version = version + 1 where id = @id",
            new { id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.CalendarEventCancelled, "calendar_event", id.ToString(), TargetOrgUnitId: row.OrgUnitId), ct);
        await outbox.EnqueueAsync(new CalendarEventCancelled(id, row.OrgUnitId, false), ct);
        return (await RowAsync(id, ct))!.View();
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, ct);
        await session.ExecAsync("delete from calendar.event where id = @id", new { id }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.CalendarEventDeleted, "calendar_event", id.ToString(), TargetOrgUnitId: row.OrgUnitId), ct);
        await outbox.EnqueueAsync(new CalendarEventCancelled(id, row.OrgUnitId, true), ct);
    }

    public async Task<ResponsesView> ResponsesAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, ct);
        var answers = await session.QueryAsync("select occurrence_key, user_id, answer, answered_at from calendar.response where event_id = @id order by occurrence_key, answered_at",
            new { id }, r => (Key: r.GetString(0), User: r.GetGuid(1), Answer: r.GetString(2), At: r.GetFieldValue<DateTimeOffset>(3)), ct);
        var counts = answers.GroupBy(a => a.Key).Select(g => new ResponseCounts(g.Key, g.Count(a => a.Answer == "going"), g.Count(a => a.Answer == "maybe"),
            g.Count(a => a.Answer == "declined"))).ToList();
        var names = await users.GetManyAsync(answers.Select(a => a.User).Distinct().ToList(), ct);
        var people = answers.Select(a => new ResponsePerson(a.Key, new PersonRef(a.User, names.GetValueOrDefault(a.User)?.DisplayName ?? string.Empty), a.Answer, a.At)).ToList();
        _ = row;
        return new ResponsesView(counts, people);
    }

    // ---- Employees ----

    public async Task<RsvpView> RespondAsync(Guid id, ResponseRequest request, CancellationToken ct)
    {
        var me = context.RequireUser();
        var row = await RowAsync(id, ct);
        if (row is null || !await audiences.IncludesAsync(row.AppliesTo, me, ct))
        {
            throw AppException.NotFound();
        }
        var errors = new ValidationErrors();
        if (request.Answer is null || !Answers.Contains(request.Answer))
        {
            errors.Add("answer", "validation.calendar_event.answer");
        }
        var occurrence = request.OccurrenceKey is null ? null : Expand(row, null, null).FirstOrDefault(o => o.Key == request.OccurrenceKey);
        if (occurrence is null)
        {
            errors.Add("occurrenceKey", "validation.calendar_event.occurrence");
        }
        errors.ThrowIfAny();
        if (!row.Rsvp || row.Status != "published" || OccurrenceEnd(occurrence!, row) <= clock.UtcNow)
        {
            throw AppException.Conflict("conflict.calendar_event.rsvp_closed");
        }
        await session.ExecAsync(
            """
            insert into calendar.response (tenant_id, event_id, occurrence_key, user_id, answer, answered_at)
            values (platform.current_tenant_id(), @id, @key, @me, @answer, @now)
            on conflict (tenant_id, event_id, occurrence_key, user_id) do update set answer = excluded.answer, answered_at = excluded.answered_at
            """, new { id, key = occurrence!.Key, me, answer = request.Answer, now = clock.UtcNow }, ct);
        await outbox.EnqueueAsync(new CalendarEventResponded(id, occurrence.Key, me, request.Answer!), ct);
        return new RsvpView(request.Answer, true);
    }

    /// <summary>
    /// The occurrences the signed-in employee may see between two instants: published events, and cancelled ones for 7
    /// days after cancelling, whose audience includes them. With each, the viewer's own answer and, for people who manage
    /// the event, the counts.
    /// </summary>
    internal async Task<IReadOnlyList<(Occurrence Occurrence, RsvpView? Rsvp)>> VisibleAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        var me = context.RequireUser();
        var now = clock.UtcNow;
        var rows = await session.QueryAsync(
            $"""
            select {EventRow.Columns} from calendar.event
            where (status = 'published' or cancelled_at > @cancelledSince)
              and coalesce(start_at, start_date::timestamptz) < @toMargin and series_end > @fromMargin
            order by coalesce(start_at, start_date::timestamptz)
            limit 1000
            """,
            new { cancelledSince = now - CancelledShownFor, toMargin = to.AddDays(2), fromMargin = from.AddDays(-2) }, EventRow.Read, ct);
        var list = new List<(Occurrence, RsvpView?)>();
        if (rows.Count == 0)
        {
            return list;
        }
        var coverage = await authz.CoverageAsync(Permissions.ContentPublish, ct);
        var visible = new List<EventRow>();
        foreach (var row in rows)
        {
            if (await audiences.IncludesAsync(row.AppliesTo, me, ct))
            {
                visible.Add(row);
            }
        }
        var ids = visible.Where(r => r.Rsvp).Select(r => r.Id).ToArray();
        var answers = ids.Length == 0 ? [] : await session.QueryAsync(
            "select event_id, occurrence_key, user_id, answer from calendar.response where event_id = any(@ids)",
            new { ids }, r => (Event: r.GetGuid(0), Key: r.GetString(1), User: r.GetGuid(2), Answer: r.GetString(3)), ct);
        foreach (var row in visible)
        {
            var manages = Manages(coverage, row);
            foreach (var o in Expand(row, from, to))
            {
                RsvpView? rsvp = null;
                if (row.Rsvp)
                {
                    var mine = answers.FirstOrDefault(a => a.Event == row.Id && a.Key == o.Key && a.User == me).Answer;
                    var open = row.Status == "published" && OccurrenceEnd(o, row) > now;
                    rsvp = manages
                        ? new RsvpView(mine, open, answers.Count(a => a.Event == row.Id && a.Key == o.Key && a.Answer == "going"),
                            answers.Count(a => a.Event == row.Id && a.Key == o.Key && a.Answer == "maybe"),
                            answers.Count(a => a.Event == row.Id && a.Key == o.Key && a.Answer == "declined"))
                        : new RsvpView(mine, open);
                }
                list.Add((o, rsvp));
            }
        }
        return list;
    }

    /// <summary>The occurrences of a row that touch the instants; the whole series when both are null.</summary>
    internal static IEnumerable<Occurrence> Expand(EventRow row, DateTimeOffset? from, DateTimeOffset? to)
    {
        var zone = row.Zone;
        if (row.AllDay)
        {
            var length = row.EndDate!.Value.DayNumber - row.StartDate!.Value.DayNumber;
            foreach (var date in Recurrence.Dates(row.StartDate.Value, row.Recurrence))
            {
                var end = date.AddDays(length);
                if (from is { } f && to is { } t)
                {
                    // All-day dates are midnight to midnight in the event's zone; the range end is exclusive.
                    if (ToInstant(date.ToDateTime(TimeOnly.MinValue), zone) >= t || ToInstant(end.ToDateTime(TimeOnly.MinValue), zone) <= f)
                    {
                        continue;
                    }
                }
                yield return new Occurrence(row, date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), null, null, date, end);
            }
            yield break;
        }
        var localStart = TimeZoneInfo.ConvertTime(row.StartAt!.Value, zone);
        var duration = row.EndAt!.Value - row.StartAt.Value;
        foreach (var date in Recurrence.Dates(DateOnly.FromDateTime(localStart.DateTime), row.Recurrence))
        {
            var start = ToInstant(date.ToDateTime(TimeOnly.FromTimeSpan(localStart.TimeOfDay)), zone);
            var end = start + duration;
            if (from is { } f && to is { } t && (start >= t || end <= f))
            {
                continue;
            }
            yield return new Occurrence(row, start.UtcDateTime.ToString("yyyy-MM-ddTHH:mm'Z'", System.Globalization.CultureInfo.InvariantCulture), start, end, null, null);
        }
    }

    /// <summary>A local wall time in a zone as an instant. A time that a daylight saving jump skips moves forward by the jump.</summary>
    internal static DateTimeOffset ToInstant(DateTime local, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        while (zone.IsInvalidTime(unspecified))
        {
            unspecified = unspecified.AddMinutes(30);
        }
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), TimeSpan.Zero);
    }

    private static DateTimeOffset OccurrenceEnd(Occurrence o, EventRow row) =>
        o.End ?? ToInstant(o.EndDate!.Value.ToDateTime(TimeOnly.MinValue), row.Zone);

    // ---- Helpers ----

    private sealed record Valid(string Kind, Dictionary<string, string> Titles, Dictionary<string, string> Descriptions, DateTimeOffset? StartAt, DateTimeOffset? EndAt,
        DateOnly? StartDate, DateOnly? EndDate, string Zone, string? Location, string? Url, ContentAudience Audience, RecurrenceRule? Recurrence, DateTimeOffset SeriesEnd);

    private async Task<Valid> ValidateAsync(EventRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var titles = Clean(request.Titles);
        if (titles.Count == 0 || titles.Values.Any(t => t.Length > MaxTitle))
        {
            errors.Add("titles", "validation.calendar_event.title");
        }
        var descriptions = Clean(request.Descriptions);
        if (descriptions.Values.Any(d => d.Length > MaxDescription))
        {
            errors.Add("descriptions", "validation.calendar_event.description");
        }
        var kind = request.Kind ?? EventKinds.Company;
        if (!EventKinds.All.Contains(kind))
        {
            errors.Add("kind", "validation.calendar_event.kind");
        }
        var zoneId = string.IsNullOrWhiteSpace(request.TimeZone) ? "UTC" : request.TimeZone.Trim();
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(zoneId, out var zone))
        {
            errors.Add("timeZone", "validation.calendar_event.time_zone");
            zone = TimeZoneInfo.Utc;
        }
        var now = clock.UtcNow;
        var horizon = now.AddYears(Recurrence.MaxYears);
        DateOnly firstDate;
        if (request.AllDay)
        {
            if (request.StartDate is not { } s || request.EndDate is not { } e || e <= s || e.DayNumber - s.DayNumber > 31)
            {
                errors.Add("endDate", "validation.calendar_event.dates");
            }
            firstDate = request.StartDate ?? DateOnly.FromDateTime(now.UtcDateTime);
        }
        else
        {
            if (request.Start is not { } s || request.End is not { } e || e <= s || e - s > TimeSpan.FromDays(7))
            {
                errors.Add("end", "validation.calendar_event.times");
            }
            firstDate = request.Start is { } st ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(st, zone).DateTime) : DateOnly.FromDateTime(now.UtcDateTime);
        }
        if (firstDate.ToDateTime(TimeOnly.MinValue) > horizon.UtcDateTime)
        {
            errors.Add("start", "validation.calendar_event.too_far");
        }
        var location = string.IsNullOrWhiteSpace(request.Location) ? null : request.Location.Trim();
        if (location is { Length: > MaxLocation })
        {
            errors.Add("location", "validation.calendar_event.location");
        }
        var url = string.IsNullOrWhiteSpace(request.OnlineUrl) ? null : request.OnlineUrl.Trim();
        if (url is not null && (url.Length > MaxUrl || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps))
        {
            errors.Add("onlineUrl", "validation.calendar_event.online_url");
        }
        var audience = request.Audience ?? ContentAudience.Everyone;
        audiences.Validate(audience, "audience", errors);
        if (request.OrgUnitId is { } owner)
        {
            foreach (var unit in (audience.AnyOf ?? []).SelectMany(c => c.OrgUnits ?? []))
            {
                if (!await hierarchy.IsAncestorOrSelfAsync(owner, unit.Id, ct))
                {
                    errors.Add("audience", "validation.calendar_event.audience_outside");
                    break;
                }
            }
        }
        Recurrence.Validate(request.Recurrence, firstDate, errors);
        errors.ThrowIfAny();
        var lastDate = Recurrence.LastDate(firstDate, request.Recurrence);
        DateTimeOffset seriesEnd;
        if (request.AllDay)
        {
            var length = request.EndDate!.Value.DayNumber - request.StartDate!.Value.DayNumber;
            seriesEnd = ToInstant(lastDate.AddDays(length + 1).ToDateTime(TimeOnly.MinValue), zone);
        }
        else
        {
            var localStart = TimeZoneInfo.ConvertTime(request.Start!.Value, zone);
            seriesEnd = ToInstant(lastDate.ToDateTime(TimeOnly.FromTimeSpan(localStart.TimeOfDay)), zone) + (request.End!.Value - request.Start.Value);
        }
        return new Valid(kind, titles, descriptions, request.AllDay ? null : request.Start!.Value.ToUniversalTime(), request.AllDay ? null : request.End!.Value.ToUniversalTime(),
            request.AllDay ? request.StartDate : null, request.AllDay ? request.EndDate : null, zone.Id, location, url, audience, request.Recurrence, seriesEnd);
    }

    private static Dictionary<string, string> Clean(IReadOnlyDictionary<string, string>? values) =>
        (values ?? new Dictionary<string, string>()).Where(p => !string.IsNullOrWhiteSpace(p.Value) && p.Key.Length is >= 2 and <= 10)
            .ToDictionary(p => p.Key, p => p.Value.Trim());

    private async Task<EventRow?> RowAsync(Guid id, CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync($"select {EventRow.Columns} from calendar.event where id = @id", new { id }, EventRow.Read, ct);

    private async Task<EventRow> ManagedAsync(Guid id, CancellationToken ct)
    {
        var coverage = await RequireCoverageAsync(ct);
        var row = await RowAsync(id, ct);
        return row is not null && Manages(coverage, row) ? row : throw AppException.NotFound();
    }

    private static bool Manages(OrgCoverage coverage, EventRow row) => row.OrgUnitId is { } unit ? coverage.Covers(unit) : coverage.WholeTenant;

    private async Task<OrgCoverage> RequireCoverageAsync(CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.ContentPublish, ct);
        return coverage.Any ? coverage : throw AppException.Denied();
    }

    private async Task DemandManageAsync(Guid? unit, CancellationToken ct) =>
        await authz.DemandAsync(Permissions.ContentPublish, unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant, ct);
}
