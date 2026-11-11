using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Hr.Journeys;

public static class JourneyKinds
{
    public const string Onboarding = "onboarding";
    public const string Offboarding = "offboarding";
    public const string Transfer = "transfer";
    public const string Promotion = "promotion";

    public static readonly IReadOnlyList<string> All = [Onboarding, Offboarding, Transfer, Promotion];
}

/// <summary>
/// What starts a journey of a template (spec 022 FR-014). <c>hire</c>: an HR new hire or re-hire (spec 019), or a new
/// account whose HR hire date is recent; <c>account</c>: any new account (spec 003 <c>UserProvisioned</c>), anchored on
/// the HR hire date or today; <c>termination</c>: an HR termination; <c>service</c>: an approved request of a service,
/// anchored on a date field of its form; <c>manual</c>: an HR officer starts it.
/// </summary>
public static class JourneyStarts
{
    public const string Hire = "hire";
    public const string Account = "account";
    public const string Termination = "termination";
    public const string Service = "service";
    public const string Manual = "manual";

    public static readonly IReadOnlyList<string> All = [Hire, Account, Termination, Service, Manual];
}

/// <summary>Who does a step: the person, their buddy, their line manager, named people, or holders of a permission at their unit (a queue).</summary>
public static class JourneyAssignees
{
    public const string Person = "person";
    public const string Buddy = "buddy";
    public const string Manager = "manager";
    public const string Users = "users";
    public const string Permission = "permission";

    public static readonly IReadOnlyList<string> All = [Person, Buddy, Manager, Users, Permission];
}

/// <summary>
/// One step of a template: done <paramref name="OffsetDays"/> calendar days from the anchor date (negative is before).
/// <paramref name="Permission"/> names the queue of a permission step, and for person, buddy and manager steps the
/// fallback when that person is missing or has left (<c>hr.manage</c> when not given).
/// </summary>
public sealed record JourneyStepSpec(
    string Key,
    IReadOnlyDictionary<string, string> Names,
    int OffsetDays,
    string Assignee,
    IReadOnlyList<Guid>? Users = null,
    string? Permission = null,
    int? DueWorkingDays = null);

public sealed record JourneyTemplateRequest(
    string? Key,
    string? Kind,
    IReadOnlyDictionary<string, string>? Names,
    Guid? OrgUnitId,
    string? StartOn,
    Guid? ServiceId,
    string? AnchorField,
    IReadOnlyList<JourneyStepSpec>? Steps,
    bool? Active);

public sealed record JourneyTemplateView(
    Guid Id,
    string Key,
    string Kind,
    IReadOnlyDictionary<string, string> Names,
    Guid? OrgUnitId,
    string StartOn,
    Guid? ServiceId,
    string? AnchorField,
    IReadOnlyList<JourneyStepSpec> Steps,
    bool Active,
    int Version,
    DateTimeOffset UpdatedAt,
    string ETag);

internal sealed record JourneyTemplateRow(
    Guid Id, string Key, string Kind, Dictionary<string, string> Names, Guid? OrgUnitId, string StartOn, Guid? ServiceId, string? AnchorField,
    List<JourneyStepSpec> Steps, bool Active, int Version, DateTimeOffset UpdatedAt)
{
    public JourneyTemplateView View() =>
        new(Id, Key, Kind, Names, OrgUnitId, StartOn, ServiceId, AnchorField, Steps, Active, Version, UpdatedAt, JourneyTemplates.ETag(Version));
}

