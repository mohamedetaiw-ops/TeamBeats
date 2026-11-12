using System.Globalization;
using System.Text;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Calendar;

/// <summary>
/// The unified calendar (spec 037): merges meetings (Outlook, Google), TeamBeats events, location holidays, the
/// employee's leave and, for managers, team leave into canonical items, in a bounded range, for the signed-in employee
/// only. Every source is read through its owner's contract on each request and wrapped so that one failing source
/// leaves the others working (FR-023). Reporting lines and leave are never cached across requests (FR-024), so a manager
/// change or a cancellation shows at once.
/// </summary>
public sealed class UnifiedCalendar(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IEntitlementService entitlements,
    ITenantSettingsReader tenantSettings,
    IMeetingCalendars meetings,
    IHrCalendarFeed hr,
    IWorkingCalendar working,
    IReportingLine reporting,
    IUserDirectory users,
    CompanyEvents events,
    CalendarSettings settings,
    RequestLocale locale,
    IAuditWriter audit)
{
    public const int MaxDays = 62;
    public const int MaxHolidayDays = 366;
    public const int PageSize = 500;
    public const int MaxTeamListed = 200;
    public const int MaxTeam = 1000;
    public const int AlertDays = 14;
    public const int StartingSoonDays = 7;
    public static readonly TimeSpan JoinLead = TimeSpan.FromMinutes(10);

    private TimeZoneInfo? _zone;
    private IReadOnlyList<Guid>? _team;

    // ---- Layers and preferences ----

    public async Task<TimeZoneInfo> ZoneAsync(CancellationToken ct)
    {
        if (_zone is null)
        {
            var id = (await tenantSettings.GetLocaleSettingsAsync(ct)).TimeZone;
            _zone = TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : TimeZoneInfo.Utc;
        }
        return _zone;
    }

    public async Task<DateOnly> TodayAsync(CancellationToken ct) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.UtcNow, await ZoneAsync(ct)).DateTime);

    /// <summary>The viewer's reports in scope: direct reports, and indirect ones when the tenant setting says so. Read per request.</summary>
    public async Task<IReadOnlyList<Guid>> TeamAsync(CancellationToken ct)
    {
        if (_team is null)
        {
            var me = context.RequireUser();
            var indirect = (await settings.CurrentAsync(ct)).IndirectReports;
            var ids = indirect ? [.. await reporting.AllReportsAsync(me, ct)] : (await reporting.DirectReportsAsync(me, ct)).ToList();
            _team = ids.Where(id => id != me).Distinct().Take(MaxTeam).ToList();
        }
        return _team;
    }

    /// <summary>The layers that apply to the viewer (FR-010). The page offers only these.</summary>
    public async Task<IReadOnlyList<string>> AvailableAsync(CancellationToken ct)
    {
        var list = new List<string>();
        if (await entitlements.IsEnabledAsync(Capabilities.IntegrationMicrosoft365, ct) || await entitlements.IsEnabledAsync(Capabilities.IntegrationConnectors, ct))
        {
            list.Add(CalendarLayers.Meeting);
        }
        list.AddRange([CalendarLayers.Company, CalendarLayers.Department, CalendarLayers.Important, CalendarLayers.Holiday]);
        if (await entitlements.IsEnabledAsync(Capabilities.HrLeave, ct))
        {
            list.Add(CalendarLayers.MyLeave);
            if ((await TeamAsync(ct)).Count > 0)
            {
                list.Add(CalendarLayers.TeamLeave);
            }
        }
        return list;
    }

    public async Task<PreferencesView> PreferencesAsync(CancellationToken ct)
    {
        var available = await AvailableAsync(ct);
        var row = await session.QuerySingleOrDefaultAsync("select layers, view from calendar.preference where user_id = @me", new { me = context.RequireUser() },
            r => (Layers: r.GetFieldValue<string[]>(0), View: r.GetString(1)), ct);
        var layers = (row.Layers ?? [.. (await settings.CurrentAsync(ct)).DefaultLayers]).Where(available.Contains).ToList();
        var view = row.View ?? "agenda";
        if (view == "team" && !available.Contains(CalendarLayers.TeamLeave))
        {
            view = "agenda";
        }
        return new PreferencesView(available, layers, view);
    }

    public async Task<PreferencesView> SavePreferencesAsync(PreferencesRequest request, CancellationToken ct)
    {
        var available = await AvailableAsync(ct);
        var errors = new ValidationErrors();
        var layers = (request.Layers ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if (layers.Any(l => !CalendarLayers.All.Contains(l)))
        {
            errors.Add("layers", "validation.calendar_preferences.layers");
        }
        var view = request.View ?? "agenda";
        if (!CalendarViews.All.Contains(view) || view == "team" && !available.Contains(CalendarLayers.TeamLeave))
        {
            errors.Add("view", "validation.calendar_preferences.view");
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            insert into calendar.preference (tenant_id, user_id, layers, view, updated_at)
            values (platform.current_tenant_id(), @me, @layers, @view, @now)
            on conflict (tenant_id, user_id) do update set layers = excluded.layers, view = excluded.view, updated_at = excluded.updated_at
            """, new { me = context.RequireUser(), layers, view, now = clock.UtcNow }, ct);
        return new PreferencesView(available, layers.Where(available.Contains).ToList(), view);
    }

    // ---- Events ----

    public static void ValidateRange(DateOnly from, DateOnly to, int maxDays)
    {
        if (to <= from || to.DayNumber - from.DayNumber > maxDays)
        {
            throw AppException.Validation("to", "validation.calendar.range");
        }
    }

    private async Task<(DateTimeOffset From, DateTimeOffset To)> InstantsAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var zone = await ZoneAsync(ct);
        return (CompanyEvents.ToInstant(from.ToDateTime(TimeOnly.MinValue), zone), CompanyEvents.ToInstant(to.ToDateTime(TimeOnly.MinValue), zone));
    }

    /// <summary>
    /// Canonical items between two dates (to exclusive), for the layers asked for (the viewer's preference when none),
    /// deduplicated and ordered, one page at a time.
    /// </summary>
    public async Task<CalendarEventsResponse> EventsAsync(DateOnly from, DateOnly to, string? layers, string? cursor, bool refresh, CancellationToken ct)
    {
        ValidateRange(from, to, MaxDays);
        var (items, sources, wanted) = await CollectAsync(from, to, layers, refresh, ct);
        var offset = 0;
        if (cursor is not null && (!TryCursor(cursor, out offset) || offset < 0 || offset > items.Count))
        {
            throw AppException.Validation("cursor", "validation.calendar.cursor");
        }
        var page = items.Skip(offset).Take(PageSize).ToList();
        var next = offset + PageSize < items.Count ? Convert.ToBase64String(Encoding.UTF8.GetBytes((offset + PageSize).ToString(CultureInfo.InvariantCulture))) : null;
        return new CalendarEventsResponse((await ZoneAsync(ct)).Id, from, to, wanted, sources, page, next);
    }

    private static bool TryCursor(string cursor, out int offset)
    {
        offset = 0;
        try
        {
            return int.TryParse(Encoding.UTF8.GetString(Convert.FromBase64String(cursor)), NumberStyles.None, CultureInfo.InvariantCulture, out offset);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal async Task<(List<CalendarItemView> Items, List<SourceStatus> Sources, IReadOnlyList<string> Layers)> CollectAsync(DateOnly from, DateOnly to,
        string? layerFilter, bool refresh, CancellationToken ct)
    {
        var zone = await ZoneAsync(ct);
        var available = await AvailableAsync(ct);
        IReadOnlyList<string> wanted;
        if (string.IsNullOrWhiteSpace(layerFilter))
        {
            wanted = (await PreferencesAsync(ct)).Layers;
        }
        else
        {
            var asked = layerFilter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (asked.Any(l => !CalendarLayers.All.Contains(l)))
            {
                throw AppException.Validation("layers", "validation.calendar_preferences.layers");
            }
            wanted = asked.Where(available.Contains).Distinct().ToList();
        }
        var (fromAt, toAt) = await InstantsAsync(from, to, ct);
        var items = new List<CalendarItemView>();
        var sources = new List<SourceStatus>();
        var now = clock.UtcNow;
        var me = context.RequireUser();
        var lastDay = to.AddDays(-1);

        // Company events first, so meeting copies of them can merge into them (FR-004).
        var teamBeatsEvents = new List<CalendarItemView>();
        if (wanted.Any(l => l is CalendarLayers.Company or CalendarLayers.Department or CalendarLayers.Important))
        {
            await SourceAsync(ItemSources.Events, sources, async () =>
            {
                foreach (var (o, rsvp) in await events.VisibleAsync(fromAt, toAt, ct))
                {
                    var layer = CalendarLayers.ForKind(o.Event.Kind);
                    if (!wanted.Contains(layer))
                    {
                        continue;
                    }
                    var actions = new List<string>();
                    var end = o.End ?? CompanyEvents.ToInstant(o.EndDate!.Value.ToDateTime(TimeOnly.MinValue), zone);
                    if (o.Event.OnlineUrl is not null && o.Event.Status == "published" && end > now)
                    {
                        actions.Add(ItemActions.Join);
                    }
                    if (rsvp is { Open: true })
                    {
                        actions.Add(ItemActions.Rsvp);
                    }
                    teamBeatsEvents.Add(Item(zone, $"event:{o.Event.Id}:{o.Key}", layer, ItemSources.Events, o.Event.Id.ToString(), Pick(o.Event.Titles),
                        o.Start, o.End, o.StartDate, o.EndDate, o.Event.Status == "cancelled" ? ItemStatuses.Cancelled : ItemStatuses.Confirmed,
                        o.Event.OrgUnitId is null ? "tenant" : "unit", actions) with
                    {
                        Location = o.Event.Location,
                        OnlineUrl = o.Event.OnlineUrl,
                        Description = o.Event.Descriptions.Count > 0 ? Pick(o.Event.Descriptions) : null,
                        Rsvp = rsvp,
                        OccurrenceKey = o.Key,
                        Recurring = o.Event.Recurrence is not null,
                    });
                }
                return now;
            }, ct);
        }

        if (wanted.Contains(CalendarLayers.Meeting))
        {
            IReadOnlyList<MeetingSourceResult> results;
            try
            {
                results = await meetings.ReadAsync(new CalendarQuery(fromAt, toAt, refresh), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not AppException)
            {
                results = [new MeetingSourceResult(ItemSources.Outlook, "degraded", null, [])];
            }
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var result in results)
            {
                sources.Add(new SourceStatus(result.Source, result.State, result.AsOf));
                foreach (var o in result.Occurrences.Where(o => !o.IsCancelled && o.Response != "declined"))
                {
                    // Same meeting through two sources or two calendars: its iCalendar UID. Never the title or the time.
                    var uid = string.IsNullOrWhiteSpace(o.ICalUId) ? $"{result.Source}:{o.Id}" : o.ICalUId!;
                    if (MergeIntoEvent(teamBeatsEvents, uid, o, zone))
                    {
                        continue;
                    }
                    var item = Meeting(o, result.Source, zone, now);
                    if (seen.TryGetValue(uid, out var index))
                    {
                        if (items[index].OnlineUrl is null && item.OnlineUrl is not null)
                        {
                            items[index] = items[index] with { OnlineUrl = item.OnlineUrl, Actions = item.Actions };
                        }
                        continue;
                    }
                    seen[uid] = items.Count;
                    items.Add(item);
                }
            }
        }
        items.AddRange(teamBeatsEvents);

        if (wanted.Contains(CalendarLayers.Holiday))
        {
            await SourceAsync(ItemSources.Holidays, sources, async () =>
            {
                items.AddRange(HolidayItems(await hr.HolidaysAsync(me, from, lastDay, ct), zone));
                return now;
            }, ct);
        }

        if (wanted.Contains(CalendarLayers.MyLeave) || wanted.Contains(CalendarLayers.TeamLeave))
        {
            await SourceAsync(ItemSources.Leave, sources, async () =>
            {
                if (wanted.Contains(CalendarLayers.MyLeave))
                {
                    foreach (var r in await hr.LeaveAsync([me], from, lastDay, ct))
                    {
                        var actions = r.RequestId is null ? new List<string>() : [ItemActions.OpenLeave];
                        items.Add(Item(zone, $"leave:{r.RequestId?.ToString() ?? r.Start.ToString("O", CultureInfo.InvariantCulture)}", CalendarLayers.MyLeave,
                            ItemSources.Leave, r.RequestId?.ToString() ?? string.Empty, r.TypeNames is null ? null : Pick(r.TypeNames), null, null, r.Start, r.End,
                            r.Status == "approved" ? ItemStatuses.Confirmed : ItemStatuses.Pending, "private", actions) with
                        {
                            HalfStart = r.StartHalf,
                            HalfEnd = r.EndHalf,
                            LeaveRequestId = r.RequestId,
                            TypeKey = r.TypeKey,
                        });
                    }
                }
                if (wanted.Contains(CalendarLayers.TeamLeave))
                {
                    var team = await TeamAsync(ct);
                    var ranges = await hr.LeaveAsync(team, from, lastDay, ct);
                    for (var d = from; d < to; d = d.AddDays(1))
                    {
                        var away = ranges.Where(r => r.Status == "approved" && r.Start <= d && d < r.End).Select(r => r.UserId).Distinct().Count();
                        if (away > 0)
                        {
                            items.Add(Item(zone, $"team:{d:yyyy-MM-dd}", CalendarLayers.TeamLeave, ItemSources.Leave, d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                                null, null, null, d, d.AddDays(1), ItemStatuses.Confirmed, "team", [ItemActions.ViewTeam]) with { Count = away });
                        }
                    }
                }
                return now;
            }, ct);
        }

        var ordered = items
            .OrderBy(i => i.SortDate)
            .ThenBy(i => i.AllDay ? 0 : 1)
            .ThenBy(i => i.Start ?? DateTimeOffset.MinValue)
            .ThenBy(i => LayerOrder(i.Layer))
            .ThenBy(i => i.Title, StringComparer.CurrentCulture)
            .ThenBy(i => i.Id, StringComparer.Ordinal)
            .ToList();
        return (ordered, sources, wanted);
    }

    private static int LayerOrder(string layer) => Array.IndexOf(CalendarLayers.All, layer);

    /// <summary>An Outlook or Google copy of a TeamBeats event (UID <c>&lt;id&gt;@teambeats</c>) adds its join link to the event and is not shown itself.</summary>
    private static bool MergeIntoEvent(List<CalendarItemView> teamBeatsEvents, string uid, CalendarOccurrence o, TimeZoneInfo zone)
    {
        if (!uid.EndsWith("@teambeats", StringComparison.OrdinalIgnoreCase) || uid.Length < 36 || !Guid.TryParse(uid[..36], out var eventId))
        {
            return false;
        }
        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(o.Start, zone).DateTime);
        var index = teamBeatsEvents.FindIndex(e => e.SourceId == eventId.ToString() && e.SortDate == day);
        if (index < 0)
        {
            index = teamBeatsEvents.FindIndex(e => e.SourceId == eventId.ToString());
        }
        if (index < 0)
        {
            return false;
        }
        if (teamBeatsEvents[index].OnlineUrl is null && o.OnlineJoinUrl is not null)
        {
            var e = teamBeatsEvents[index];
            teamBeatsEvents[index] = e with { OnlineUrl = o.OnlineJoinUrl, Actions = [.. e.Actions.Prepend(ItemActions.Join).Distinct()] };
        }
        return true;
    }

    private CalendarItemView Meeting(CalendarOccurrence o, string source, TimeZoneInfo zone, DateTimeOffset now)
    {
        var actions = new List<string>();
        if (o.OnlineJoinUrl is not null && !o.IsPrivate && o.End > now)
        {
            actions.Add(ItemActions.Join);
        }
        if (!string.IsNullOrEmpty(o.WebLink))
        {
            actions.Add(ItemActions.OpenSource);
        }
        var status = o.ShowAs == "tentative" || o.Response == "tentative" ? ItemStatuses.Tentative : ItemStatuses.Confirmed;
        DateOnly? startDate = o.IsAllDay ? DateOnly.FromDateTime(o.Start.UtcDateTime) : null;
        DateOnly? endDate = o.IsAllDay ? DateOnly.FromDateTime(o.End.UtcDateTime) : null;
        if (endDate <= startDate)
        {
            endDate = startDate!.Value.AddDays(1);
        }
        return Item(zone, $"{source}:{o.Id}", CalendarLayers.Meeting, source, o.Id, o.IsPrivate ? null : o.Subject, o.IsAllDay ? null : o.Start, o.IsAllDay ? null : o.End,
            startDate, endDate, status, o.IsPrivate ? "private" : "personal", actions) with
        {
            Location = o.IsPrivate ? null : o.Location,
            OnlineUrl = o.IsPrivate ? null : o.OnlineJoinUrl,
            WebLink = string.IsNullOrEmpty(o.WebLink) ? null : o.WebLink,
            Free = o.ShowAs == "free",
            IsPrivate = o.IsPrivate,
        };
    }

    /// <summary>Consecutive holidays with the same name become one range, so Eid shows as one bar (FR-013).</summary>
    private IEnumerable<CalendarItemView> HolidayItems(IReadOnlyList<HolidayEntry> holidays, TimeZoneInfo zone)
    {
        var list = holidays.OrderBy(h => h.Date).ToList();
        for (var i = 0; i < list.Count;)
        {
            var start = list[i];
            var title = Pick(start.Names);
            var j = i + 1;
            while (j < list.Count && list[j].Date == list[j - 1].Date.AddDays(1) && Pick(list[j].Names) == title && !list[j].HalfDay && !list[j - 1].HalfDay)
            {
                j++;
            }
            var end = list[j - 1].Date.AddDays(1);
            yield return Item(zone, $"holiday:{start.Date:yyyy-MM-dd}", CalendarLayers.Holiday, ItemSources.Holidays, start.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                title, null, null, start.Date, end, ItemStatuses.Confirmed, "tenant", []) with { HalfStart = start.HalfDay && j == i + 1 };
            i = j;
        }
    }

    private static CalendarItemView Item(TimeZoneInfo zone, string id, string layer, string source, string sourceId, string? title, DateTimeOffset? start,
        DateTimeOffset? end, DateOnly? startDate, DateOnly? endDate, string status, string visibility, IReadOnlyList<string> actions) =>
        new(id, layer, source, sourceId, title, startDate is not null, start?.ToUniversalTime(), end?.ToUniversalTime(), startDate, endDate, zone.Id, status, visibility, actions)
        {
            SortDate = startDate ?? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start!.Value, zone).DateTime),
        };

    private string Pick(IReadOnlyDictionary<string, string> names)
    {
        if (names.TryGetValue(locale.Locale, out var exact) && !string.IsNullOrWhiteSpace(exact))
        {
            return exact;
        }
        var language = locale.Locale.Split('-')[0];
        return names.FirstOrDefault(p => p.Key.StartsWith(language, StringComparison.OrdinalIgnoreCase)).Value
            ?? names.GetValueOrDefault("en") ?? names.Values.FirstOrDefault() ?? string.Empty;
    }

    private static async Task SourceAsync(string source, List<SourceStatus> sources, Func<Task<DateTimeOffset>> read, CancellationToken ct)
    {
        try
        {
            sources.Add(new SourceStatus(source, "ok", await read()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not AppException && !ct.IsCancellationRequested)
        {
            sources.Add(new SourceStatus(source, "degraded", null));
        }
    }

    // ---- Summary, holidays and sources ----

    public async Task<CalendarSummaryResponse> SummaryAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        ValidateRange(from, to, MaxDays);
        var prefs = await PreferencesAsync(ct);
        var (items, sources, _) = await CollectAsync(from, to, prefs.Layers.Count == 0 ? "holiday" : string.Join(',', prefs.Layers), false, ct);
        if (prefs.Layers.Count == 0)
        {
            items.Clear();
        }
        var zone = await ZoneAsync(ct);
        var days = new List<DaySummary>();
        for (var d = from; d < to; d = d.AddDays(1))
        {
            var counts = CalendarLayers.All.ToDictionary(l => l, _ => 0);
            var teamAway = 0;
            foreach (var item in items.Where(i => Touches(i, d, zone)))
            {
                if (item.Layer == CalendarLayers.TeamLeave)
                {
                    teamAway += item.Count ?? 0;
                }
                if (item.Status != ItemStatuses.Cancelled)
                {
                    counts[item.Layer]++;
                }
            }
            days.Add(new DaySummary(d, counts.Where(c => c.Value > 0).ToDictionary(c => c.Key, c => c.Value), teamAway));
        }
        var today = await TodayAsync(ct);
        var holidays = new List<HolidayAlert>();
        if (prefs.Available.Contains(CalendarLayers.Holiday) && prefs.Layers.Contains(CalendarLayers.Holiday))
        {
            try
            {
                holidays.AddRange((await hr.HolidaysAsync(context.RequireUser(), today, today.AddDays(AlertDays - 1), ct))
                    .Select(h => new HolidayAlert(h.Date, Pick(h.Names), h.HalfDay)).Take(3));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not AppException)
            {
                // The alert is a hint; the holidays source state already says when holidays cannot be read.
            }
        }
        var teamToday = 0;
        if (prefs.Layers.Contains(CalendarLayers.TeamLeave))
        {
            var ranges = await hr.LeaveAsync(await TeamAsync(ct), today, today, ct);
            teamToday = ranges.Where(r => r.Status == "approved").Select(r => r.UserId).Distinct().Count();
        }
        return new CalendarSummaryResponse(zone.Id, today, from, to, prefs.Available, prefs.Layers, prefs.View, prefs.Available.Contains(CalendarLayers.TeamLeave), days,
            new CalendarAlerts(holidays, teamToday), sources);
    }

    /// <summary>Whether the item touches a local date: all-day by its dates, timed by its instants in the zone.</summary>
    public static bool Touches(CalendarItemView item, DateOnly day, TimeZoneInfo zone)
    {
        if (item.AllDay)
        {
            return item.StartDate <= day && day < item.EndDate;
        }
        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(item.Start!.Value, zone).DateTime);
        var endLocal = TimeZoneInfo.ConvertTime(item.End!.Value, zone);
        var end = DateOnly.FromDateTime(endLocal.DateTime);
        if (endLocal.TimeOfDay == TimeSpan.Zero && end > start)
        {
            end = end.AddDays(-1);
        }
        return start <= day && day <= end;
    }

    public async Task<IReadOnlyList<HolidayView>> HolidaysAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        ValidateRange(from, to, MaxHolidayDays);
        var zone = await ZoneAsync(ct);
        return HolidayItems(await hr.HolidaysAsync(context.RequireUser(), from, to.AddDays(-1), ct), zone)
            .Select(i => new HolidayView(i.StartDate!.Value, i.EndDate!.Value, i.Title ?? string.Empty, i.HalfStart)).ToList();
    }

    public async Task<IReadOnlyList<SourceStatus>> SourcesAsync(CancellationToken ct)
    {
        var today = await TodayAsync(ct);
        var all = string.Join(',', await AvailableAsync(ct));
        var (_, sources, _) = await CollectAsync(today, today.AddDays(1), all, false, ct);
        return sources;
    }

    // ---- Team availability ----

    public async Task<TeamAvailabilityResponse> TeamAvailabilityAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        ValidateRange(from, to, MaxDays);
        if (!await entitlements.IsEnabledAsync(Capabilities.HrLeave, ct))
        {
            throw AppException.NotFound();
        }
        var team = await TeamAsync(ct);
        if (team.Count == 0)
        {
            throw AppException.NotFound();
        }
        var current = await settings.CurrentAsync(ct);
        var zone = await ZoneAsync(ct);
        var today = await TodayAsync(ct);
        var lastDay = to.AddDays(-1);
        var rangeFrom = today < from ? today : from;
        var rangeTo = lastDay > today.AddDays(StartingSoonDays) ? lastDay : today.AddDays(StartingSoonDays);
        var ranges = await hr.LeaveAsync(team, rangeFrom, rangeTo, ct);
        var names = await users.GetManyAsync(team, ct);
        var members = team.Where(names.ContainsKey).ToList();
        var calendars = new Dictionary<Guid, WorkingCalendarSnapshot>();
        foreach (var id in members)
        {
            calendars[id] = await working.ForUserAsync(id, ct);
        }

        decimal AwayOn(Guid user, DateOnly d)
        {
            var away = 0m;
            foreach (var r in ranges.Where(r => r.UserId == user && r.Status == "approved" && r.Start <= d && d < r.End))
            {
                var half = (r.StartHalf && d == r.Start) || (r.EndHalf && d == r.End.AddDays(-1));
                away = Math.Max(away, half ? 0.5m : 1m);
            }
            return away;
        }

        var days = new List<TeamDay>();
        for (var d = from; d < to; d = d.AddDays(1))
        {
            var workingPeople = members.Where(m => calendars[m].IsWorkingDay(d)).ToList();
            if (workingPeople.Count == 0)
            {
                days.Add(new TeamDay(d, false, 0, 0, 0, null, false));
                continue;
            }
            var away = workingPeople.Sum(m => AwayOn(m, d));
            var available = workingPeople.Count - away;
            var share = (int)Math.Round(available * 100m / workingPeople.Count, MidpointRounding.AwayFromZero);
            days.Add(new TeamDay(d, true, workingPeople.Count, away, available, share, available * 100m < current.Threshold * workingPeople.Count));
        }
        var onLeaveToday = members.Count(m => AwayOn(m, today) > 0);
        var soon = ranges.Where(r => r.Start > today && r.Start <= today.AddDays(StartingSoonDays)).Select(r => r.UserId).Distinct().Count();
        string? TypeName(LeaveRange r) => r.TypeNames is null ? null : Pick(r.TypeNames);
        var people = members.OrderBy(m => names[m].DisplayName, StringComparer.CurrentCulture).Take(MaxTeamListed)
            .Select(m => new TeamMember(m, names[m].DisplayName, ranges.Where(r => r.UserId == m && r.End > from && r.Start <= lastDay)
                .Select(r => new TeamRange(r.Start, r.End, r.Status, r.StartHalf, r.EndHalf, r.TypeKey, TypeName(r), r.RequestId)).ToList()))
            .ToList();
        var upcoming = ranges.Where(r => r.End > today && names.ContainsKey(r.UserId)).OrderBy(r => r.Start).ThenBy(r => names[r.UserId].DisplayName, StringComparer.CurrentCulture)
            .Take(20).Select(r => new UpcomingLeave(r.UserId, names[r.UserId].DisplayName, r.Start, r.End, r.Status, TypeName(r))).ToList();
        await audit.WriteAsync(new AuditEntry(AuditActions.CalendarTeamViewed, "calendar_team", context.RequireUser().ToString(),
            Changes: new { from, to, people = members.Count }), ct);
        return new TeamAvailabilityResponse(zone.Id, from, to, today, members.Count, current.Threshold, current.IndirectReports,
            new TeamToday(members.Count - onLeaveToday, members.Count, onLeaveToday, soon), days, people, upcoming, Math.Max(0, members.Count - MaxTeamListed));
    }
}
