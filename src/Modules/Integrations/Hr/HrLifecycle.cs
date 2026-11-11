using System.Globalization;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Hr.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Hr;

public static class HrChangeKinds
{
    public const string Hire = "hire";
    public const string Assignment = "assignment";
    public const string Termination = "termination";
    public const string Rehire = "rehire";

    public static readonly IReadOnlyList<string> All = [Hire, Assignment, Termination, Rehire];
}

public static class HrChangeStatuses
{
    public const string Pending = "pending";

    /// <summary>An assignment to an org code that no unit is mapped to yet (US3 scenario 3).</summary>
    public const string Unmapped = "unmapped";

    public const string Applied = "applied";
    public const string Cancelled = "cancelled";

    public static readonly IReadOnlyList<string> Listed = [Pending, Unmapped, Applied, Cancelled];
}

/// <summary>A change HR reported that has not been applied yet, as the planner sees it.</summary>
public sealed record OpenHrChange(Guid Id, string Kind, string Status, DateOnly EffectiveDate, IReadOnlyDictionary<string, string?> To);

/// <summary>A change the planner asks for: <paramref name="To"/> holds the assignment fields for an assignment, the flags for a termination.</summary>
public sealed record PlannedHrChange(string Kind, DateOnly EffectiveDate, DateTimeOffset DueAt, IReadOnlyDictionary<string, string?> From,
    IReadOnlyDictionary<string, string?> To);

/// <summary>What one record's new read means: the data to store now, the changes to add and the open changes to cancel.</summary>
public sealed record HrRecordPlan(Dictionary<string, string> Data, IReadOnlyList<PlannedHrChange> Add, IReadOnlyList<(Guid Id, string Reason)> Cancel);

/// <summary>The dates the planner needs, in the tenant time zone and working calendar.</summary>
public sealed record HrPlanClock(DateTimeOffset Now, DateOnly Today, Func<DateOnly, DateTimeOffset> DayStart, Func<DateOnly, DateTimeOffset> HireDue);

public sealed record HrChangeView(
    Guid Id,
    string RecordId,
    Guid? UserId,
    string? UserName,
    string Kind,
    DateOnly EffectiveDate,
    DateTimeOffset DueAt,
    string Status,
    IReadOnlyDictionary<string, string?> Details,
    Guid RunId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? AppliedAt,
    string? CancelledReason);