/// <summary>
/// Journey templates (spec 022 FR-013, US4, US5): steps relative to an anchor date, versioned, schema validated and
/// audited. Each version publishes one single step route per step on spec 024, so a journey keeps the routes of the
/// version it started with. HR officers manage templates of the units they cover; a template without a unit needs
/// tenant-wide <c>hr.manage</c>. Needs <c>hr.journeys</c> in the plan.
/// </summary>
public sealed partial class JourneyTemplates(
    IDbSession session,
    ITenantContext context,
    IClock clock,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IAuditWriter audit,
    IOrganizationHierarchy hierarchy,
    ITenantMemberships memberships,
    IProcessEngine engine)
{
    public const int MaxTemplates = 100;
    public const int MaxSteps = 50;
    public const int MaxUsersPerStep = 20;
    public const int MinOffset = -60;
    public const int MaxOffset = 365;
    public const int MaxDueWorkingDays = 60;

    public static string ETag(int version) => $"\"v{version}\"";

    /// <summary>The engine key of a step's route: <c>hr.journey.{template}.{version}.{step}</c>.</summary>
    public static string ProcessKey(string template, int version, string step) => $"{ProcessPrefix}{template}.{version}.{step}";

    public const string ProcessPrefix = "hr.journey.";

    private const string Columns = "id, key, kind, names::text, org_unit_id, start_on, service_id, anchor_field, steps::text, active, version, updated_at";

    private static JourneyTemplateRow Read(Npgsql.NpgsqlDataReader r) => new(
        r.GetGuid(0), r.GetString(1), r.GetString(2), JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3)) ?? [], r.Get<Guid?>("org_unit_id"),
        r.GetString(5), r.Get<Guid?>("service_id"), r.Get<string?>("anchor_field"),
        JsonSerializer.Deserialize<List<JourneyStepSpec>>(r.GetString(8), JsonDefaults.Options) ?? [], r.GetBoolean(9), r.GetInt32(10),
        r.GetFieldValue<DateTimeOffset>(11));

    internal Task<List<JourneyTemplateRow>> RowsAsync(CancellationToken ct) =>
        session.QueryAsync($"select {Columns} from hr.journey_template order by key", null, Read, ct);

    internal async Task<JourneyTemplateRow?> RowAsync(Guid id, CancellationToken ct) =>
        (await session.QueryAsync($"select {Columns} from hr.journey_template where id = @id", new { id }, Read, ct)).SingleOrDefault();

    internal async Task<JourneyTemplateRow?> RowByKeyAsync(string key, CancellationToken ct) =>
        (await session.QueryAsync($"select {Columns} from hr.journey_template where key = @key", new { key }, Read, ct)).SingleOrDefault();

    /// <summary>
    /// The active template a start applies to for a person whose primary unit has this path from the root: the one at
    /// the nearest unit, else the tenant-wide one (a template applies to its unit's whole subtree).
    /// </summary>
    internal async Task<JourneyTemplateRow?> ApplicableAsync(string startOn, Guid? serviceId, IReadOnlyList<Guid> pathFromRoot, CancellationToken ct)
    {
        var candidates = (await RowsAsync(ct)).Where(t => t.Active && t.StartOn == startOn && t.ServiceId == serviceId).ToList();
        return pathFromRoot.Reverse().Select(u => candidates.FirstOrDefault(t => t.OrgUnitId == u)).FirstOrDefault(t => t is not null)
            ?? candidates.FirstOrDefault(t => t.OrgUnitId is null);
    }

    /// <summary>The nearest active offboarding template a no-show starts (edge cases): one started by termination, else a manual one.</summary>
    internal async Task<JourneyTemplateRow?> OffboardingForAsync(IReadOnlyList<Guid> pathFromRoot, CancellationToken ct)
    {
        var all = (await RowsAsync(ct)).Where(t => t.Active && t.Kind == JourneyKinds.Offboarding).ToList();
        foreach (var start in new[] { JourneyStarts.Termination, JourneyStarts.Manual })
        {
            var matching = all.Where(t => t.StartOn == start).ToList();
            var found = pathFromRoot.Reverse().Select(u => matching.FirstOrDefault(t => t.OrgUnitId == u)).FirstOrDefault(t => t is not null)
                ?? matching.FirstOrDefault(t => t.OrgUnitId is null);
            if (found is not null)
            {
                return found;
            }
        }
        return null;
    }

    private async Task<OrgCoverage> CoverageAsync(CancellationToken ct)
    {
        var coverage = await authz.CoverageAsync(Permissions.HrManage, ct);
        return coverage.Any ? coverage : throw AppException.Denied();
    }

    public async Task<IReadOnlyList<JourneyTemplateView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        var coverage = await CoverageAsync(ct);
        return (await RowsAsync(ct)).Where(t => t.OrgUnitId is null || coverage.Covers(t.OrgUnitId)).Select(t => t.View()).ToList();
    }

    public async Task<JourneyTemplateView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        var coverage = await CoverageAsync(ct);
        var row = await RowAsync(id, ct);
        return row is not null && (row.OrgUnitId is null || coverage.Covers(row.OrgUnitId)) ? row.View() : throw AppException.NotFound();
    }

    public async Task<JourneyTemplateView> CreateAsync(JourneyTemplateRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        await DemandUnitAsync(request.OrgUnitId, ct);
        var valid = await ValidateAsync(request, ct);
        if (await RowByKeyAsync(valid.Key, ct) is not null)
        {
            throw AppException.Conflict("conflict.journey.template_exists");
        }
        if (await session.ScalarAsync<long>("select count(*) from hr.journey_template", null, ct) >= MaxTemplates)
        {
            throw AppException.Conflict("conflict.journey.too_many_templates");
        }
        await DemandTriggerFreeAsync(valid, null, ct);
        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into hr.journey_template (tenant_id, id, key, kind, names, org_unit_id, start_on, service_id, anchor_field, steps, active, version, created_at, updated_at, updated_by)
            values (platform.current_tenant_id(), @id, @key, @kind, @names, @unit, @startOn, @service, @anchor, @steps, @active, 1, @now, @now, @actor)
            """, new
            {
                id, key = valid.Key, kind = valid.Kind, names = Jsonb.From(valid.Names), unit = request.OrgUnitId, startOn = valid.StartOn, service = valid.ServiceId,
                anchor = valid.AnchorField, steps = Jsonb.From(valid.Steps), active = request.Active ?? true, now, actor = context.ActorId,
            }, ct);
        await PublishAsync(valid.Key, 1, valid.Steps, ct);
        var row = (await RowAsync(id, ct))!;
        await ChangedAsync(row, "created", ct);
        return row.View();
    }

    /// <summary>Replaces a template with <c>If-Match</c>. The key and unit stay; a template is never deleted, it is made inactive.</summary>
    public async Task<JourneyTemplateView> UpdateAsync(Guid id, string? ifMatch, JourneyTemplateRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.HrJourneys, ct);
        var coverage = await CoverageAsync(ct);
        var row = await RowAsync(id, ct);
        if (row is null || !(row.OrgUnitId is null || coverage.Covers(row.OrgUnitId)))
        {
            throw AppException.NotFound();
        }
        await DemandUnitAsync(row.OrgUnitId, ct);
        if (ifMatch != ETag(row.Version))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.journey.changed", null, new Dictionary<string, object?> { ["etag"] = ETag(row.Version) });
        }
        var valid = await ValidateAsync(request with { Key = row.Key, OrgUnitId = row.OrgUnitId }, ct);
        var active = request.Active ?? row.Active;
        if (active)
        {
            await DemandTriggerFreeAsync(valid, row.Id, ct);
        }
        var version = row.Version + 1;
        await session.ExecAsync(
            """
            update hr.journey_template set kind = @kind, names = @names, start_on = @startOn, service_id = @service, anchor_field = @anchor, steps = @steps,
              active = @active, version = @version, updated_at = @now, updated_by = @actor
            where id = @id
            """, new
            {
                id, kind = valid.Kind, names = Jsonb.From(valid.Names), startOn = valid.StartOn, service = valid.ServiceId, anchor = valid.AnchorField,
                steps = Jsonb.From(valid.Steps), active, version, now = clock.UtcNow, actor = context.ActorId,
            }, ct);
        await PublishAsync(row.Key, version, valid.Steps, ct);
        var updated = (await RowAsync(id, ct))!;
        await ChangedAsync(updated, "updated", ct);
        return updated.View();
    }

    private Task ChangedAsync(JourneyTemplateRow row, string operation, CancellationToken ct) =>
        audit.WriteAsync(new AuditEntry(AuditActions.HrJourneyTemplateChanged, "journey_template", row.Key, TargetOrgUnitId: row.OrgUnitId,
            Changes: new
            {
                operation, row.Kind, row.StartOn, row.ServiceId, row.AnchorField, row.Active, row.Version,
                steps = row.Steps.Select(s => new { s.Key, s.OffsetDays, s.Assignee, s.Permission, users = s.Users?.Count ?? 0, s.DueWorkingDays }),
            }), ct);

    /// <summary>One single step route per step, published under the version so open journeys keep theirs.</summary>
    private async Task PublishAsync(string key, int version, IReadOnlyList<JourneyStepSpec> steps, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            var name = StepName(step.Names);
            await engine.PublishRouteAsync(ProcessKey(key, version, step.Key), RouteSources.JourneyRoute,
                new RouteSpec(name, [new RouteStep(step.Key, name, Candidates(step), step.DueWorkingDays)]), ct);
        }
    }

    internal static string StepName(IReadOnlyDictionary<string, string> names) => names.GetValueOrDefault("en") ?? names.Values.First();

    /// <summary>
    /// The candidates of a step. A person or buddy step goes to the instance's subject (the person, or the buddy when
    /// started for them), a manager step to the subject's line manager; each falls back to the permission.
    /// </summary>
    internal static CandidateRule Candidates(JourneyStepSpec step) => step.Assignee switch
    {
        JourneyAssignees.Person or JourneyAssignees.Buddy => new CandidateRule(Permission: step.Permission ?? Permissions.HrManage, Subject: true),
        JourneyAssignees.Manager => new CandidateRule(Permission: step.Permission ?? Permissions.HrManage, LineManager: true),
        JourneyAssignees.Users => new CandidateRule(Users: step.Users),
        _ => new CandidateRule(Permission: step.Permission),
    };

    private async Task DemandUnitAsync(Guid? unit, CancellationToken ct)
    {
        if (unit is { } u && !await hierarchy.UnitExistsAsync(u, ct))
        {
            throw AppException.Validation("orgUnitId", "validation.journey.unit_unknown");
        }
        await authz.DemandAsync(Permissions.HrManage, unit is { } id ? AuthzTarget.OrgUnit(id) : AuthzTarget.Tenant, ct);
    }

    /// <summary>One active template per start and unit (and service), so an event starts one journey, never two.</summary>
    private async Task DemandTriggerFreeAsync(Valid valid, Guid? self, CancellationToken ct)
    {
        if (valid.StartOn == JourneyStarts.Manual)
        {
            return;
        }
        var unit = self is { } id ? (await RowAsync(id, ct))!.OrgUnitId : valid.OrgUnitId;
        if ((await RowsAsync(ct)).Any(t => t.Id != self && t.Active && t.StartOn == valid.StartOn && t.ServiceId == valid.ServiceId && t.OrgUnitId == unit))
        {
            throw AppException.Conflict("conflict.journey.trigger_taken");
        }
    }

    private sealed record Valid(string Key, string Kind, Dictionary<string, string> Names, Guid? OrgUnitId, string StartOn, Guid? ServiceId, string? AnchorField,
        List<JourneyStepSpec> Steps);

    private async Task<Valid> ValidateAsync(JourneyTemplateRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var key = request.Key ?? string.Empty;
        if (!KeyPattern().IsMatch(key))
        {
            errors.Add("key", "validation.journey.key");
        }
        var kind = request.Kind ?? string.Empty;
        if (!JourneyKinds.All.Contains(kind))
        {
            errors.Add("kind", "validation.journey.kind");
        }
        var names = CleanNames(request.Names);
        if (names is null)
        {
            errors.Add("names", "validation.journey.names");
        }
        var startOn = request.StartOn ?? JourneyStarts.Manual;
        if (!JourneyStarts.All.Contains(startOn))
        {
            errors.Add("startOn", "validation.journey.start_on");
        }
        else if (startOn is JourneyStarts.Hire or JourneyStarts.Account && kind != JourneyKinds.Onboarding
            || startOn is JourneyStarts.Termination or JourneyStarts.Service && kind != JourneyKinds.Offboarding)
        {
            // Hires and new accounts start onboarding; terminations and resignations start offboarding (US4, US5).
            errors.Add("startOn", "validation.journey.start_kind");
        }
        Guid? serviceId = null;
        string? anchorField = null;
        if (startOn == JourneyStarts.Service)
        {
            serviceId = request.ServiceId;
            anchorField = request.AnchorField?.Trim();
            if (serviceId is null || serviceId == Guid.Empty)
            {
                errors.Add("serviceId", "validation.journey.service");
            }
            if (anchorField is null || !FieldPattern().IsMatch(anchorField))
            {
                errors.Add("anchorField", "validation.journey.anchor_field");
            }
        }
        var steps = new List<JourneyStepSpec>();
        var input = request.Steps ?? [];
        if (input.Count is 0 or > MaxSteps)
        {
            errors.Add("steps", "validation.journey.steps");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var people = input.SelectMany(s => s.Users ?? []).Distinct().ToList();
        var members = people.Count == 0 ? new Dictionary<Guid, TenantMembershipInfo>() : await memberships.GetManyAsync(people, ct);
        for (var i = 0; i < Math.Min(input.Count, MaxSteps); i++)
        {
            var s = input[i];
            var field = $"steps[{i}]";
            if (s is null)
            {
                errors.Add(field, "validation.journey.steps");
                continue;
            }
            if (s.Key is null || !KeyPattern().IsMatch(s.Key) || !seen.Add(s.Key))
            {
                errors.Add($"{field}.key", "validation.journey.step_key");
            }
            var stepNames = CleanNames(s.Names);
            if (stepNames is null)
            {
                errors.Add($"{field}.names", "validation.journey.names");
            }
            if (s.OffsetDays is < MinOffset or > MaxOffset)
            {
                errors.Add($"{field}.offsetDays", "validation.journey.offset");
            }
            if (s.DueWorkingDays is < 1 or > MaxDueWorkingDays)
            {
                errors.Add($"{field}.dueWorkingDays", "validation.journey.due");
            }
            if (!JourneyAssignees.All.Contains(s.Assignee ?? string.Empty))
            {
                errors.Add($"{field}.assignee", "validation.journey.assignee");
            }
            var users = (s.Users ?? []).Distinct().ToList();
            if (s.Assignee == JourneyAssignees.Users)
            {
                if (users.Count is 0 or > MaxUsersPerStep || users.Any(u => !members.TryGetValue(u, out var m) || m.Status != MembershipStatus.Active))
                {
                    errors.Add($"{field}.users", "validation.journey.users");
                }
            }
            else if (users.Count > 0)
            {
                errors.Add($"{field}.users", "validation.journey.users");
            }
            var permission = string.IsNullOrWhiteSpace(s.Permission) ? null : s.Permission.Trim();
            if (s.Assignee == JourneyAssignees.Permission && permission is null
                || permission is not null && (s.Assignee == JourneyAssignees.Users || !Permissions.All.Contains(permission)))
            {
                errors.Add($"{field}.permission", "validation.journey.permission");
            }
            if (stepNames is not null && s.Key is not null)
            {
                steps.Add(new JourneyStepSpec(s.Key, stepNames, s.OffsetDays, s.Assignee ?? string.Empty, s.Assignee == JourneyAssignees.Users ? users : null,
                    permission, s.DueWorkingDays));
            }
        }
        errors.ThrowIfAny();
        return new Valid(key, kind, names!, request.OrgUnitId, startOn, serviceId, anchorField, steps.OrderBy(s => s.OffsetDays).ToList());
    }

    internal static Dictionary<string, string>? CleanNames(IReadOnlyDictionary<string, string>? names)
    {
        var clean = (names ?? new Dictionary<string, string>()).Where(n => !string.IsNullOrWhiteSpace(n.Value))
            .ToDictionary(n => n.Key, n => n.Value.Trim(), StringComparer.Ordinal);
        return clean.Count == 0 || clean.Any(n => !LocaleKey().IsMatch(n.Key) || n.Value.Length > 120 || n.Value.Any(char.IsControl)) ? null : clean;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]{1,39}$")]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex("^[a-z]{2}$")]
    private static partial Regex LocaleKey();
}
