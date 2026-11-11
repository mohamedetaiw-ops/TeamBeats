using System.Globalization;
using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Integrations.Contracts;
using TeamBeats.Modules.People.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Integrations.Hr;

public static class HrMatchStates
{
    public const string Matched = "matched";
    public const string Unmatched = "unmatched";
    public const string Conflict = "conflict";

    /// <summary>Unmatched for longer than <see cref="HrSync.RetryDays"/> (FR-015): still retried, listed apart.</summary>
    public const string Stale = "stale";

    public static readonly IReadOnlyList<string> Listed = [Matched, Unmatched, Stale, Conflict];
}

public static class HrMatchRules
{
    public const string WorkEmail = "work_email";
    public const string EmployeeNumber = "employee_number";
    public const string Manual = "manual";

    public static readonly IReadOnlyList<string> Automatic = [WorkEmail, EmployeeNumber];
}

public static class HrRunStatuses
{
    public const string Applied = "applied";
    public const string Held = "held";
    public const string Confirmed = "confirmed";
    public const string Discarded = "discarded";
    public const string Superseded = "superseded";
}

public sealed record HrSettingsView(IReadOnlyList<string> MatchRule, int ChangeThresholdPercent, int Version, int OnboardingLeadDays = 5, bool RehireReactivates = false);

public sealed record HrRunView(
    Guid Id,
    Guid ConnectionId,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int Total,
    int Matched,
    int Unmatched,
    int Conflicts,
    int Refused,
    int Changed,
    int Terminations,
    int Moves,
    int ActiveBefore,
    IReadOnlyList<HrProblem> Problems,
    Guid? DecidedBy,
    DateTimeOffset? DecidedAt);

/// <summary>One HR record as the HR administrator sees it in the results lists.</summary>
public sealed record HrRecordRow(
    string RecordId,
    string? EmployeeNumber,
    string? WorkEmail,
    string? NameEn,
    string? NameAr,
    string State,
    string? MatchRule,
    Guid? UserId,
    string? UserName,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt,
    Guid LastRunId);

public sealed record HrRecordPage(IReadOnlyList<HrRecordRow> Items, string? NextCursor);

public sealed record HrCounts(int Matched, int Unmatched, int Stale, int Conflicts);

public sealed record HrConnectionSummary(Guid Id, string Kind, string DisplayName, bool Enabled);

public sealed record HrOverview(HrSettingsView Settings, HrConnectionSummary? Connection, HrCounts Counts, HrRunView? LastRun, HrRunView? HeldRun);

public sealed record HrSettingsRequest(IReadOnlyList<string>? MatchRule, int? ChangeThresholdPercent, int? OnboardingLeadDays = null, bool? RehireReactivates = null);