public sealed record HrOrgCodeView(string Code, Guid? UnitId, IReadOnlyDictionary<string, string>? UnitNames, int People, int Waiting, DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record HrOrgCodeRequest(Guid? UnitId);

/// <summary>
/// HR lifecycle (spec 019 US2 to US4, FR-005 to FR-008, FR-018). Each applied run is compared with what TeamBeats last
/// applied: a new employee is a hire, a changed org unit, manager, title or location is an assignment, a termination
/// date or terminated status is a termination, and a terminated employee who is active again is a rehire. Changes wait
/// until they are due in the tenant time zone, and an assignment to an org code without a unit waits for its mapping
/// without blocking anything else. Applying a change publishes the lifecycle events; the modules that own the data
/// (002, 003, 010, 022) act on them. The first run of a tenant is the baseline and plans nothing but notice periods.
/// </summary>
public sealed class HrLifecycle(
    IDbSession session,
    IOutbox outbox,
    IAuditWriter audit,
    IClock clock,
    ITenantContext context,
    IWorkingCalendar calendars,
    IOrganizationHierarchy hierarchy,
    IUserDirectory users,
    IEntitlementService entitlements)
{
    public const int DueBatch = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string? F(IReadOnlyDictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v : null;

    private static DateOnly? D(IReadOnlyDictionary<string, string> d, string key) =>
        F(d, key) is { } s && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var v) ? v : null;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static Dictionary<string, string?> Assignment(IReadOnlyDictionary<string, string> d) =>
        HrFields.Assignment.ToDictionary(f => f, f => F(d, f), StringComparer.Ordinal);

    private static bool SameValues(IReadOnlyDictionary<string, string?> a, IReadOnlyDictionary<string, string?> b) =>
        HrFields.Assignment.All(f => a.GetValueOrDefault(f) == b.GetValueOrDefault(f));

    /// <summary>Terminated, or leaving on a termination date that is not older than the latest hire.</summary>
    public static bool Leaving(IReadOnlyDictionary<string, string> d) =>
        F(d, HrFields.Status) == HrEmployeeStatuses.Terminated
        || (D(d, HrFields.TerminationDate) is { } end && (D(d, HrFields.HireDate) is not { } hire || end >= hire));

    /// <summary>
    /// Plans one record. <paramref name="before"/> is what TeamBeats last applied (null for a record seen for the first
    /// time); <paramref name="left"/> is true when the person's last termination was applied and no rehire since.
    /// </summary>
    public static HrRecordPlan Plan(IReadOnlyDictionary<string, string>? before, IReadOnlyDictionary<string, string> incoming, bool baseline, bool left,
        IReadOnlyList<OpenHrChange> open, HrPlanClock at)
    {
        var data = new Dictionary<string, string>(incoming, StringComparer.Ordinal);
        var add = new List<PlannedHrChange>();
        var cancel = new List<(Guid, string)>();
        var none = new Dictionary<string, string?>(StringComparer.Ordinal);
        OpenHrChange? Open(string kind) => open.FirstOrDefault(c => c.Kind == kind);
        DateTimeOffset DueOn(DateOnly day) => day <= at.Today ? at.Now : at.DayStart(day);

        // Assignment: the four fields change together on the assignment date (US3 scenario 1, FR-005).
        var openAssignment = Open(HrChangeKinds.Assignment);
        if (before is not null)
        {
            var applied = Assignment(before);
            var next = Assignment(incoming);
            if (!SameValues(applied, next))
            {
                var effective = D(incoming, HrFields.AssignmentDate) is { } a && a > at.Today ? a : at.Today;
                var keep = openAssignment is not null && SameValues(openAssignment.To, next)
                    && (effective <= at.Today ? openAssignment.EffectiveDate <= at.Today : openAssignment.EffectiveDate == effective);
                if (!keep)
                {
                    if (openAssignment is not null)
                    {
                        cancel.Add((openAssignment.Id, "replaced"));
                    }
                    add.Add(new PlannedHrChange(HrChangeKinds.Assignment, effective, DueOn(effective), applied, next));
                }
                // Until the change applies, TeamBeats keeps the assignment it has.
                foreach (var f in HrFields.Assignment)
                {
                    if (applied[f] is { } v)
                    {
                        data[f] = v;
                    }
                    else
                    {
                        data.Remove(f);
                    }
                }
            }
            else if (openAssignment is not null)
            {
                cancel.Add((openAssignment.Id, "reverted"));
            }
        }

        var leaving = Leaving(incoming);
        var active = F(incoming, HrFields.Status) == HrEmployeeStatuses.Active;

        // Termination (US4 scenarios 1 and 2): planned at once, access ends after the last working day.
        var openTermination = Open(HrChangeKinds.Termination);
        var openHire = Open(HrChangeKinds.Hire);
        if (leaving && !left && openHire is null)
        {
            var end = D(incoming, HrFields.TerminationDate);
            var lastDay = end ?? openTermination?.EffectiveDate ?? at.Today;
            var changed = before is null || !Leaving(before) || D(before, HrFields.TerminationDate) != end;
            var historic = before is null && lastDay < at.Today;
            if (openTermination is not null && openTermination.EffectiveDate == lastDay)
            {
                // Already planned for that day.
            }
            else if ((changed || openTermination is not null) && !historic)
            {
                if (openTermination is not null)
                {
                    cancel.Add((openTermination.Id, "replaced"));
                }
                var immediate = end is null || lastDay < at.Today;
                add.Add(new PlannedHrChange(HrChangeKinds.Termination, lastDay, immediate ? at.Now : at.DayStart(lastDay.AddDays(1)), none,
                    new Dictionary<string, string?>(StringComparer.Ordinal) { ["immediate"] = immediate ? "true" : "false" }));
            }
        }
        else if (!leaving && openTermination is not null)
        {
            cancel.Add((openTermination.Id, "rescinded"));
        }

        // Hire (US2): a new employee after the baseline, prepared the lead time before the start date. A joiner who
        // leaves before the hire is due is a cancelled hire, not a termination.
        if (before is null && !baseline && active && !leaving)
        {
            var start = D(incoming, HrFields.HireDate) ?? at.Today;
            add.Add(new PlannedHrChange(HrChangeKinds.Hire, start, start <= at.Today ? at.Now : at.HireDue(start), none, none));
        }
        else if (openHire is not null)
        {
            if (!active || leaving)
            {
                // A hire cancelled in HR before it was due (US2 scenario 3).
                cancel.Add((openHire.Id, "cancelled_in_hr"));
            }
            else if (D(incoming, HrFields.HireDate) is { } start && start != openHire.EffectiveDate)
            {
                cancel.Add((openHire.Id, "replaced"));
                add.Add(new PlannedHrChange(HrChangeKinds.Hire, start, start <= at.Today ? at.Now : at.HireDue(start), none, none));
            }
        }

        // Rehire (US4 scenario 3): a former employee whose access ended is active again.
        var openRehire = Open(HrChangeKinds.Rehire);
        if (left && active && !leaving)
        {
            var start = D(incoming, HrFields.HireDate) is { } h && h > at.Today ? h : at.Today;
            if (openRehire is null || openRehire.EffectiveDate != start)
            {
                if (openRehire is not null)
                {
                    cancel.Add((openRehire.Id, "replaced"));
                }
                add.Add(new PlannedHrChange(HrChangeKinds.Rehire, start, DueOn(start), none, none));
            }
        }
        else if (openRehire is not null)
        {
            cancel.Add((openRehire.Id, "cancelled_in_hr"));
        }

        return new HrRecordPlan(data, add, cancel);
    }

    // ---- planning a run ----

    private sealed record Settings(int LeadDays, bool RehireReactivates);

    private async Task<Settings> SettingsAsync(CancellationToken ct) =>
        await session.QuerySingleOrDefaultAsync("select onboarding_lead_days, rehire_reactivates from integrations.hr_settings", null,
            r => new Settings(r.GetInt32(0), r.GetBoolean(1)), ct) ?? new Settings(5, false);

    public async Task<HrPlanClock> ClockAsync(CancellationToken ct)
    {
        var calendar = await calendars.TenantDefaultAsync(ct);
        var lead = (await SettingsAsync(ct)).LeadDays;
        var now = clock.UtcNow;
        DateTimeOffset DayStart(DateOnly day)
        {
            var local = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            return new DateTimeOffset(local, calendar.Zone.GetUtcOffset(local)).ToUniversalTime();
        }
        return new HrPlanClock(now, calendar.Today(now), DayStart, start => DayStart(lead == 0 ? start : calendar.AddWorkingDays(start, -lead)));
    }

    /// <summary>Open changes and whether each person's access has ended, for every record of the tenant.</summary>
    public async Task<(IReadOnlyDictionary<string, List<OpenHrChange>> Open, IReadOnlySet<string> Left)> StateAsync(CancellationToken ct)
    {
        var open = (await session.QueryAsync(
                "select id, record_id, kind, status, effective_date, details::text from integrations.hr_change where status in ('pending', 'unmapped')", null,
                r => (Record: r.GetString(1), Change: new OpenHrChange(r.GetGuid(0), r.GetString(2), r.GetString(3), r.GetFieldValue<DateOnly>(4), To(r.GetString(5)))),
                ct))
            .GroupBy(x => x.Record, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Select(x => x.Change).ToList(), StringComparer.Ordinal);
        var left = (await session.QueryAsync(
            """
            select distinct on (record_id) record_id, kind from integrations.hr_change
            where status = 'applied' and kind in ('termination', 'rehire') order by record_id, applied_at desc, seq desc
            """, null, r => (Record: r.GetString(0), Kind: r.GetString(1)), ct)).Where(x => x.Kind == HrChangeKinds.Termination)
            .Select(x => x.Record).ToHashSet(StringComparer.Ordinal);
        return (open, left);
    }

    private static IReadOnlyDictionary<string, string?> To(string details)
    {
        var all = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(details) ?? [];
        return all.TryGetValue("to", out var to) && to.ValueKind == JsonValueKind.Object
            ? to.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null, StringComparer.Ordinal)
            : new Dictionary<string, string?>(StringComparer.Ordinal);
    }

    /// <summary>Stores what a run's plans ask for. Terminations are published at once so offboarding can start (US4 scenario 1).</summary>
    public async Task SaveAsync(Guid runId, IReadOnlyList<(string RecordId, Guid? UserId, HrRecordPlan Plan)> plans, IReadOnlyCollection<string> orgCodes,
        CancellationToken ct)
    {
        var now = clock.UtcNow;
        var cancels = plans.SelectMany(p => p.Plan.Cancel).ToList();
        if (cancels.Count > 0)
        {
            await session.ExecAsync(
                """
                update integrations.hr_change c set status = 'cancelled', cancelled_reason = r.reason
                from jsonb_to_recordset(@rows) as r(id uuid, reason text) where c.id = r.id and c.status in ('pending', 'unmapped')
                """, new { rows = Jsonb.From(cancels.Select(c => new { id = c.Id, reason = c.Reason })) }, ct);
        }
        var rows = new List<object>();
        foreach (var (recordId, userId, plan) in plans)
        {
            foreach (var change in plan.Add)
            {
                var id = Ids.New();
                rows.Add(new
                {
                    id, record = recordId, user = userId, kind = change.Kind, effective = change.EffectiveDate, due = change.DueAt,
                    details = new { from = change.From, to = change.To },
                });
                if (change.Kind == HrChangeKinds.Termination)
                {
                    await outbox.EnqueueAsync(new EmployeeTerminated(userId, recordId, change.EffectiveDate, change.To.GetValueOrDefault("immediate") == "true", runId, id), ct);
                }
            }
        }
        if (rows.Count > 0)
        {
            await session.ExecAsync(
                """
                insert into integrations.hr_change (tenant_id, id, record_id, user_id, kind, effective_date, due_at, details, status, run_id, created_at)
                select platform.current_tenant_id(), r.id, r.record, r."user", r.kind, r.effective, r.due, r.details, 'pending', @runId, @now
                from jsonb_to_recordset(@rows) as r(id uuid, record text, "user" uuid, kind text, effective date, due timestamptz, details jsonb)
                """, new { runId, now, rows = Jsonb.From(rows) }, ct);
        }
        if (orgCodes.Count > 0)
        {
            await session.ExecAsync(
                """
                insert into integrations.hr_org_code (tenant_id, code, first_seen_at, last_seen_at)
                select platform.current_tenant_id(), c, @now, @now from unnest(@codes::text[]) as c
                on conflict (tenant_id, code) do update set last_seen_at = excluded.last_seen_at
                """, new { now, codes = orgCodes.ToArray() }, ct);
        }
    }

    // ---- applying due changes ----

    private sealed record DueChange(Guid Id, string RecordId, string Kind, DateOnly EffectiveDate, Guid RunId, IReadOnlyDictionary<string, string?> From,
        IReadOnlyDictionary<string, string?> To);

    /// <summary>Applies every change that is due, oldest first and in HR order on the same date. Returns how many applied.</summary>
    public async Task<int> ApplyDueAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = await session.QueryAsync(
            $"""
            select id, record_id, kind, effective_date, run_id, details::text from integrations.hr_change
            where status = 'pending' and due_at <= @now order by due_at, seq limit {DueBatch}
            """, new { now },
            r =>
            {
                var details = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>>>(r.GetString(5)) ?? [];
                return new DueChange(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetFieldValue<DateOnly>(3), r.GetGuid(4),
                    details.GetValueOrDefault("from") ?? [], details.GetValueOrDefault("to") ?? []);
            }, ct);
        // Changes wait, and are kept, while the plan lacks HR integration.
        if (due.Count == 0 || !await entitlements.IsEnabledAsync(Capabilities.HrIntegration, ct))
        {
            return 0;
        }
        var settings = await SettingsAsync(ct);
        var codes = await CodesAsync(ct);
        var applied = 0;
        var assignmentsBy = new HashSet<Guid>();
        foreach (var change in due)
        {
            var link = await session.QuerySingleOrDefaultAsync(
                "select user_id, connection_id from integrations.hr_employee where record_id = @r", new { r = change.RecordId },
                r => (User: r.Get<Guid?>("user_id"), Connection: r.GetGuid(1)), ct);
            var user = link.User;
            switch (change.Kind)
            {
                case HrChangeKinds.Hire:
                    await outbox.EnqueueAsync(new EmployeeHired(user, change.RecordId, change.EffectiveDate, change.RunId, change.Id), ct);
                    break;
                case HrChangeKinds.Termination:
                    if (user is { } leaver)
                    {
                        await outbox.EnqueueAsync(new EmployeeLeft(leaver, change.RecordId, change.EffectiveDate, change.Id), ct);
                    }
                    break;
                case HrChangeKinds.Rehire:
                    await outbox.EnqueueAsync(new EmployeeRehired(user, change.RecordId, change.EffectiveDate, settings.RehireReactivates, change.RunId, change.Id), ct);
                    break;
                case HrChangeKinds.Assignment:
                    var toCode = change.To.GetValueOrDefault(HrFields.OrgUnitCode);
                    var fromCode = change.From.GetValueOrDefault(HrFields.OrgUnitCode);
                    var moved = toCode is not null && toCode != fromCode;
                    if (moved && codes.GetValueOrDefault(toCode!) is null)
                    {
                        // Only this change waits for the code to be mapped (US3 scenario 3).
                        await session.ExecAsync("update integrations.hr_change set status = 'unmapped' where id = @id", new { id = change.Id }, ct);
                        await session.ExecAsync(
                            """
                            insert into integrations.hr_org_code (tenant_id, code, first_seen_at, last_seen_at) values (platform.current_tenant_id(), @code, @now, @now)
                            on conflict do nothing
                            """, new { code = toCode, now }, ct);
                        continue;
                    }
                    await session.ExecAsync(
                        """
                        update integrations.hr_employee set data = jsonb_strip_nulls(data || @to) where record_id = @r
                        """, new { r = change.RecordId, to = Jsonb.From(HrFields.Assignment.ToDictionary(f => f, f => change.To.GetValueOrDefault(f))) }, ct);
                    if (user is { } mover)
                    {
                        if (moved)
                        {
                            await outbox.EnqueueAsync(new EmployeeTransferred(mover, change.RecordId, fromCode is null ? null : codes.GetValueOrDefault(fromCode),
                                codes[toCode!]!.Value, change.EffectiveDate, change.RunId, change.Id), ct);
                        }
                        var manager = change.To.GetValueOrDefault(HrFields.ManagerEmployeeNumber);
                        if (manager != change.From.GetValueOrDefault(HrFields.ManagerEmployeeNumber))
                        {
                            var managerUser = manager is null ? null : await session.QuerySingleOrDefaultAsync(
                                "select user_id from integrations.hr_employee where data->>'employee_number' = @n and user_id is not null and user_id <> @u limit 1",
                                new { n = manager, u = mover }, r => (Guid?)r.GetGuid(0), ct);
                            await outbox.EnqueueAsync(new EmployeeManagerChanged(mover, change.RecordId, managerUser, change.EffectiveDate, change.RunId, change.Id), ct);
                        }
                    }
                    assignmentsBy.Add(link.Connection);
                    break;
            }
            await session.ExecAsync("update integrations.hr_change set status = 'applied', applied_at = @now, user_id = coalesce(@u, user_id) where id = @id",
                new { id = change.Id, now, u = user }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.HrChangeApplied, "hr_change", change.Id.ToString(),
                Changes: new { user, kind = change.Kind, effectiveDate = Iso(change.EffectiveDate), run = change.RunId, record = change.RecordId }), ct);
            applied++;
        }
        foreach (var connection in assignmentsBy)
        {
            // Titles and locations reach profiles the way every applied sync does.
            await outbox.EnqueueAsync(new HrSyncCompleted(connection, due[0].RunId, 0, applied), ct);
        }
        return applied;
    }

    // ---- org codes ----

    private async Task<Dictionary<string, Guid?>> CodesAsync(CancellationToken ct) =>
        (await session.QueryAsync("select code, unit_id from integrations.hr_org_code", null, r => (Code: r.GetString(0), Unit: r.Get<Guid?>("unit_id")), ct))
        .ToDictionary(x => x.Code, x => x.Unit, StringComparer.Ordinal);

    public async Task<IReadOnlyList<HrOrgCodeView>> OrgCodesAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        var rows = await session.QueryAsync(
            """
            select c.code, c.unit_id, c.first_seen_at, c.last_seen_at,
              (select count(*) from integrations.hr_employee e where e.data->>'org_unit_code' = c.code)::int,
              (select count(*) from integrations.hr_change h where h.status = 'unmapped' and h.details->'to'->>'org_unit_code' = c.code)::int
            from integrations.hr_org_code c order by (c.unit_id is not null), c.code
            """, null,
            r => (Code: r.GetString(0), Unit: r.Get<Guid?>("unit_id"), First: r.GetFieldValue<DateTimeOffset>(2), Last: r.GetFieldValue<DateTimeOffset>(3),
                People: r.GetInt32(4), Waiting: r.GetInt32(5)), ct);
        var names = await hierarchy.GetNamesAsync(rows.Select(r => r.Unit).OfType<Guid>().Distinct().ToList(), ct);
        return rows.Select(r => new HrOrgCodeView(r.Code, r.Unit, r.Unit is { } u ? names.GetValueOrDefault(u) : null, r.People, r.Waiting, r.First, r.Last)).ToList();
    }

    /// <summary>
    /// Maps an HR org code to a unit, or clears the mapping (FR-007). Assignments waiting for the code become due at
    /// once. Units are never created from HR codes: a new code is listed here until someone maps it (US3 scenario 4).
    /// </summary>
    public async Task<HrOrgCodeView> MapOrgCodeAsync(string code, HrOrgCodeRequest request, CancellationToken ct)
    {
        await RequireAsync(ct);
        var exists = await session.ScalarAsync<bool>("select exists (select 1 from integrations.hr_org_code where code = @code)", new { code }, ct);
        if (!exists)
        {
            throw AppException.NotFound();
        }
        if (request.UnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.Validation("unitId", "validation.hr.org_unit");
        }
        var now = clock.UtcNow;
        await session.ExecAsync("update integrations.hr_org_code set unit_id = @unit, mapped_by = @user, mapped_at = @now where code = @code",
            new { code, unit = request.UnitId, user = context.RequireUser(), now }, ct);
        if (request.UnitId is not null)
        {
            await session.ExecAsync(
                """
                update integrations.hr_change set status = 'pending', due_at = least(due_at, @now)
                where status = 'unmapped' and details->'to'->>'org_unit_code' = @code
                """, new { code, now }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.HrOrgCodeMapped, "hr_org_code", code, TargetOrgUnitId: request.UnitId,
            Changes: new { code, unit = request.UnitId }), ct);
        await ApplyDueAsync(ct);
        return (await OrgCodesAsync(ct)).Single(c => c.Code == code);
    }

    public async Task<IReadOnlyList<HrChangeView>> ChangesAsync(string? status, int limit, CancellationToken ct)
    {
        await RequireAsync(ct);
        if (status is not null && !HrChangeStatuses.Listed.Contains(status))
        {
            throw AppException.Validation("status", "validation.hr.change_status");
        }
        var rows = await session.QueryAsync(
            """
            select c.id, c.record_id, coalesce(c.user_id, e.user_id) as user_id, c.kind, c.effective_date, c.due_at, c.status, c.details::text, c.run_id, c.created_at,
              c.applied_at, c.cancelled_reason
            from integrations.hr_change c left join integrations.hr_employee e on e.record_id = c.record_id
            where (@status::text is null or c.status = @status)
            order by case c.status when 'unmapped' then 0 when 'pending' then 1 else 2 end, c.due_at desc, c.seq desc limit @limit
            """, new { status, limit = Math.Clamp(limit, 1, 200) },
            r => new HrChangeView(r.GetGuid(0), r.GetString(1), r.Get<Guid?>("user_id"), null, r.GetString(3), r.GetFieldValue<DateOnly>(4),
                r.GetFieldValue<DateTimeOffset>(5), r.GetString(6), To(r.GetString(7)), r.GetGuid(8), r.GetFieldValue<DateTimeOffset>(9),
                r.Get<DateTimeOffset?>("applied_at"), r.Get<string>("cancelled_reason")), ct);
        var people = await users.GetManyAsync(rows.Select(r => r.UserId).OfType<Guid>().ToList(), ct);
        return rows.Select(r => r with { UserName = r.UserId is { } u && people.TryGetValue(u, out var p) ? p.DisplayName : null }).ToList();
    }

    private async Task RequireAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrIntegration, ct))
        {
            throw AppException.NotInPackage(Capabilities.HrIntegration);
        }
    }
}
