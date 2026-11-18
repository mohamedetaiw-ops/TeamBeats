using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Notifications.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

public static class AgentAlertRules
{
    public const string DeniedBurst = "denied_burst";
    public const string Injection = "injection";
    public const string CostSpike = "cost_spike";
}

public static class AgentSeverities
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
    public const string Critical = "critical";

    public static readonly IReadOnlyList<string> All = [Low, Medium, High, Critical];

    /// <summary>One level up, for an agent on the watch list.</summary>
    public static string Raise(string severity) => All[Math.Min(All.Count - 1, Math.Max(0, All.ToList().IndexOf(severity)) + 1)];
}

public sealed record AgentAlertView(Guid Id, Guid AgentId, IReadOnlyDictionary<string, string> AgentNames, Guid OrgUnitId, string Rule, string Severity, string Status,
    int Matches, IReadOnlyList<Guid> RunIds, JsonElement Details, Guid? IncidentId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record IncidentAction(DateTimeOffset At, Guid By, string Text);

public sealed record AgentIncidentView(Guid Id, string Title, string Severity, string Status, Guid? AgentId, Guid OrgUnitId, IReadOnlyList<Guid> RunIds,
    IReadOnlyList<IncidentAction> Actions, string? ClosureNote, Guid OpenedBy, DateTimeOffset OpenedAt, Guid? ClosedBy, DateTimeOffset? ClosedAt, int Version,
    IReadOnlyList<Guid> AlertIds);

public sealed record AgentWatchView(Guid AgentId, IReadOnlyDictionary<string, string> AgentNames, Guid OrgUnitId, int Version, string Reason, DateTimeOffset Since,
    DateTimeOffset Until);

public sealed record OpenIncidentRequest(string? Title, string? Severity, Guid? AlertId, Guid? AgentId, IReadOnlyList<Guid>? RunIds);

public sealed record IncidentChangeRequest(int? Version, string? Severity, string? Action, IReadOnlyList<Guid>? RunIds);

public sealed record CloseIncidentRequest(int? Version, string? Note);

public sealed record AgentEvidence(DateTimeOffset ExportedAt, Guid ExportedBy, AgentIncidentView Incident, IReadOnlyList<AgentAlertView> Alerts, IReadOnlyList<RunRecord> Runs);

/// <summary>
/// The Agent Security Center (spec 027 FR-023, US6 scenarios 4 and 5, US9 scenario 3), for tenants whose package has
/// <c>ai.governance.security_center</c>. Anomaly rules run each minute: ten refused tool calls in ten minutes for one
/// agent, an injection detection, and an hour of model tokens at least five times the agent's hourly average over the
/// week before (and at least 50,000). A match joins the agent's open alert for the same rule while it is recent, so a
/// burst is one alert; a new alert goes to the agent's owner and the holders of <c>agent.security_view</c> over the
/// agent's unit, and names only the rule. Agents on the watch list (High risk, for 30 days after each publication) get
/// alerts one severity higher. A security officer opens an incident from an alert or by hand, records actions and
/// linked runs, closes it with a note, and exports it as evidence; each step is audited. Reading needs
/// <c>agent.security_view</c> at the agent's unit; changing also needs <c>agent.suspend</c> there. Outside a person's
/// scope an alert or incident does not exist.
/// </summary>
public sealed class AgentSecurityCenter(IDbSession session, ITenantContext context, IEntitlementService entitlements, IAuthorizationService authz,
    IAuthorizationProvisioning roles, IOrganizationHierarchy hierarchy, IAuditWriter audit, IClock clock, INotificationSender notifications)
{
    public const int DeniedThreshold = 10;
    public static readonly TimeSpan DeniedWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan InjectionWindow = TimeSpan.FromHours(1);
    public static readonly TimeSpan CostWindow = TimeSpan.FromHours(1);
    public const int CostFactor = 5;
    public const long CostMinimumTokens = 50_000;
    public static readonly TimeSpan WatchPeriod = TimeSpan.FromDays(30);
    public const int MaxTitle = 200;
    public const int MaxText = 2000;
    public const int MaxRuns = 50;

    public const string AlertTemplate = "agents.security_alert";
    public const string WatchTemplate = "agents.high_risk_published";
    public const string Link = "/admin/agents/security";

    private static string AlertLink(Guid id) => $"{Link}/alerts/{id}";

    private async Task<Guid> BeginAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiGovernanceSecurityCenter, ct);
        var user = context.RequireUser();
        if (!await authz.HoldsAnywhereAsync(Permissions.AgentSecurityView, ct))
        {
            throw AppException.Denied();
        }
        return user;
    }

    private async Task<bool> CanViewAsync(Guid unit, CancellationToken ct) =>
        (await authz.CheckAsync(Permissions.AgentSecurityView, AuthzTarget.OrgUnit(unit), ct)).Allowed;

    /// <summary>Changing needs both permissions at the unit; outside the view scope the record does not exist.</summary>
    private async Task DemandChangeAsync(Guid unit, CancellationToken ct)
    {
        if (!await CanViewAsync(unit, ct))
        {
            throw AppException.NotFound();
        }
        await authz.DemandAsync(Permissions.AgentSuspend, AuthzTarget.OrgUnit(unit), ct);
    }

    // ---- Anomaly rules ----

    /// <summary>One pass of the anomaly rules for the current tenant (worker). Returns the alerts raised.</summary>
    public async Task<IReadOnlyList<Guid>> ScanAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiGovernanceSecurityCenter, ct))
        {
            return [];
        }
        var now = clock.UtcNow;
        var raised = new List<Guid>();

        var denied = await session.QueryAsync(
            """
            select r.agent_id, a.org_unit_id, count(*)::int, array_agg(distinct r.id)
            from agents.run_step s join agents.run r on r.id = s.run_id join agents.agent a on a.id = r.agent_id
            where s.kind = 'tool' and s.decision = 'refused' and s.at > @since and r.mode <> 'eval'
            group by r.agent_id, a.org_unit_id having count(*) >= @threshold
            """, new { since = now - DeniedWindow, threshold = DeniedThreshold },
            r => (Agent: r.GetGuid(0), Unit: r.GetGuid(1), Count: r.GetInt32(2), Runs: r.GetFieldValue<Guid[]>(3)), ct);
        foreach (var d in denied)
        {
            if (await MatchAsync(d.Agent, d.Unit, AgentAlertRules.DeniedBurst, AgentSeverities.Medium, d.Count, false, d.Runs, new { refused = d.Count, minutes = DeniedWindow.TotalMinutes },
                DeniedWindow, now, ct) is { } id)
            {
                raised.Add(id);
            }
        }

        var findings = await session.QueryAsync(
            """
            select f.agent_id, a.org_unit_id, count(*)::int, array_agg(distinct f.run_id), array_agg(f.id), array_agg(distinct f.rule)
            from agents.injection_finding f join agents.agent a on a.id = f.agent_id
            where f.alert_id is null group by f.agent_id, a.org_unit_id
            """, null,
            r => (Agent: r.GetGuid(0), Unit: r.GetGuid(1), Count: r.GetInt32(2), Runs: r.GetFieldValue<Guid[]>(3), Ids: r.GetFieldValue<Guid[]>(4), Rules: r.GetFieldValue<string[]>(5)), ct);
        foreach (var f in findings)
        {
            var (alertId, id) = await RaiseOrJoinAsync(f.Agent, f.Unit, AgentAlertRules.Injection, AgentSeverities.High, f.Count, true, f.Runs,
                new { rules = f.Rules }, InjectionWindow, now, ct);
            await session.ExecAsync("update agents.injection_finding set alert_id = @alertId where id = any(@ids)", new { alertId, ids = f.Ids }, ct);
            if (id is { } raisedId)
            {
                raised.Add(raisedId);
            }
        }

        var spikes = await session.QueryAsync(
            """
            with recent as (
              select agent_id, sum(input_tokens + output_tokens)::bigint as tokens, array_agg(id) as runs
              from agents.run where created_at > @hour and mode <> 'eval' group by agent_id
            ), before as (
              select agent_id, sum(input_tokens + output_tokens)::float8 / 168 as hourly
              from agents.run where created_at > @week and created_at <= @hour and mode <> 'eval' group by agent_id
            )
            select r.agent_id, a.org_unit_id, r.tokens, coalesce(b.hourly, 0), r.runs
            from recent r join agents.agent a on a.id = r.agent_id left join before b on b.agent_id = r.agent_id
            where r.tokens >= @minimum and r.tokens >= @factor * coalesce(b.hourly, 0)
            """, new { hour = now - CostWindow, week = now - CostWindow - TimeSpan.FromDays(7), minimum = CostMinimumTokens, factor = CostFactor },
            r => (Agent: r.GetGuid(0), Unit: r.GetGuid(1), Tokens: r.GetInt64(2), Hourly: r.GetDouble(3), Runs: r.GetFieldValue<Guid[]>(4)), ct);
        foreach (var s in spikes)
        {
            if (await MatchAsync(s.Agent, s.Unit, AgentAlertRules.CostSpike, AgentSeverities.Medium, 1, false, s.Runs.Take(MaxRuns).ToArray(),
                new { tokens = s.Tokens, hourlyAverage = Math.Round(s.Hourly) }, CostWindow, now, ct) is { } id)
            {
                raised.Add(id);
            }
        }
        return raised;
    }

    private async Task<Guid?> MatchAsync(Guid agent, Guid unit, string rule, string severity, int matches, bool add, Guid[] runs, object details, TimeSpan window,
        DateTimeOffset now, CancellationToken ct)
    {
        var (_, raised) = await RaiseOrJoinAsync(agent, unit, rule, severity, matches, add, runs, details, window, now, ct);
        return raised;
    }

    /// <summary>
    /// Joins the agent's open alert for the rule while it was touched inside the window, or raises a new one. Returns the
    /// alert either way, and the alert raised when it is new.
    /// </summary>
    private async Task<(Guid AlertId, Guid? Raised)> RaiseOrJoinAsync(Guid agent, Guid unit, string rule, string severity, int matches, bool add, Guid[] runs, object details,
        TimeSpan window, DateTimeOffset now, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(details, AgentPolicyService.Json);
        var joined = await session.QuerySingleOrDefaultAsync(
            """
            update agents.security_alert set
              matches = case when @add then matches + @matches else greatest(matches, @matches) end,
              run_ids = (select coalesce(array_agg(distinct x), '{}') from unnest(run_ids || @runs) x),
              details = @details::jsonb, updated_at = @now
            where id = (select id from agents.security_alert where agent_id = @agent and rule = @rule and status = 'open' and updated_at > @since
                        order by updated_at desc limit 1)
            returning id
            """, new { add, matches, runs, details = json, now, agent, rule, since = now - window }, r => (Guid?)r.GetGuid(0), ct);
        if (joined is { } existing)
        {
            return (existing, null);
        }
        var id = Ids.New();
        if (await WatchedAsync(agent, now, ct))
        {
            severity = AgentSeverities.Raise(severity);
        }
        await session.ExecAsync(
            """
            insert into agents.security_alert (tenant_id, id, agent_id, org_unit_id, rule, severity, status, matches, run_ids, details, created_at, updated_at)
            values (platform.current_tenant_id(), @id, @agent, @unit, @rule, @severity, 'open', @matches, @runs, @details::jsonb, @now, @now)
            """, new { id, agent, unit, rule, severity, matches, runs, details = json, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentAlertRaised, "agent_alert", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { agent, rule, severity, matches }), ct);
        var owner = await session.ScalarAsync<Guid>("select owner_user_id from agents.agent where id = @agent", new { agent }, ct);
        var recipients = (await SecurityHoldersAsync(unit, ct)).Append(owner).Distinct().ToList();
        await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.SecurityNotice, AlertTemplate + "." + rule, AlertLink(id),
            DedupeKey: "agent_alert:" + id.ToString("N"), GroupKey: "agent_alert:" + rule), ct);
        return (id, id);
    }

    private async Task<bool> WatchedAsync(Guid agent, DateTimeOffset now, CancellationToken ct) =>
        await session.ScalarAsync<bool>("select exists (select 1 from agents.agent_watch where agent_id = @agent and until > @now)", new { agent, now }, ct);

    /// <summary>Holders of <c>agent.security_view</c> tenant-wide or at a unit above (or at) the agent's unit.</summary>
    private async Task<IReadOnlyList<Guid>> SecurityHoldersAsync(Guid unit, CancellationToken ct)
    {
        var path = (await hierarchy.GetAncestorsAndSelfAsync(unit, ct)).ToHashSet();
        var tenantWide = await roles.TenantHoldersAsync(Permissions.AgentSecurityView, ct);
        var org = (await roles.OrgHoldersAsync(Permissions.AgentSecurityView, ct))
            .Where(h => h.UnitId == unit || (h.Subtree && path.Contains(h.UnitId)))
            .Select(h => h.UserId);
        return tenantWide.Concat(org).Distinct().ToList();
    }

    // ---- Watch list ----

    /// <summary>
    /// After a High risk version is published (US9 scenario 3): the agent is on the watch list for 30 days from now, and
    /// the security officers over its unit are told. Nothing happens without the Security Center in the package.
    /// </summary>
    public async Task WatchPublishedAsync(Guid agent, Guid unit, int version, CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.AiGovernanceSecurityCenter, ct))
        {
            return;
        }
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into agents.agent_watch (tenant_id, agent_id, version, reason, since, until)
            values (platform.current_tenant_id(), @agent, @version, 'high_risk_published', @now, @until)
            on conflict (tenant_id, agent_id) do update set version = excluded.version, reason = excluded.reason, since = excluded.since, until = excluded.until
            """, new { agent, version, now, until = now + WatchPeriod }, ct);
        var recipients = await SecurityHoldersAsync(unit, ct);
        if (recipients.Count > 0)
        {
            await notifications.SendAsync(new NotificationRequest(recipients, NotificationCategories.SecurityNotice, WatchTemplate, $"{Link}/watch",
                DedupeKey: $"agent_watch:{agent:N}:{version}"), ct);
        }
    }

    public async Task<IReadOnlyList<AgentWatchView>> WatchListAsync(CancellationToken ct)
    {
        await BeginAsync(ct);
        var scope = await authz.CoverageAsync(Permissions.AgentSecurityView, ct);
        return await session.QueryAsync(
            """
            select w.agent_id, coalesce(v.definition->'name', '{}')::text, a.org_unit_id, w.version, w.reason, w.since, w.until
            from agents.agent_watch w join agents.agent a on a.id = w.agent_id
            left join agents.agent_version v on v.agent_id = a.id and v.number = coalesce(a.current_version, w.version)
            where w.until > @now and (@all or a.org_unit_id = any(@units))
            order by w.since desc
            """, new { now = clock.UtcNow, all = scope.WholeTenant, units = scope.Units.ToArray() },
            r => new AgentWatchView(r.GetGuid(0), Names(r.GetString(1)), r.GetGuid(2), r.GetInt32(3), r.GetString(4), r.GetFieldValue<DateTimeOffset>(5),
                r.GetFieldValue<DateTimeOffset>(6)), ct);
    }

    // ---- Alerts ----

    private const string AlertSql =
        """
        select s.id, s.agent_id, coalesce(v.definition->'name', '{}')::text, s.org_unit_id, s.rule, s.severity, s.status, s.matches, s.run_ids, s.details::text,
          s.incident_id, s.created_at, s.updated_at
        from agents.security_alert s join agents.agent a on a.id = s.agent_id
        left join agents.agent_version v on v.agent_id = a.id and v.number = a.current_version
        """;

    private static AgentAlertView ReadAlert(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetGuid(1), Names(r.GetString(2)), r.GetGuid(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt32(7), r.GetFieldValue<Guid[]>(8),
            JsonDocument.Parse(r.GetString(9)).RootElement.Clone(), r.Get<Guid?>("incident_id"), r.GetFieldValue<DateTimeOffset>(11), r.GetFieldValue<DateTimeOffset>(12));

    private static Dictionary<string, string> Names(string json) => JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? [];

    public async Task<IReadOnlyList<AgentAlertView>> AlertsAsync(string? status, Guid? agentId, int limit, CancellationToken ct)
    {
        await BeginAsync(ct);
        var scope = await authz.CoverageAsync(Permissions.AgentSecurityView, ct);
        return await session.QueryAsync(
            $"""
            {AlertSql}
            where (@all or s.org_unit_id = any(@units)) and (@status::text is null or s.status = @status) and (@agentId::uuid is null or s.agent_id = @agentId)
            order by s.updated_at desc, s.id limit @limit
            """, new { all = scope.WholeTenant, units = scope.Units.ToArray(), status, agentId, limit = Math.Clamp(limit, 1, 200) }, ReadAlert, ct);
    }

    public async Task<AgentAlertView> AlertAsync(Guid id, CancellationToken ct)
    {
        await BeginAsync(ct);
        var alert = await FindAlertAsync(id, ct);
        if (alert is null || !await CanViewAsync(alert.OrgUnitId, ct))
        {
            throw AppException.NotFound();
        }
        return alert;
    }

    private async Task<AgentAlertView?> FindAlertAsync(Guid id, CancellationToken ct) =>
        (await session.QueryAsync($"{AlertSql} where s.id = @id", new { id }, ReadAlert, ct)).FirstOrDefault();

    public async Task<AgentAlertView> DismissAsync(Guid id, CancellationToken ct)
    {
        var user = await BeginAsync(ct);
        var alert = await FindAlertAsync(id, ct) ?? throw AppException.NotFound();
        await DemandChangeAsync(alert.OrgUnitId, ct);
        if (alert.Status != "open")
        {
            throw AppException.Conflict("conflict.agent_security.alert_closed");
        }
        await session.ExecAsync("update agents.security_alert set status = 'dismissed', decided_by = @user, updated_at = @now where id = @id",
            new { id, user, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentAlertDismissed, "agent_alert", id.ToString(), TargetOrgUnitId: alert.OrgUnitId,
            Changes: new { agent = alert.AgentId, rule = alert.Rule }), ct);
        return (await FindAlertAsync(id, ct))!;
    }

    // ---- Incidents ----

    private const string IncidentSql =
        """
        select i.id, i.title, i.severity, i.status, i.agent_id, i.org_unit_id, i.run_ids, i.actions::text, i.closure_note, i.opened_by, i.opened_at, i.closed_by,
          i.closed_at, i.version, coalesce((select array_agg(s.id order by s.created_at) from agents.security_alert s where s.incident_id = i.id), '{}')
        from agents.security_incident i
        """;

    private static AgentIncidentView ReadIncident(Npgsql.NpgsqlDataReader r) =>
        new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3), r.Get<Guid?>("agent_id"), r.GetGuid(5), r.GetFieldValue<Guid[]>(6),
            JsonSerializer.Deserialize<List<IncidentAction>>(r.GetString(7), AgentPolicyService.Json) ?? [], r.Get<string?>("closure_note"), r.GetGuid(9),
            r.GetFieldValue<DateTimeOffset>(10), r.Get<Guid?>("closed_by"), r.Get<DateTimeOffset?>("closed_at"), r.GetInt32(13), r.GetFieldValue<Guid[]>(14));

    private async Task<AgentIncidentView?> FindIncidentAsync(Guid id, CancellationToken ct) =>
        (await session.QueryAsync($"{IncidentSql} where i.id = @id", new { id }, ReadIncident, ct)).FirstOrDefault();

    public async Task<IReadOnlyList<AgentIncidentView>> IncidentsAsync(string? status, int limit, CancellationToken ct)
    {
        await BeginAsync(ct);
        var scope = await authz.CoverageAsync(Permissions.AgentSecurityView, ct);
        return await session.QueryAsync(
            $"""
            {IncidentSql}
            where (@all or i.org_unit_id = any(@units)) and (@status::text is null or i.status = @status)
            order by i.opened_at desc, i.id limit @limit
            """, new { all = scope.WholeTenant, units = scope.Units.ToArray(), status, limit = Math.Clamp(limit, 1, 200) }, ReadIncident, ct);
    }

    public async Task<AgentIncidentView> IncidentAsync(Guid id, CancellationToken ct)
    {
        await BeginAsync(ct);
        var incident = await FindIncidentAsync(id, ct);
        if (incident is null || !await CanViewAsync(incident.OrgUnitId, ct))
        {
            throw AppException.NotFound();
        }
        return incident;
    }

    private static string CheckSeverity(string? severity) =>
        severity is { } s && AgentSeverities.All.Contains(s) ? s : throw AppException.Validation("severity", "validation.agent_security.severity");

    private static string? CheckText(string? text, string field, int max, bool required)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return required ? throw AppException.Validation(field, "validation.required") : null;
        }
        return value.Length > max ? throw AppException.Validation(field, "validation.agent_security.text") : value;
    }

    /// <summary>Runs linked to an incident must be runs of the tenant that the officer may see, of the incident's agent when it has one.</summary>
    private async Task<Guid[]> CheckRunsAsync(IReadOnlyList<Guid>? runIds, Guid? agent, CancellationToken ct)
    {
        var ids = (runIds ?? []).Distinct().ToArray();
        if (ids.Length > MaxRuns)
        {
            throw AppException.Validation("runIds", "validation.agent_security.runs");
        }
        if (ids.Length == 0)
        {
            return ids;
        }
        var found = await session.QueryAsync("select id, org_unit_id from agents.run where id = any(@ids) and (@agent::uuid is null or agent_id = @agent)",
            new { ids, agent }, r => (Id: r.GetGuid(0), Unit: r.GetGuid(1)), ct);
        if (found.Count != ids.Length)
        {
            throw AppException.Validation("runIds", "validation.agent_security.run");
        }
        foreach (var unit in found.Select(f => f.Unit).Distinct())
        {
            if (!await CanViewAsync(unit, ct))
            {
                throw AppException.Validation("runIds", "validation.agent_security.run");
            }
        }
        return ids;
    }

    /// <summary>Opens an incident from an alert (which then points at it) or by hand for an agent.</summary>
    public async Task<AgentIncidentView> OpenIncidentAsync(OpenIncidentRequest request, CancellationToken ct)
    {
        var user = await BeginAsync(ct);
        var title = CheckText(request.Title, "title", MaxTitle, true)!;
        AgentAlertView? alert = null;
        Guid agent;
        Guid unit;
        if (request.AlertId is { } alertId)
        {
            alert = await FindAlertAsync(alertId, ct) ?? throw AppException.NotFound();
            await DemandChangeAsync(alert.OrgUnitId, ct);
            if (alert.Status != "open")
            {
                throw AppException.Conflict("conflict.agent_security.alert_closed");
            }
            (agent, unit) = (alert.AgentId, alert.OrgUnitId);
        }
        else if (request.AgentId is { } agentId)
        {
            var found = await session.QuerySingleOrDefaultAsync("select org_unit_id from agents.agent where id = @agentId", new { agentId }, r => (Guid?)r.GetGuid(0), ct)
                ?? throw AppException.Validation("agentId", "validation.agent_security.agent");
            await DemandChangeAsync(found, ct);
            (agent, unit) = (agentId, found);
        }
        else
        {
            throw AppException.Validation("agentId", "validation.agent_security.agent");
        }
        var severity = request.Severity is null && alert is not null ? alert.Severity : CheckSeverity(request.Severity);
        var runs = (await CheckRunsAsync(request.RunIds, agent, ct)).Union(alert?.RunIds ?? []).Take(MaxRuns).ToArray();
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into agents.security_incident (tenant_id, id, title, severity, status, agent_id, org_unit_id, run_ids, actions, opened_by, opened_at)
            values (platform.current_tenant_id(), @id, @title, @severity, 'open', @agent, @unit, @runs, '[]', @user, @now)
            """, new { id, title, severity, agent, unit, runs, user, now }, ct);
        if (alert is not null)
        {
            await session.ExecAsync("update agents.security_alert set status = 'incident', incident_id = @id, decided_by = @user, updated_at = @now where id = @alert",
                new { id, user, now, alert = alert.Id }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentIncidentOpened, "agent_incident", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { agent, severity, alert = alert?.Id, runs = runs.Length }), ct);
        return (await FindIncidentAsync(id, ct))!;
    }

    private async Task<AgentIncidentView> OpenForChangeAsync(Guid id, int? version, CancellationToken ct)
    {
        var incident = await FindIncidentAsync(id, ct) ?? throw AppException.NotFound();
        await DemandChangeAsync(incident.OrgUnitId, ct);
        if (incident.Status != "open")
        {
            throw AppException.Conflict("conflict.agent_security.incident_closed");
        }
        if (version is not { } v || v != incident.Version)
        {
            throw AppException.Conflict("conflict.stale_version");
        }
        return incident;
    }

    /// <summary>Records an action taken, a new severity or more linked runs on an open incident.</summary>
    public async Task<AgentIncidentView> ChangeIncidentAsync(Guid id, IncidentChangeRequest request, CancellationToken ct)
    {
        var user = await BeginAsync(ct);
        var incident = await OpenForChangeAsync(id, request.Version, ct);
        var severity = request.Severity is null ? incident.Severity : CheckSeverity(request.Severity);
        var text = CheckText(request.Action, "action", MaxText, false);
        var added = await CheckRunsAsync(request.RunIds, incident.AgentId, ct);
        var runs = incident.RunIds.Union(added).ToArray();
        if (runs.Length > MaxRuns)
        {
            throw AppException.Validation("runIds", "validation.agent_security.runs");
        }
        var now = clock.UtcNow;
        var actions = incident.Actions.ToList();
        if (text is not null)
        {
            actions.Add(new IncidentAction(now, user, text));
        }
        var changed = await session.ExecAsync(
            """
            update agents.security_incident set severity = @severity, run_ids = @runs, actions = @actions::jsonb, version = version + 1
            where id = @id and version = @version
            """, new { id, severity, runs, actions = JsonSerializer.Serialize(actions, AgentPolicyService.Json), version = incident.Version }, ct);
        if (changed == 0)
        {
            throw AppException.Conflict("conflict.stale_version");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentIncidentUpdated, "agent_incident", id.ToString(), TargetOrgUnitId: incident.OrgUnitId,
            Changes: new { severity = severity == incident.Severity ? null : new { from = incident.Severity, to = severity }, action = text is not null, runsAdded = runs.Length - incident.RunIds.Count }), ct);
        return (await FindIncidentAsync(id, ct))!;
    }

    public async Task<AgentIncidentView> CloseIncidentAsync(Guid id, CloseIncidentRequest request, CancellationToken ct)
    {
        var user = await BeginAsync(ct);
        var incident = await OpenForChangeAsync(id, request.Version, ct);
        var note = CheckText(request.Note, "note", MaxText, true)!;
        var now = clock.UtcNow;
        var changed = await session.ExecAsync(
            """
            update agents.security_incident set status = 'closed', closure_note = @note, closed_by = @user, closed_at = @now, version = version + 1
            where id = @id and version = @version
            """, new { id, note, user, now, version = incident.Version }, ct);
        if (changed == 0)
        {
            throw AppException.Conflict("conflict.stale_version");
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentIncidentClosed, "agent_incident", id.ToString(), TargetOrgUnitId: incident.OrgUnitId,
            Changes: new { severity = incident.Severity }), ct);
        return (await FindIncidentAsync(id, ct))!;
    }

    /// <summary>
    /// The incident as evidence (US6 scenario 5): the incident with its actions and closure note, its alerts, and the
    /// execution record of each linked run with its timeline (parameters masked as stored). Audited.
    /// </summary>
    public async Task<AgentEvidence> EvidenceAsync(Guid id, AgentRuns runs, CancellationToken ct)
    {
        var user = await BeginAsync(ct);
        var incident = await IncidentAsync(id, ct);
        var alerts = await session.QueryAsync($"{AlertSql} where s.incident_id = @id order by s.created_at", new { id }, ReadAlert, ct);
        var records = new List<RunRecord>();
        foreach (var runId in incident.RunIds)
        {
            records.Add(await runs.RecordAsync(runId, ct));
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentEvidenceExported, "agent_incident", id.ToString(), TargetOrgUnitId: incident.OrgUnitId,
            Changes: new { runs = records.Count, alerts = alerts.Count }), ct);
        return new AgentEvidence(clock.UtcNow, user, incident, alerts, records);
    }
}