/// <summary>
/// HR sync (spec 019 US1, FR-002, FR-004, FR-014, FR-015, FR-018). Each run stores the canonical records the pack
/// read, matches them to TeamBeats users by the tenant's ordered rule and publishes <see cref="HrSyncCompleted"/>, so
/// the modules that own the data apply it. A run that would terminate or move more than the tenant's threshold of
/// active matched employees stops before applying and waits for an HR administrator.
/// </summary>
public sealed class HrSync(
    IDbSession session,
    IUserDirectory users,
    IEmployeeNumbers employeeNumbers,
    IOutbox outbox,
    IAuditWriter audit,
    IClock clock,
    ITenantContext context,
    IEntitlementService entitlements,
    HrLifecycle lifecycle)
{
    /// <summary>Unmatched HR records are retried each sync; after this many days they are listed as stale (FR-015).</summary>
    public const int RetryDays = 30;

    /// <summary>Terminated employees' HR copies are removed this many days after their last day (section 8).</summary>
    public const int KeepAfterTerminationDays = 30;

    public const int RunRetentionDays = 90;
    public const int MaxProblemsKept = 500;

    public static readonly IReadOnlyList<string> PackKinds = [HrRestPack.KindName, HrSftpPack.KindName];

    // ---- canonical form ----

    public static Dictionary<string, string> ToFields(HrEmployee e)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                d[key] = value;
            }
        }
        Put(HrFields.RecordId, e.RecordId);
        Put(HrFields.EmployeeNumber, e.EmployeeNumber);
        Put(HrFields.WorkEmail, e.WorkEmail);
        Put(HrFields.NameEn, e.NameEn);
        Put(HrFields.NameAr, e.NameAr);
        Put(HrFields.Status, e.Status);
        Put(HrFields.HireDate, e.HireDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Put(HrFields.TerminationDate, e.TerminationDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Put(HrFields.JobTitle, e.JobTitle);
        Put(HrFields.Grade, e.Grade);
        Put(HrFields.EmploymentType, e.EmploymentType);
        Put(HrFields.CostCentre, e.CostCentre);
        Put(HrFields.Location, e.Location);
        Put(HrFields.OrgUnitCode, e.OrgUnitCode);
        Put(HrFields.ManagerEmployeeNumber, e.ManagerEmployeeNumber);
        Put(HrFields.AssignmentDate, e.AssignmentDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        Put(HrFields.PersonalEmail, e.PersonalEmail);
        return d;
    }

    private static Dictionary<string, string> Fields(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

    private static string? F(IReadOnlyDictionary<string, string> d, string key) => d.TryGetValue(key, out var v) ? v : null;

    // ---- settings ----

    public async Task<HrSettingsView> SettingsAsync(CancellationToken ct)
    {
        await session.ExecAsync("insert into integrations.hr_settings (tenant_id) values (platform.current_tenant_id()) on conflict do nothing", null, ct);
        return (await session.QuerySingleOrDefaultAsync("select match_rule, change_threshold_percent, version, onboarding_lead_days, rehire_reactivates from integrations.hr_settings", null,
            r => new HrSettingsView(r.GetFieldValue<string[]>(0), r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetBoolean(4)), ct))!;
    }

    public static string ETag(int version) => $"\"v{version}\"";

    public async Task<HrSettingsView> ChangeSettingsAsync(HrSettingsRequest request, string? ifMatch, CancellationToken ct)
    {
        await RequireAsync(ct);
        var current = await SettingsAsync(ct);
        if (ifMatch != ETag(current.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.hr.settings_changed", null,
                new Dictionary<string, object?> { ["etag"] = ETag(current.Version) });
        }
        var errors = new ValidationErrors();
        var rule = request.MatchRule ?? current.MatchRule;
        if (rule.Count is 0 or > 2 || rule.Distinct(StringComparer.Ordinal).Count() != rule.Count || rule.Any(r => !HrMatchRules.Automatic.Contains(r)))
        {
            errors.Add("matchRule", "validation.hr.match_rule");
        }
        var threshold = request.ChangeThresholdPercent ?? current.ChangeThresholdPercent;
        if (threshold is < 1 or > 100)
        {
            errors.Add("changeThresholdPercent", "validation.hr.threshold");
        }
        var lead = request.OnboardingLeadDays ?? current.OnboardingLeadDays;
        if (lead is < 0 or > 30)
        {
            errors.Add("onboardingLeadDays", "validation.hr.lead_days");
        }
        var rehire = request.RehireReactivates ?? current.RehireReactivates;
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            update integrations.hr_settings set match_rule = @rule, change_threshold_percent = @threshold, onboarding_lead_days = @lead,
              rehire_reactivates = @rehire, version = version + 1, updated_at = now()
            """, new { rule = rule.ToArray(), threshold, lead, rehire }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrSettingsChanged, "hr_settings", null,
            Changes: new { matchRule = rule, changeThresholdPercent = threshold, onboardingLeadDays = lead, rehireReactivates = rehire }), ct);
        return await SettingsAsync(ct);
    }

    private async Task RequireAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrIntegration, ct))
        {
            throw AppException.NotInPackage(Capabilities.HrIntegration);
        }
    }

    // ---- runs ----

    private sealed record Stored(string RecordId, Dictionary<string, string> Data, Guid? UserId, string State, string? Rule);

    /// <summary>Records a run of what the pack read. Returns the run with its counts.</summary>
    public async Task<HrRunView> RunAsync(Guid connectionId, HrRead read, CancellationToken ct)
    {
        var settings = await SettingsAsync(ct);
        var now = clock.UtcNow;
        var runId = Ids.New();
        // A newer read replaces a run that is still waiting for confirmation.
        await session.ExecAsync("delete from integrations.hr_incoming where run_id in (select id from integrations.hr_run where status = 'held')", null, ct);
        await session.ExecAsync("update integrations.hr_run set status = 'superseded', finished_at = @now where status = 'held'", new { now }, ct);
        await session.ExecAsync(
            "insert into integrations.hr_run (tenant_id, id, connection_id, started_at, status) values (platform.current_tenant_id(), @runId, @connectionId, @now, 'applied')",
            new { runId, connectionId, now }, ct);

        var stored = await StoredAsync(ct);
        var incoming = read.Employees.ToDictionary(e => e.RecordId, ToFields, StringComparer.Ordinal);
        var (activeBefore, terminations, moves) = Changes(stored, incoming);
        var problems = read.Problems.Take(MaxProblemsKept).ToList();
        if (activeBefore > 0 && (terminations + moves) * 100 > settings.ChangeThresholdPercent * activeBefore)
        {
            await session.ExecAsync(
                """
                insert into integrations.hr_incoming (tenant_id, run_id, record_id, data)
                select platform.current_tenant_id(), @runId, r.id, r.data from jsonb_to_recordset(@rows) as r(id text, data jsonb)
                """, new { runId, rows = Jsonb.From(incoming.Select(i => new { id = i.Key, data = i.Value })) }, ct);
            await session.ExecAsync(
                """
                update integrations.hr_run set status = 'held', finished_at = @now, total = @total, refused = @refused, terminations = @terminations,
                  moves = @moves, active_before = @activeBefore, problems = @problems where id = @runId
                """, new { runId, now, total = incoming.Count, refused = read.Problems.Count, terminations, moves, activeBefore, problems = Jsonb.From(problems) }, ct);
            await audit.WriteAsync(new AuditEntry(AuditActions.HrSyncHeld, "hr_run", runId.ToString(),
                Changes: new { terminations, moves, activeBefore, threshold = settings.ChangeThresholdPercent }), ct);
            await outbox.EnqueueAsync(new HrSyncHeld(connectionId, runId, terminations, moves), ct);
            return (await RunViewAsync(runId, ct))!;
        }
        await ApplyAsync(runId, connectionId, settings, stored, incoming, ct);
        await session.ExecAsync(
            "update integrations.hr_run set refused = @refused, terminations = @terminations, moves = @moves, active_before = @activeBefore, problems = @problems where id = @runId",
            new { runId, refused = read.Problems.Count, terminations, moves, activeBefore, problems = Jsonb.From(problems) }, ct);
        return (await RunViewAsync(runId, ct))!;
    }

    /// <summary>Active matched employees before the run, and how many the run would terminate or move to another org code.</summary>
    private static (int ActiveBefore, int Terminations, int Moves) Changes(IReadOnlyDictionary<string, Stored> stored, IReadOnlyDictionary<string, Dictionary<string, string>> incoming)
    {
        int active = 0, terminations = 0, moves = 0;
        foreach (var s in stored.Values.Where(s => s.State == HrMatchStates.Matched && F(s.Data, HrFields.Status) == HrEmployeeStatuses.Active))
        {
            active++;
            if (!incoming.TryGetValue(s.RecordId, out var next))
            {
                continue;
            }
            if (F(next, HrFields.Status) == HrEmployeeStatuses.Terminated)
            {
                terminations++;
            }
            else if (F(s.Data, HrFields.OrgUnitCode) is { } before && F(next, HrFields.OrgUnitCode) != before)
            {
                moves++;
            }
        }
        return (active, terminations, moves);
    }

    private async Task<Dictionary<string, Stored>> StoredAsync(CancellationToken ct) =>
        (await session.QueryAsync("select record_id, data::text, user_id, match_state, match_rule from integrations.hr_employee", null,
            r => new Stored(r.GetString(0), Fields(r.GetString(1)), r.Get<Guid?>("user_id"), r.GetString(3), r.Get<string>("match_rule")), ct))
        .ToDictionary(s => s.RecordId, StringComparer.Ordinal);

    private async Task ApplyAsync(Guid runId, Guid connectionId, HrSettingsView settings, IReadOnlyDictionary<string, Stored> stored,
        IReadOnlyDictionary<string, Dictionary<string, string>> incoming, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var accounts = await users.ListAllAsync(ct);
        var byEmail = accounts.Where(a => !a.IsTest && a.Email.Length > 0).GroupBy(a => a.Email.ToLowerInvariant(), StringComparer.Ordinal)
            .Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Id, StringComparer.Ordinal);
        var known = accounts.Select(a => a.Id).ToHashSet();
        var numbers = await employeeNumbers.ResolveAsync(
            incoming.Values.Select(d => F(d, HrFields.EmployeeNumber)).OfType<string>().ToList(), ct);

        // Two HR records with one work email or employee number are never matched automatically (US1 scenario 2).
        var emailCount = incoming.Values.Select(d => F(d, HrFields.WorkEmail)).OfType<string>().GroupBy(e => e, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var numberCount = incoming.Values.Select(d => F(d, HrFields.EmployeeNumber)).OfType<string>().GroupBy(e => e, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        // Links already made stay (scenario 3): manual ones always, automatic ones while the user exists.
        var taken = stored.Values.Where(s => s.UserId is { } u && known.Contains(u)).ToDictionary(s => s.UserId!.Value, s => s.RecordId);
        // Lifecycle (US2 to US4): the first run of a tenant is the baseline; later runs are compared with what was applied.
        var baseline = stored.Count == 0;
        var (open, left) = await lifecycle.StateAsync(ct);
        var at = await lifecycle.ClockAsync(ct);
        var plans = new List<(string, Guid?, HrRecordPlan)>();
        var rows = new List<object>();
        int matched = 0, unmatched = 0, conflicts = 0, changed = 0;
        foreach (var (id, read) in incoming.OrderBy(i => i.Key, StringComparer.Ordinal))
        {
            stored.TryGetValue(id, out var before);
            var plan = HrLifecycle.Plan(before?.Data, read, baseline, left.Contains(id), open.TryGetValue(id, out var o) ? o : [], at);
            var data = plan.Data;
            Guid? user = null;
            string state, rule;
            if (before is { UserId: { } linked } && known.Contains(linked))
            {
                (user, state, rule) = (linked, HrMatchStates.Matched, before.Rule ?? HrMatchRules.Manual);
            }
            else if (before is { Rule: HrMatchRules.Manual })
            {
                // An administrator unlinked it: it stays unlinked until someone links it again.
                (state, rule) = (HrMatchStates.Unmatched, HrMatchRules.Manual);
            }
            else
            {
                (user, state, rule) = Match(data, settings.MatchRule, byEmail, numbers, emailCount, numberCount, taken, id);
            }
            if (user is { } u)
            {
                taken[u] = id;
            }
            switch (state)
            {
                case HrMatchStates.Matched: matched++; break;
                case HrMatchStates.Conflict: conflicts++; break;
                default: unmatched++; break;
            }
            if (before is null || !Same(before.Data, data) || before.UserId != user)
            {
                changed++;
            }
            rows.Add(new { id, data, user, state, rule = rule.Length == 0 ? null : rule });
            if (plan.Add.Count > 0 || plan.Cancel.Count > 0)
            {
                plans.Add((id, user, plan));
            }
        }
        await session.ExecAsync(
            """
            insert into integrations.hr_employee (tenant_id, record_id, connection_id, data, user_id, match_state, match_rule, first_seen_at, last_seen_at, last_run_id)
            select platform.current_tenant_id(), r.id, @connectionId, r.data, r."user", r.state, r.rule, @now, @now, @runId
            from jsonb_to_recordset(@rows) as r(id text, data jsonb, "user" uuid, state text, rule text)
            on conflict (tenant_id, record_id) do update set connection_id = excluded.connection_id, data = excluded.data, user_id = excluded.user_id,
              match_state = excluded.match_state, match_rule = excluded.match_rule,
              linked_by = case when excluded.match_rule = 'manual' then integrations.hr_employee.linked_by end,
              last_seen_at = excluded.last_seen_at, last_run_id = excluded.last_run_id
            """, new { connectionId, now, runId, rows = Jsonb.From(rows) }, ct);
        await session.ExecAsync(
            """
            update integrations.hr_run set finished_at = @now, total = @total, matched = @matched, unmatched = @unmatched, conflicts = @conflicts, changed = @changed
            where id = @runId
            """, new { runId, now, total = incoming.Count, matched, unmatched, conflicts, changed }, ct);
        var codes = incoming.Values.Select(d => F(d, HrFields.OrgUnitCode)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        await lifecycle.SaveAsync(runId, plans, codes, ct);
        await outbox.EnqueueAsync(new HrSyncCompleted(connectionId, runId, matched, changed), ct);
        await lifecycle.ApplyDueAsync(ct);
    }

    private static (Guid? User, string State, string Rule) Match(IReadOnlyDictionary<string, string> data, IReadOnlyList<string> order,
        IReadOnlyDictionary<string, Guid> byEmail, IReadOnlyDictionary<string, Guid> byNumber, IReadOnlyDictionary<string, int> emailCount,
        IReadOnlyDictionary<string, int> numberCount, IReadOnlyDictionary<Guid, string> taken, string recordId)
    {
        foreach (var rule in order)
        {
            var (key, counts, lookup) = rule == HrMatchRules.WorkEmail
                ? (F(data, HrFields.WorkEmail), emailCount, byEmail)
                : (F(data, HrFields.EmployeeNumber), numberCount, byNumber);
            if (key is null)
            {
                continue;
            }
            if (counts.GetValueOrDefault(key) > 1)
            {
                return (null, HrMatchStates.Conflict, "");
            }
            if (!lookup.TryGetValue(key, out var user))
            {
                continue;
            }
            // The user already belongs to another HR record: a person is matched once.
            return taken.TryGetValue(user, out var other) && other != recordId ? (null, HrMatchStates.Conflict, "") : (user, HrMatchStates.Matched, rule);
        }
        return (null, HrMatchStates.Unmatched, "");
    }

    private static bool Same(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    /// <summary>Applies a held run (FR-004) after an HR administrator has looked at it.</summary>
    public async Task<HrRunView> ConfirmAsync(Guid runId, CancellationToken ct)
    {
        await RequireAsync(ct);
        var run = await HeldAsync(runId, ct);
        var settings = await SettingsAsync(ct);
        var incoming = (await session.QueryAsync("select record_id, data::text from integrations.hr_incoming where run_id = @runId", new { runId },
            r => (Id: r.GetString(0), Data: Fields(r.GetString(1))), ct)).ToDictionary(r => r.Id, r => r.Data, StringComparer.Ordinal);
        await ApplyAsync(runId, run.ConnectionId, settings, await StoredAsync(ct), incoming, ct);
        await session.ExecAsync("delete from integrations.hr_incoming where run_id = @runId", new { runId }, ct);
        var user = context.RequireUser();
        await session.ExecAsync("update integrations.hr_run set status = 'confirmed', decided_by = @user, decided_at = @now where id = @runId",
            new { runId, user, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrSyncConfirmed, "hr_run", runId.ToString(),
            Changes: new { decision = "confirmed", run.Terminations, run.Moves }), ct);
        return (await RunViewAsync(runId, ct))!;
    }

    /// <summary>Drops a held run: nothing of it is applied.</summary>
    public async Task<HrRunView> DiscardAsync(Guid runId, CancellationToken ct)
    {
        await RequireAsync(ct);
        var run = await HeldAsync(runId, ct);
        await session.ExecAsync("delete from integrations.hr_incoming where run_id = @runId", new { runId }, ct);
        await session.ExecAsync("update integrations.hr_run set status = 'discarded', decided_by = @user, decided_at = @now where id = @runId",
            new { runId, user = context.RequireUser(), now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrSyncConfirmed, "hr_run", runId.ToString(),
            Changes: new { decision = "discarded", run.Terminations, run.Moves }), ct);
        return (await RunViewAsync(runId, ct))!;
    }

    private async Task<HrRunView> HeldAsync(Guid runId, CancellationToken ct)
    {
        var run = await RunViewAsync(runId, ct) ?? throw AppException.NotFound();
        if (run.Status != HrRunStatuses.Held)
        {
            throw AppException.Conflict("conflict.hr.run_not_held");
        }
        return run;
    }

    // ---- links ----

    public async Task<HrRecordRow> LinkAsync(string recordId, Guid userId, CancellationToken ct)
    {
        await RequireAsync(ct);
        var row = await RecordAsync(recordId, ct) ?? throw AppException.NotFound();
        if (await users.GetAsync(userId, ct) is not { IsTest: false })
        {
            throw AppException.Validation("userId", "validation.hr.user_unknown");
        }
        var other = await session.ScalarAsync<string>("select record_id from integrations.hr_employee where user_id = @userId and record_id <> @recordId",
            new { userId, recordId }, ct);
        if (other is not null)
        {
            throw AppException.Validation("userId", "validation.hr.user_linked");
        }
        var actor = context.RequireUser();
        await session.ExecAsync(
            "update integrations.hr_employee set user_id = @userId, match_state = 'matched', match_rule = 'manual', linked_by = @actor where record_id = @recordId",
            new { userId, recordId, actor }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrMatchLinked, "hr_record", recordId, Changes: new { user = userId, previous = row.UserId }), ct);
        await ChangedOneAsync(recordId, row.LastRunId, ct);
        return (await RecordAsync(recordId, ct))!;
    }

    public async Task<HrRecordRow> UnlinkAsync(string recordId, CancellationToken ct)
    {
        await RequireAsync(ct);
        var row = await RecordAsync(recordId, ct) ?? throw AppException.NotFound();
        if (row.UserId is null)
        {
            throw AppException.Conflict("conflict.hr.not_linked");
        }
        await session.ExecAsync(
            "update integrations.hr_employee set user_id = null, match_state = 'unmatched', match_rule = 'manual', linked_by = @actor where record_id = @recordId",
            new { recordId, actor = context.RequireUser() }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.HrMatchUnlinked, "hr_record", recordId, Changes: new { user = row.UserId }), ct);
        await ChangedOneAsync(recordId, row.LastRunId, ct);
        return (await RecordAsync(recordId, ct))!;
    }

    /// <summary>A link change is applied by the data owners like a run that changed one record.</summary>
    private async Task ChangedOneAsync(string recordId, Guid runId, CancellationToken ct)
    {
        var connection = await session.ScalarAsync<Guid>("select connection_id from integrations.hr_employee where record_id = @recordId", new { recordId }, ct);
        await outbox.EnqueueAsync(new HrSyncCompleted(connection, runId, 0, 1), ct);
    }

    // ---- views ----

    private const string RunColumns =
        "id, connection_id, status, started_at, finished_at, total, matched, unmatched, conflicts, refused, changed, terminations, moves, active_before, problems::text, decided_by, decided_at";

    private static HrRunView MapRun(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetGuid(1), r.GetString(2), r.GetFieldValue<DateTimeOffset>(3), r.Get<DateTimeOffset?>("finished_at"), r.GetInt32(5), r.GetInt32(6),
        r.GetInt32(7), r.GetInt32(8), r.GetInt32(9), r.GetInt32(10), r.GetInt32(11), r.GetInt32(12), r.GetInt32(13),
        JsonSerializer.Deserialize<List<HrProblem>>(r.GetString(14), JsonDefaults.Options) ?? [], r.Get<Guid?>("decided_by"), r.Get<DateTimeOffset?>("decided_at"));

    private Task<HrRunView?> RunViewAsync(Guid runId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {RunColumns} from integrations.hr_run where id = @runId", new { runId }, MapRun, ct);

    public async Task<IReadOnlyList<HrRunView>> RunsAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        return await session.QueryAsync($"select {RunColumns} from integrations.hr_run order by started_at desc limit 50", null, MapRun, ct);
    }

    public async Task<HrOverview> OverviewAsync(CancellationToken ct)
    {
        await RequireAsync(ct);
        var settings = await SettingsAsync(ct);
        var connection = await session.QuerySingleOrDefaultAsync(
            "select id, kind, display_name, enabled from integrations.connection where kind = any(@kinds) order by created_at limit 1",
            new { kinds = PackKinds.ToArray() }, r => new HrConnectionSummary(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetBoolean(3)), ct);
        var staleBefore = clock.UtcNow.AddDays(-RetryDays);
        var counts = (await session.QuerySingleOrDefaultAsync(
            """
            select count(*) filter (where match_state = 'matched'), count(*) filter (where match_state = 'unmatched' and first_seen_at >= @staleBefore),
                   count(*) filter (where match_state = 'unmatched' and first_seen_at < @staleBefore), count(*) filter (where match_state = 'conflict')
            from integrations.hr_employee
            """, new { staleBefore }, r => new HrCounts((int)r.GetInt64(0), (int)r.GetInt64(1), (int)r.GetInt64(2), (int)r.GetInt64(3)), ct))!;
        var last = await session.QuerySingleOrDefaultAsync(
            $"select {RunColumns} from integrations.hr_run where status <> 'superseded' order by started_at desc limit 1", null, MapRun, ct);
        var held = await session.QuerySingleOrDefaultAsync($"select {RunColumns} from integrations.hr_run where status = 'held'", null, MapRun, ct);
        return new HrOverview(settings, connection, counts, last, held);
    }

    private HrRecordRow MapRecord(Npgsql.NpgsqlDataReader r)
    {
        var state = r.GetString(5);
        var first = r.GetFieldValue<DateTimeOffset>(8);
        if (state == HrMatchStates.Unmatched && first < clock.UtcNow.AddDays(-RetryDays))
        {
            state = HrMatchStates.Stale;
        }
        return new HrRecordRow(r.GetString(0), r.Get<string>("employee_number"), r.Get<string>("work_email"), r.Get<string>("name_en"), r.Get<string>("name_ar"),
            state, r.Get<string>("match_rule"), r.Get<Guid?>("user_id"), null, first, r.GetFieldValue<DateTimeOffset>(9), r.GetGuid(10));
    }

    private Task<HrRecordRow?> RecordAsync(string recordId, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync(
            $"select {Aliased} from integrations.hr_employee where record_id = @recordId",
            new { recordId }, MapRecord, ct);

    public async Task<HrRecordPage> RecordsAsync(string? state, string? search, string? cursor, int limit, CancellationToken ct)
    {
        await RequireAsync(ct);
        if (state is not null && !HrMatchStates.Listed.Contains(state))
        {
            throw AppException.Validation("state", "validation.hr.state");
        }
        var staleBefore = clock.UtcNow.AddDays(-RetryDays);
        var text = string.IsNullOrWhiteSpace(search) ? null : search.Trim().ToLowerInvariant();
        var size = Math.Clamp(limit, 1, 100);
        var rows = await session.QueryAsync(
            $"""
            select {Aliased} from integrations.hr_employee
            where (@state::text is null
                   or (@state = 'unmatched' and match_state = 'unmatched' and first_seen_at >= @staleBefore)
                   or (@state = 'stale' and match_state = 'unmatched' and first_seen_at < @staleBefore)
                   or (@state not in ('unmatched', 'stale') and match_state = @state))
              and (@text::text is null or lower(record_id) = @text or lower(coalesce(data->>'employee_number', '')) = @text
                   or position(@text in lower(coalesce(data->>'work_email', ''))) > 0 or position(@text in lower(coalesce(data->>'name_en', ''))) > 0
                   or position(@text in coalesce(data->>'name_ar', '')) > 0)
              and (@cursor::text is null or record_id > @cursor)
            order by record_id limit @take
            """, new { state, staleBefore, text, cursor, take = size + 1 }, MapRecord, ct);
        var page = rows.Take(size).ToList();
        if (page.Count > 0)
        {
            var names = await users.GetManyAsync(page.Where(p => p.UserId is not null).Select(p => p.UserId!.Value).ToList(), ct);
            page = page.Select(p => p.UserId is { } u && names.TryGetValue(u, out var info) ? p with { UserName = info.DisplayName } : p).ToList();
        }
        return new HrRecordPage(page, rows.Count > size ? page[^1].RecordId : null);
    }

    private static readonly string Aliased =
        "record_id, data->>'employee_number' as employee_number, data->>'work_email' as work_email, data->>'name_en' as name_en, data->>'name_ar' as name_ar, " +
        "match_state, match_rule, user_id, first_seen_at, last_seen_at, last_run_id";

    // ---- contract for other modules ----

    public async Task<IReadOnlyList<HrRecordView>> MatchedAsync(IReadOnlyCollection<Guid>? userIds = null, CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrIntegration, ct))
        {
            return [];
        }
        return await session.QueryAsync(
            """
            select user_id, record_id, data::text, last_seen_at from integrations.hr_employee
            where match_state = 'matched' and user_id is not null and (@ids::uuid[] is null or user_id = any(@ids))
            """, new { ids = userIds?.Distinct().ToArray() },
            r => new HrRecordView(r.GetGuid(0), r.GetString(1), Visible(Fields(r.GetString(2))), r.GetFieldValue<DateTimeOffset>(3)), ct);
    }

    /// <summary>The fields other modules may see with a person's record: all but <see cref="HrFields.Private"/>.</summary>
    public static Dictionary<string, string> Visible(Dictionary<string, string> fields)
    {
        foreach (var key in HrFields.Private)
        {
            fields.Remove(key);
        }
        return fields;
    }

    public async Task<IReadOnlyList<string>> MappedFieldsAsync(CancellationToken ct = default)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.HrIntegration, ct))
        {
            return [];
        }
        var settings = await session.QuerySingleOrDefaultAsync(
            "select settings->'fields' from integrations.connection where kind = any(@kinds) and enabled order by created_at limit 1",
            new { kinds = PackKinds.ToArray() }, r => r.IsDBNull(0) ? null : r.GetString(0), ct);
        if (settings is null)
        {
            return [];
        }
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(settings) ?? new Dictionary<string, string>();
        return HrFields.All.Where(fields.ContainsKey).ToList();
    }

    /// <summary>Run results after 90 days and the copies of people who left more than 30 days ago (section 8).</summary>
    public async Task<int> PurgeAsync(DateTimeOffset now, CancellationToken ct)
    {
        var runs = await session.ExecAsync("delete from integrations.hr_run where started_at < @before and status <> 'held'",
            new { before = now.AddDays(-RunRetentionDays) }, ct);
        var leavers = await session.ExecAsync(
            """
            delete from integrations.hr_employee
            where data->>'status' = 'terminated' and (data->>'termination_date') is not null and (data->>'termination_date')::date < @before
            """, new { before = DateOnly.FromDateTime(now.UtcDateTime).AddDays(-KeepAfterTerminationDays) }, ct);
        return runs + leavers;
    }
}
