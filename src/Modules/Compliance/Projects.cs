using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Documents.Contracts;
using TeamBeats.Modules.Identity.Contracts;
using TeamBeats.Modules.Policies.Contracts;
using TeamBeats.Modules.Spaces.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Compliance;

/// <summary>The four control statuses of US7 scenario 3, in order.</summary>
public static class ControlStatuses
{
    public const string NotStarted = "not_started";
    public const string InProgress = "in_progress";
    public const string EvidenceReady = "evidence_ready";
    public const string Reviewed = "reviewed";

    public static readonly IReadOnlyList<string> All = [NotStarted, InProgress, EvidenceReady, Reviewed];
}

public sealed record PersonRef(Guid UserId, string DisplayName);

public sealed record SpaceRef(Guid SpaceId, string Name);

public sealed record ProjectRights(bool CanManage, bool ReadOnly);

public sealed record ProjectSummary(Guid Id, string Name, string FrameworkKey, string FrameworkVersion, IReadOnlyDictionary<string, string> FrameworkNames,
    Guid? OrgUnitId, IReadOnlyDictionary<string, string>? OrgUnitNames, SpaceRef? SharedSpace, string Status, IReadOnlyDictionary<string, int> ByStatus,
    int OverdueTasks, ProjectRights Rights, DateTimeOffset CreatedAt);

public sealed record ControlSummary(Guid Id, string Code, IReadOnlyDictionary<string, string> Titles, PersonRef? Owner, string Status, DateOnly? DueOn,
    bool Overdue, int Tasks, int OpenTasks, int Evidence, int Policies, int Documents, int Version);

public sealed record ProjectView(ProjectSummary Project, IReadOnlyList<ControlSummary> Controls, int Version);

public sealed record LinkView(Guid Id, string? Code, IReadOnlyDictionary<string, string>? Names, bool Available);

public sealed record EvidenceView(Guid Id, string Kind, Guid SourceId, string Format, long? RowCount, long Bytes, string Sha256, PersonRef? AttachedBy,
    DateTimeOffset AttachedAt);

public sealed record TaskView(Guid Id, string Title, PersonRef? Owner, DateOnly? DueOn, DateTimeOffset? DoneAt, bool Overdue, IReadOnlyList<EvidenceView> Evidence,
    bool CanEdit, bool CanAttach);

public sealed record ControlRights(bool CanEdit, bool CanChangeStatus, bool CanReview, bool CanAddTasks);

public sealed record ControlDetail(ControlSummary Control, Guid ProjectId, string ProjectName, string? Note, IReadOnlyList<LinkView> Policies,
    IReadOnlyList<LinkView> Documents, IReadOnlyList<TaskView> Tasks, ControlRights Rights);

public sealed record OverdueTask(Guid TaskId, Guid ControlId, string ControlCode, string Title, PersonRef? Owner, DateOnly DueOn);

public sealed record OwnerReadiness(PersonRef? Owner, IReadOnlyDictionary<string, int> ByStatus, int OverdueTasks);

public sealed record UnitReadiness(Guid? OrgUnitId, IReadOnlyDictionary<string, string>? Names, IReadOnlyDictionary<string, int> ByStatus);

public sealed record ReadinessView(Guid ProjectId, int Total, IReadOnlyDictionary<string, int> ByStatus, int OverdueControls, IReadOnlyList<OverdueTask> OverdueTasks,
    IReadOnlyList<OwnerReadiness> ByOwner, IReadOnlyList<UnitReadiness> ByOrgUnit);

public sealed record ProjectRequest(string? Name, string? FrameworkKey, Guid? OrgUnitId, Guid? OwnerId, DateOnly? DueOn);

public sealed record ProjectChange(string? Name, Guid? OrgUnitId, bool? WholeTenant, string? Status, int? Version);

public sealed record ShareRequest(Guid? SpaceId);

public sealed record ControlChange(Guid? OwnerId, bool? ClearOwner, DateOnly? DueOn, bool? ClearDue, string? Status, IReadOnlyList<Guid>? PolicyIds,
    IReadOnlyList<Guid>? DocumentIds, string? Note, int? Version);

public sealed record TaskRequest(string? Title, Guid? OwnerId, bool? ClearOwner, DateOnly? DueOn, bool? ClearDue, bool? Done);

public sealed record EvidenceRequest(Guid? ExportId);

public sealed record ComplianceRights(bool Enabled, bool CanView, bool CanManage);

internal sealed record ProjectRow(Guid Id, string Name, string FrameworkKey, string FrameworkVersion, Dictionary<string, string> FrameworkNames, Guid? OrgUnitId,
    Guid? SharedSpaceId, string Status, int Version, DateTimeOffset CreatedAt)
{
    public const string Columns = "id, name, framework_key, framework_version, framework_names::text, org_unit_id, shared_space_id, status, version, created_at";

    public static ProjectRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetString(3),
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(4), JsonDefaults.Options)!, r.Get<Guid?>("org_unit_id"), r.Get<Guid?>("shared_space_id"),
        r.GetString(7), r.GetInt32(8), r.GetFieldValue<DateTimeOffset>(9));

    public AuthzTarget Target => OrgUnitId is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant;

    public bool Active => Status == "active";
}

internal sealed record ControlRow(Guid Id, Guid ProjectId, string Code, Dictionary<string, string> Titles, Guid? OwnerId, string Status, DateOnly? DueOn, Guid[] PolicyIds,
    Guid[] DocumentIds, string? Note, int Version)
{
    public const string Columns = "id, project_id, code, titles::text, owner_id, status, due_on, policy_ids, document_ids, note, version";

    public static ControlRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetString(2),
        JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(3), JsonDefaults.Options)!, r.Get<Guid?>("owner_id"), r.GetString(5),
        r.IsDBNull(6) ? null : r.GetFieldValue<DateOnly>(6), r.GetFieldValue<Guid[]>(7), r.GetFieldValue<Guid[]>(8), r.Get<string>("note"), r.GetInt32(10));
}

internal sealed record TaskRow(Guid Id, Guid ProjectId, Guid ControlId, string Title, Guid? OwnerId, DateOnly? DueOn, DateTimeOffset? DoneAt)
{
    public const string Columns = "id, project_id, control_id, title, owner_id, due_on, done_at";

    public static TaskRow Read(NpgsqlDataReader r) => new(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3), r.Get<Guid?>("owner_id"),
        r.IsDBNull(5) ? null : r.GetFieldValue<DateOnly>(5), r.Get<DateTimeOffset?>("done_at"));
}

/// <summary>
/// What the viewer may do in a project. Managers hold <c>compliance.manage</c> over the project's unit (or the tenant);
/// viewers hold <c>compliance.view</c> there; owners own a control or an evidence task in it; readers of the space it is
/// shared to (auditor guests among them) see it read-only (FR-024).
/// </summary>
internal sealed record Standing(ProjectRow Project, bool Manages, bool Views, bool Owner, bool SpaceReader)
{
    public bool Sees => Manages || Views || Owner || SpaceReader;

    public bool ReadOnly => !Manages && !Owner;
}

/// <summary>
/// Compliance projects (spec 026 US7, FR-022 to FR-024): a project picks a framework and an org unit scope and copies
/// its controls; each control has an owner, a status, a due date, links to policies and documents, and evidence tasks.
/// An audit export attached as evidence is copied with its SHA-256 (FR-023). The readiness view counts controls by
/// status and lists overdue tasks, by owner and org unit. A project shared to a guest space is read-only to its readers.
/// </summary>
public sealed class ComplianceProjects(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    ComplianceLibrary library,
    IAuditWriter audit,
    IAuditExports exports,
    IPolicyReferences policies,
    IDocumentReferences documents,
    ISpaceAccess spaces,
    IUserDirectory users,
    ITenantMemberships memberships,
    IOrganizationHierarchy hierarchy,
    TenantObjectStorage storage,
    IEntitlementService entitlements,
    IClock clock)
{
    public const int MaxLinks = 20;
    public const int MaxTasksPerControl = 30;
    public const long MaxEvidenceBytes = 200L * 1024 * 1024;
    public const string ProjectsLink = "/compliance";

    public static string EvidenceKey(Guid id, string format) => $"compliance/evidence/{id}.{format}";

    private DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);

    private async Task<bool> AllowedAsync(string permission, AuthzTarget target, CancellationToken ct) => (await authz.CheckAsync(permission, target, ct)).Allowed;

    public async Task<ComplianceRights> RightsAsync(CancellationToken ct)
    {
        if (!await entitlements.IsEnabledAsync(Capabilities.ComplianceProjects, ct))
        {
            return new ComplianceRights(false, false, false);
        }
        var manage = await authz.HoldsAnywhereAsync(Permissions.ComplianceManage, ct);
        return new ComplianceRights(true, manage || await authz.HoldsAnywhereAsync(Permissions.ComplianceView, ct), manage);
    }

    // ---- Standing ----

    private Task<ProjectRow?> RowAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {ProjectRow.Columns} from compliance.project where id = @id{(forUpdate ? " for update" : "")}", new { id }, ProjectRow.Read, ct);

    private async Task<Standing> StandingAsync(ProjectRow p, CancellationToken ct)
    {
        var user = context.RequireUser();
        var manages = await AllowedAsync(Permissions.ComplianceManage, p.Target, ct);
        var views = manages || await AllowedAsync(Permissions.ComplianceView, p.Target, ct);
        var owner = !manages && await session.ScalarAsync<bool>(
            """
            select exists (select 1 from compliance.project_control where project_id = @id and owner_id = @user)
              or exists (select 1 from compliance.evidence_task where project_id = @id and owner_id = @user)
            """, new { id = p.Id, user }, ct);
        var reader = !views && !owner && p.SharedSpaceId is { } space && await spaces.ForViewerAsync(space, ct) is { Reads: true };
        return new Standing(p, manages, views, owner, reader);
    }

    /// <summary>A project the viewer can see; anything else is not found, so projects of other units stay hidden.</summary>
    private async Task<Standing> VisibleAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await library.RequireAsync(ct);
        var p = await RowAsync(id, forUpdate, ct) ?? throw AppException.NotFound();
        var standing = await StandingAsync(p, ct);
        return standing.Sees ? standing : throw AppException.NotFound();
    }

    private async Task<Standing> ManagedAsync(Guid id, CancellationToken ct)
    {
        var standing = await VisibleAsync(id, true, ct);
        return standing.Manages ? standing : throw AppException.Denied();
    }

    private static void RequireActive(ProjectRow p)
    {
        if (!p.Active)
        {
            throw AppException.Conflict("conflict.compliance.archived");
        }
    }

    // ---- Lists and views ----

    private async Task<Dictionary<Guid, (Dictionary<string, int> ByStatus, int Overdue)>> CountsAsync(IReadOnlyCollection<Guid> projectIds, CancellationToken ct)
    {
        var counts = projectIds.ToDictionary(id => id, _ => (ByStatus: ControlStatuses.All.ToDictionary(s => s, _ => 0), Overdue: 0));
        if (projectIds.Count == 0)
        {
            return counts;
        }
        var ids = projectIds.ToArray();
        foreach (var (project, status, n) in await session.QueryAsync(
            "select project_id, status, count(*)::int from compliance.project_control where project_id = any(@ids) group by project_id, status",
            new { ids }, r => (r.GetGuid(0), r.GetString(1), r.GetInt32(2)), ct))
        {
            counts[project].ByStatus[status] = n;
        }
        foreach (var (project, n) in await session.QueryAsync(
            "select project_id, count(*)::int from compliance.evidence_task where project_id = any(@ids) and done_at is null and due_on < @today group by project_id",
            new { ids, today = Today }, r => (r.GetGuid(0), r.GetInt32(1)), ct))
        {
            counts[project] = (counts[project].ByStatus, n);
        }
        return counts;
    }

    private async Task<SpaceRef?> SpaceRefAsync(ProjectRow p, CancellationToken ct) =>
        p.SharedSpaceId is { } id ? (await spaces.ForViewerAsync(id, ct) is { } s ? new SpaceRef(id, s.Name) : new SpaceRef(id, "")) : null;

    private async Task<IReadOnlyList<ProjectSummary>> SummariesAsync(IReadOnlyList<Standing> list, CancellationToken ct)
    {
        var counts = await CountsAsync(list.Select(s => s.Project.Id).ToList(), ct);
        var units = await hierarchy.GetNamesAsync(list.Select(s => s.Project.OrgUnitId).OfType<Guid>().Distinct(), ct);
        var result = new List<ProjectSummary>();
        foreach (var s in list)
        {
            var p = s.Project;
            var (byStatus, overdue) = counts[p.Id];
            result.Add(new ProjectSummary(p.Id, p.Name, p.FrameworkKey, p.FrameworkVersion, p.FrameworkNames, p.OrgUnitId,
                p.OrgUnitId is { } u && units.TryGetValue(u, out var n) ? n : null, await SpaceRefAsync(p, ct), p.Status, byStatus, overdue,
                new ProjectRights(s.Manages, s.ReadOnly), p.CreatedAt));
        }
        return result;
    }

    /// <summary>
    /// The projects the viewer can see, active first. With <paramref name="spaceId"/>, only those shared to that space
    /// (the space page lists them for its readers).
    /// </summary>
    public async Task<IReadOnlyList<ProjectSummary>> ListAsync(Guid? spaceId, CancellationToken ct)
    {
        await library.RequireAsync(ct);
        var rows = await session.QueryAsync(
            $"select {ProjectRow.Columns} from compliance.project where (@spaceId::uuid is null or shared_space_id = @spaceId) order by status, created_at desc",
            new { spaceId }, ProjectRow.Read, ct);
        var visible = new List<Standing>();
        foreach (var row in rows)
        {
            var standing = await StandingAsync(row, ct);
            if (standing.Sees)
            {
                visible.Add(standing);
            }
        }
        return await SummariesAsync(visible, ct);
    }

    private async Task<Dictionary<Guid, PersonRef>> PeopleAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var found = await users.GetManyAsync(ids.OfType<Guid>().Distinct().ToList(), ct);
        return found.ToDictionary(u => u.Key, u => new PersonRef(u.Key, u.Value.DisplayName));
    }

    private static PersonRef? Person(Dictionary<Guid, PersonRef> people, Guid? id) =>
        id is { } u ? people.TryGetValue(u, out var p) ? p : new PersonRef(u, "") : null;

    private bool ControlOverdue(ControlRow c) => c.DueOn is { } due && due < Today && c.Status is ControlStatuses.NotStarted or ControlStatuses.InProgress;

    public async Task<ProjectView> GetAsync(Guid id, CancellationToken ct)
    {
        var standing = await VisibleAsync(id, false, ct);
        var controls = await session.QueryAsync($"select {ControlRow.Columns} from compliance.project_control where project_id = @id order by position",
            new { id }, ControlRow.Read, ct);
        var taskCounts = (await session.QueryAsync(
            """
            select t.control_id, count(*)::int, count(*) filter (where t.done_at is null)::int,
              (select count(*)::int from compliance.evidence e where e.task_id = any(array_agg(t.id)))
            from compliance.evidence_task t where t.project_id = @id group by t.control_id
            """, new { id }, r => (Control: r.GetGuid(0), Tasks: r.GetInt32(1), Open: r.GetInt32(2), Evidence: r.GetInt32(3)), ct)).ToDictionary(t => t.Control);
        var people = await PeopleAsync(controls.Select(c => c.OwnerId), ct);
        var summaries = controls.Select(c =>
        {
            var t = taskCounts.TryGetValue(c.Id, out var v) ? v : default;
            return new ControlSummary(c.Id, c.Code, c.Titles, Person(people, c.OwnerId), c.Status, c.DueOn, ControlOverdue(c), t.Tasks, t.Open, t.Evidence,
                c.PolicyIds.Length, c.DocumentIds.Length, c.Version);
        }).ToList();
        return new ProjectView((await SummariesAsync([standing], ct))[0], summaries, standing.Project.Version);
    }

    // ---- Projects ----

    private async Task RequireUnitAsync(Guid unitId, CancellationToken ct)
    {
        if (!await hierarchy.UnitExistsAsync(unitId, ct))
        {
            throw AppException.Validation("orgUnitId", "validation.compliance.unit_unknown");
        }
    }

    /// <summary>An owner must be an active member of the tenant who is not a guest: guests only ever read (FR-024).</summary>
    private async Task RequireOwnerAsync(Guid userId, string field, CancellationToken ct)
    {
        if (await memberships.GetAsync(userId, ct) is not { Status: MembershipStatus.Active } m || m.MembershipType == MembershipTypes.Guest)
        {
            throw AppException.Validation(field, "validation.compliance.owner_invalid");
        }
    }

    /// <summary>Starts a project from a framework (US7 scenario 1): every control is copied, with the owner and due date given, if any.</summary>
    public async Task<ProjectView> CreateAsync(ProjectRequest request, CancellationToken ct)
    {
        await library.RequireAsync(ct);
        var target = request.OrgUnitId is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant;
        if (request.OrgUnitId is { } u)
        {
            await RequireUnitAsync(u, ct);
        }
        await authz.DemandAsync(Permissions.ComplianceManage, target, ct);
        var errors = new ValidationErrors();
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 1 or > 120)
        {
            errors.Add("name", "validation.compliance.project_name");
        }
        var framework = string.IsNullOrWhiteSpace(request.FrameworkKey) ? null : await library.FindAsync(request.FrameworkKey, ct);
        if (framework is null)
        {
            errors.Add("frameworkKey", "validation.compliance.framework_unknown");
        }
        errors.ThrowIfAny();
        if (request.OwnerId is { } owner)
        {
            await RequireOwnerAsync(owner, "ownerId", ct);
        }

        var id = Ids.New();
        var now = clock.UtcNow;
        var tenant = context.RequireTenant();
        await session.ExecAsync(
            """
            insert into compliance.project (tenant_id, id, name, framework_key, framework_version, framework_names, org_unit_id, created_by, created_at, updated_at)
            values (@tenant, @id, @name, @key, @version, @names, @unit, @by, @now, @now)
            """, new { tenant, id, name, key = framework!.Key, version = framework.Version, names = Jsonb.From(framework.Names), unit = request.OrgUnitId, by = context.RequireUser(), now }, ct);
        var position = 0;
        foreach (var control in framework.Controls)
        {
            await session.ExecAsync(
                """
                insert into compliance.project_control (tenant_id, id, project_id, position, code, titles, owner_id, due_on, updated_at)
                values (@tenant, @cid, @id, @position, @code, @titles, @owner, @due, @now)
                """, new { tenant, cid = Ids.New(), id, position = position++, code = control.Code, titles = Jsonb.From(control.Titles), owner = request.OwnerId, due = request.DueOn, now }, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ComplianceProjectCreated, "compliance_project", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { name, framework = framework.Key, version = framework.Version, controls = framework.Controls.Count }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Renames, rescopes, archives or restores a project. Moving it needs <c>compliance.manage</c> over both units.</summary>
    public async Task<ProjectView> UpdateAsync(Guid id, ProjectChange change, CancellationToken ct)
    {
        var standing = await ManagedAsync(id, ct);
        var p = standing.Project;
        if (change.Version is { } version && version != p.Version)
        {
            throw AppException.Conflict("conflict.compliance.changed");
        }
        var name = change.Name?.Trim() ?? p.Name;
        if (name.Length is < 1 or > 120)
        {
            throw AppException.Validation("name", "validation.compliance.project_name");
        }
        var status = change.Status ?? p.Status;
        if (status is not ("active" or "archived"))
        {
            throw AppException.Validation("status", "validation.compliance.project_status");
        }
        var unit = change.WholeTenant == true ? null : change.OrgUnitId ?? p.OrgUnitId;
        if (unit != p.OrgUnitId)
        {
            if (unit is { } u)
            {
                await RequireUnitAsync(u, ct);
            }
            await authz.DemandAsync(Permissions.ComplianceManage, unit is { } to ? AuthzTarget.OrgUnit(to) : AuthzTarget.Tenant, ct);
        }
        await session.ExecAsync(
            "update compliance.project set name = @name, org_unit_id = @unit, status = @status, version = version + 1, updated_at = @now where id = @id",
            new { id, name, unit, status, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ComplianceProjectChanged, "compliance_project", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { name = name == p.Name ? null : new { from = p.Name, to = name }, orgUnit = unit == p.OrgUnitId ? null : new { from = p.OrgUnitId, to = unit },
                status = status == p.Status ? null : new { from = p.Status, to = status } }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>
    /// Shares the project read-only to a space the manager owns (FR-024), or stops sharing it with a null space. Every
    /// reader of the space then sees the controls, their status and the evidence, and nothing else of the tenant.
    /// </summary>
    public async Task<ProjectView> ShareAsync(Guid id, ShareRequest request, CancellationToken ct)
    {
        var standing = await ManagedAsync(id, ct);
        if (request.SpaceId is { } spaceId)
        {
            RequireActive(standing.Project);
            var space = await spaces.ForViewerAsync(spaceId, ct);
            if (space is not { Owns: true })
            {
                throw AppException.Validation("spaceId", "validation.compliance.space_not_owned");
            }
            if (!space.Active)
            {
                throw AppException.Validation("spaceId", "validation.compliance.space_archived");
            }
        }
        await session.ExecAsync("update compliance.project set shared_space_id = @space, version = version + 1, updated_at = @now where id = @id",
            new { id, space = request.SpaceId, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ComplianceProjectShared, "compliance_project", id.ToString(), TargetOrgUnitId: standing.Project.OrgUnitId,
            Changes: new { from = standing.Project.SharedSpaceId, to = request.SpaceId }), ct);
        return await GetAsync(id, ct);
    }

    // ---- Controls ----

    private Task<ControlRow?> ControlAsync(Guid id, bool forUpdate, CancellationToken ct) =>
        session.QuerySingleOrDefaultAsync($"select {ControlRow.Columns} from compliance.project_control where id = @id{(forUpdate ? " for update" : "")}",
            new { id }, ControlRow.Read, ct);

    private ControlRights RightsFor(Standing s, ControlRow c)
    {
        var active = s.Project.Active;
        var owns = c.OwnerId == context.ActorId;
        return new ControlRights(active && s.Manages, active && (s.Manages || owns), active && s.Manages, active && (s.Manages || owns));
    }

    public async Task<ControlDetail> GetControlAsync(Guid id, CancellationToken ct)
    {
        await library.RequireAsync(ct);
        var c = await ControlAsync(id, false, ct) ?? throw AppException.NotFound();
        var standing = await VisibleAsync(c.ProjectId, false, ct);
        return await DetailAsync(standing, c, ct);
    }

    private async Task<ControlDetail> DetailAsync(Standing standing, ControlRow c, CancellationToken ct)
    {
        var tasks = await session.QueryAsync($"select {TaskRow.Columns} from compliance.evidence_task where control_id = @id order by created_at",
            new { id = c.Id }, TaskRow.Read, ct);
        var taskIds = tasks.Select(t => t.Id).ToArray();
        var evidence = await session.QueryAsync(
            "select id, task_id, kind, source_id, format, row_count, bytes, sha256, attached_by, attached_at from compliance.evidence where task_id = any(@taskIds) order by attached_at",
            new { taskIds }, r => (Task: r.GetGuid(1), Id: r.GetGuid(0), Kind: r.GetString(2), Source: r.GetGuid(3), Format: r.GetString(4), Rows: r.Get<long?>("row_count"),
                Bytes: r.GetInt64(6), Sha: r.GetString(7), By: r.GetGuid(8), At: r.GetFieldValue<DateTimeOffset>(9)), ct);
        var people = await PeopleAsync(tasks.Select(t => t.OwnerId).Append(c.OwnerId).Concat(evidence.Select(e => (Guid?)e.By)), ct);
        var rights = RightsFor(standing, c);
        var user = context.ActorId;
        var taskViews = tasks.Select(t =>
        {
            var mine = t.OwnerId == user;
            return new TaskView(t.Id, t.Title, Person(people, t.OwnerId), t.DueOn, t.DoneAt, t.DoneAt is null && t.DueOn < Today,
                evidence.Where(e => e.Task == t.Id).Select(e => new EvidenceView(e.Id, e.Kind, e.Source, e.Format, e.Rows, e.Bytes, e.Sha, Person(people, e.By), e.At)).ToList(),
                rights.CanAddTasks, standing.Project.Active && (rights.CanAddTasks || mine));
        }).ToList();

        // Links are read as the viewer: a policy or document they cannot open shows as unavailable, with no name.
        var policyLinks = new List<LinkView>();
        foreach (var pid in c.PolicyIds)
        {
            policyLinks.Add(await policies.VisibleAsync(pid, ct) is { } p ? new LinkView(pid, p.Code, p.Names, true) : new LinkView(pid, null, null, false));
        }
        var documentLinks = new List<LinkView>();
        foreach (var did in c.DocumentIds)
        {
            documentLinks.Add(await documents.ReadableAsync(did, ct) is { } d
                ? new LinkView(did, null, new Dictionary<string, string> { ["*"] = d.Name }, true) : new LinkView(did, null, null, false));
        }
        var open = tasks.Count(t => t.DoneAt is null);
        var summary = new ControlSummary(c.Id, c.Code, c.Titles, Person(people, c.OwnerId), c.Status, c.DueOn, ControlOverdue(c), tasks.Count, open, evidence.Count,
            c.PolicyIds.Length, c.DocumentIds.Length, c.Version);
        return new ControlDetail(summary, standing.Project.Id, standing.Project.Name, c.Note, policyLinks, documentLinks, taskViews, rights);
    }

    /// <summary>
    /// Changes a control. Managers change everything; the control's owner moves its status up to "evidence ready" and
    /// writes the note. Only a manager marks a control reviewed. Links are checked as the editor: they must be able to
    /// see each policy and document they add.
    /// </summary>
    public async Task<ControlDetail> UpdateControlAsync(Guid id, ControlChange change, CancellationToken ct)
    {
        await library.RequireAsync(ct);
        var c = await ControlAsync(id, true, ct) ?? throw AppException.NotFound();
        var standing = await VisibleAsync(c.ProjectId, false, ct);
        var rights = RightsFor(standing, c);
        RequireActive(standing.Project);
        if (!rights.CanChangeStatus)
        {
            throw AppException.Denied();
        }
        if (change.Version is { } version && version != c.Version)
        {
            throw AppException.Conflict("conflict.compliance.changed");
        }
        var managerOnly = change.OwnerId is not null || change.ClearOwner == true || change.DueOn is not null || change.ClearDue == true
            || change.PolicyIds is not null || change.DocumentIds is not null;
        if (managerOnly && !rights.CanEdit)
        {
            throw AppException.Denied();
        }
        var status = change.Status ?? c.Status;
        if (!ControlStatuses.All.Contains(status))
        {
            throw AppException.Validation("status", "validation.compliance.control_status");
        }
        if (status != c.Status && (status == ControlStatuses.Reviewed || c.Status == ControlStatuses.Reviewed) && !rights.CanReview)
        {
            throw AppException.Denied("authz.denied.compliance_review");
        }
        var note = change.Note is null ? c.Note : change.Note.Trim() is { Length: > 0 } n ? n : null;
        if (note is { Length: > 2000 })
        {
            throw AppException.Validation("note", "validation.compliance.note_length");
        }
        var owner = change.ClearOwner == true ? null : change.OwnerId ?? c.OwnerId;
        if (owner is { } o && o != c.OwnerId)
        {
            await RequireOwnerAsync(o, "ownerId", ct);
        }
        var due = change.ClearDue == true ? null : change.DueOn ?? c.DueOn;
        var policyIds = change.PolicyIds?.Distinct().ToArray() ?? c.PolicyIds;
        var documentIds = change.DocumentIds?.Distinct().ToArray() ?? c.DocumentIds;
        if (policyIds.Length > MaxLinks || documentIds.Length > MaxLinks)
        {
            throw AppException.Validation(policyIds.Length > MaxLinks ? "policyIds" : "documentIds", "validation.compliance.too_many_links");
        }
        foreach (var added in policyIds.Except(c.PolicyIds))
        {
            if (await policies.VisibleAsync(added, ct) is null)
            {
                throw AppException.Validation("policyIds", "validation.compliance.policy_unknown");
            }
        }
        foreach (var added in documentIds.Except(c.DocumentIds))
        {
            if (await documents.ReadableAsync(added, ct) is null)
            {
                throw AppException.Validation("documentIds", "validation.compliance.document_unknown");
            }
        }
        await session.ExecAsync(
            """
            update compliance.project_control set owner_id = @owner, due_on = @due, status = @status, policy_ids = @policyIds, document_ids = @documentIds,
              note = @note, version = version + 1, updated_at = @now
            where id = @id
            """, new { id, owner, due, status, policyIds, documentIds, note, now = clock.UtcNow }, ct);
        if (status != c.Status)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ComplianceControlStatusChanged, "compliance_control", id.ToString(), TargetOrgUnitId: standing.Project.OrgUnitId,
                Changes: new { project = c.ProjectId, control = c.Code, from = c.Status, to = status }), ct);
        }
        if (owner != c.OwnerId || due != c.DueOn || !policyIds.SequenceEqual(c.PolicyIds) || !documentIds.SequenceEqual(c.DocumentIds))
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.ComplianceControlChanged, "compliance_control", id.ToString(), TargetOrgUnitId: standing.Project.OrgUnitId,
                Changes: new { project = c.ProjectId, control = c.Code, owner = owner == c.OwnerId ? null : new { from = c.OwnerId, to = owner },
                    due = due == c.DueOn ? null : new { from = c.DueOn, to = due }, policies = policyIds.Length, documents = documentIds.Length }), ct);
        }
        return await DetailAsync(standing, (await ControlAsync(id, false, ct))!, ct);
    }

    // ---- Evidence tasks ----

    private async Task<(Standing Standing, ControlRow Control, TaskRow Task)> TaskAsync(Guid id, CancellationToken ct)
    {
        await library.RequireAsync(ct);
        var t = await session.QuerySingleOrDefaultAsync($"select {TaskRow.Columns} from compliance.evidence_task where id = @id for update", new { id }, TaskRow.Read, ct)
            ?? throw AppException.NotFound();
        var c = (await ControlAsync(t.ControlId, false, ct))!;
        return (await VisibleAsync(t.ProjectId, false, ct), c, t);
    }

    private static string TaskTitle(string? title)
    {
        var value = title?.Trim() ?? "";
        return value.Length is >= 1 and <= 200 ? value : throw AppException.Validation("title", "validation.compliance.task_title");
    }

    /// <summary>Adds an evidence task to a control (managers and the control's owner).</summary>
    public async Task<ControlDetail> AddTaskAsync(Guid controlId, TaskRequest request, CancellationToken ct)
    {
        await library.RequireAsync(ct);
        var c = await ControlAsync(controlId, true, ct) ?? throw AppException.NotFound();
        var standing = await VisibleAsync(c.ProjectId, false, ct);
        RequireActive(standing.Project);
        if (!RightsFor(standing, c).CanAddTasks)
        {
            throw AppException.Denied();
        }
        var title = TaskTitle(request.Title);
        if (request.OwnerId is { } owner)
        {
            await RequireOwnerAsync(owner, "ownerId", ct);
        }
        if (await session.ScalarAsync<int>("select count(*)::int from compliance.evidence_task where control_id = @controlId", new { controlId }, ct) >= MaxTasksPerControl)
        {
            throw AppException.Validation("title", "validation.compliance.task_limit");
        }
        await session.ExecAsync(
            """
            insert into compliance.evidence_task (tenant_id, id, project_id, control_id, title, owner_id, due_on, created_by, created_at)
            values (@tenant, @id, @project, @controlId, @title, @owner, @due, @by, @now)
            """, new { tenant = context.RequireTenant(), id = Ids.New(), project = c.ProjectId, controlId, title, owner = request.OwnerId ?? c.OwnerId, due = request.DueOn ?? c.DueOn,
                by = context.RequireUser(), now = clock.UtcNow }, ct);
        return await DetailAsync(standing, c, ct);
    }

    /// <summary>Changes a task. Managers and the control's owner change everything; the task's owner only marks it done or open.</summary>
    public async Task<ControlDetail> UpdateTaskAsync(Guid id, TaskRequest request, CancellationToken ct)
    {
        var (standing, c, t) = await TaskAsync(id, ct);
        RequireActive(standing.Project);
        var full = RightsFor(standing, c).CanAddTasks;
        var mine = t.OwnerId == context.ActorId;
        var onlyDone = request.Title is null && request.OwnerId is null && request.ClearOwner != true && request.DueOn is null && request.ClearDue != true;
        if (!full && !(mine && onlyDone))
        {
            throw AppException.Denied();
        }
        var title = request.Title is null ? t.Title : TaskTitle(request.Title);
        var owner = request.ClearOwner == true ? null : request.OwnerId ?? t.OwnerId;
        if (owner is { } o && o != t.OwnerId)
        {
            await RequireOwnerAsync(o, "ownerId", ct);
        }
        var due = request.ClearDue == true ? null : request.DueOn ?? t.DueOn;
        var doneAt = request.Done switch { true => t.DoneAt ?? clock.UtcNow, false => (DateTimeOffset?)null, null => t.DoneAt };
        await session.ExecAsync("update compliance.evidence_task set title = @title, owner_id = @owner, due_on = @due, done_at = @doneAt where id = @id",
            new { id, title, owner, due, doneAt }, ct);
        return await DetailAsync(standing, c, ct);
    }

    /// <summary>Removes a task that has no evidence; evidence is kept with the project for 7 years.</summary>
    public async Task<ControlDetail> DeleteTaskAsync(Guid id, CancellationToken ct)
    {
        var (standing, c, _) = await TaskAsync(id, ct);
        RequireActive(standing.Project);
        if (!RightsFor(standing, c).CanAddTasks)
        {
            throw AppException.Denied();
        }
        if (await session.ScalarAsync<bool>("select exists (select 1 from compliance.evidence where task_id = @id)", new { id }, ct))
        {
            throw AppException.Conflict("conflict.compliance.task_has_evidence");
        }
        await session.ExecAsync("delete from compliance.evidence_task where id = @id", new { id }, ct);
        return await DetailAsync(standing, c, ct);
    }

    /// <summary>The audit exports the caller could attach: completed, not expired, and only for holders of <c>audit.export</c>.</summary>
    public async Task<IReadOnlyList<AuditExportSummary>> ExportsAsync(CancellationToken ct)
    {
        await library.RequireAsync(ct);
        return await exports.ListAvailableAsync(20, ct);
    }

    /// <summary>
    /// Attaches an audit export to a task (US7 scenario 2, FR-023). The file is copied into the project's storage and its
    /// SHA-256 kept with the export ID, so the evidence outlives the export and anyone can check it was not changed. The
    /// task is then done. Whoever attaches needs <c>audit.export</c>, since they could download the export anyway.
    /// </summary>
    public async Task<ControlDetail> AttachExportAsync(Guid taskId, EvidenceRequest request, CancellationToken ct)
    {
        var (standing, c, t) = await TaskAsync(taskId, ct);
        RequireActive(standing.Project);
        if (!RightsFor(standing, c).CanAddTasks && t.OwnerId != context.ActorId)
        {
            throw AppException.Denied();
        }
        if (request.ExportId is not { } exportId)
        {
            throw AppException.Validation("exportId", "validation.compliance.export_unknown");
        }
        if (await exports.OpenAsync(exportId, ct) is not { } opened)
        {
            throw AppException.Validation("exportId", "validation.compliance.export_unknown");
        }
        var (export, content) = opened;
        using var buffer = new MemoryStream();
        string sha256;
        await using (content)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var chunk = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxEvidenceBytes)
                {
                    throw AppException.Validation("exportId", "validation.compliance.evidence_too_large");
                }
                hash.AppendData(chunk, 0, read);
                buffer.Write(chunk, 0, read);
            }
            sha256 = Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        var id = Ids.New();
        buffer.Position = 0;
        await storage.PutAsync(EvidenceKey(id, export.Format), buffer, export.Format == "csv" ? "text/csv" : "application/json", ct);
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into compliance.evidence (tenant_id, id, project_id, task_id, kind, source_id, format, row_count, bytes, sha256, attached_by, attached_at)
            values (@tenant, @id, @project, @taskId, 'audit_export', @exportId, @format, @rows, @bytes, @sha256, @by, @now)
            """, new { tenant = context.RequireTenant(), id, project = t.ProjectId, taskId, exportId, format = export.Format, rows = export.RowCount, bytes = buffer.Length,
                sha256, by = context.RequireUser(), now }, ct);
        await session.ExecAsync("update compliance.evidence_task set done_at = coalesce(done_at, @now) where id = @taskId", new { taskId, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ComplianceEvidenceAttached, "compliance_evidence", id.ToString(), TargetOrgUnitId: standing.Project.OrgUnitId,
            Changes: new { project = t.ProjectId, control = c.Code, task = taskId, export = exportId, sha256 }), ct);
        return await DetailAsync(standing, c, ct);
    }

    /// <summary>The evidence file, for anyone who sees the project (auditor guests included), with its kept hash.</summary>
    public async Task<(Stream Content, string ContentType, string FileName, string Sha256)> EvidenceFileAsync(Guid id, CancellationToken ct)
    {
        await library.RequireAsync(ct);
        var e = await session.QuerySingleOrDefaultAsync("select project_id, format, sha256, source_id from compliance.evidence where id = @id", new { id },
            r => ((Guid Project, string Format, string Sha, Guid Source)?)(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetGuid(3)), ct) ?? throw AppException.NotFound();
        await VisibleAsync(e.Project, false, ct);
        var stream = await storage.OpenReadAsync(EvidenceKey(id, e.Format), ct) ?? throw AppException.NotFound();
        return (stream, e.Format == "csv" ? "text/csv" : "application/json", $"audit-{e.Source}.{e.Format}", e.Sha);
    }

    // ---- Readiness ----

    /// <summary>
    /// The readiness view (US7 scenario 3): controls by status, overdue controls and tasks, by owner and by the owner's
    /// primary org unit. Controls without an owner are counted under no owner and no unit.
    /// </summary>
    public async Task<ReadinessView> ReadinessAsync(Guid id, CancellationToken ct)
    {
        await VisibleAsync(id, false, ct);
        var controls = await session.QueryAsync($"select {ControlRow.Columns} from compliance.project_control where project_id = @id order by position",
            new { id }, ControlRow.Read, ct);
        var overdue = await session.QueryAsync(
            """
            select t.id, t.control_id, c.code, t.title, t.owner_id, t.due_on
            from compliance.evidence_task t join compliance.project_control c on c.id = t.control_id
            where t.project_id = @id and t.done_at is null and t.due_on < @today order by t.due_on, c.position limit 200
            """, new { id, today = Today }, r => (Task: r.GetGuid(0), Control: r.GetGuid(1), Code: r.GetString(2), Title: r.GetString(3), Owner: r.Get<Guid?>("owner_id"),
                Due: r.GetFieldValue<DateOnly>(5)), ct);
        var owners = controls.Select(c => c.OwnerId).Concat(overdue.Select(o => o.Owner)).OfType<Guid>().Distinct().ToList();
        var people = await PeopleAsync(owners.Cast<Guid?>(), ct);
        var unitOf = new Dictionary<Guid, Guid?>();
        foreach (var owner in owners)
        {
            unitOf[owner] = (await hierarchy.GetActiveMembershipsAsync(owner, ct)).FirstOrDefault(m => m.IsPrimary)?.OrgUnitId;
        }
        var unitNames = await hierarchy.GetNamesAsync(unitOf.Values.OfType<Guid>().Distinct(), ct);

        static Dictionary<string, int> Count(IEnumerable<ControlRow> rows) =>
            ControlStatuses.All.ToDictionary(s => s, s => rows.Count(r => r.Status == s));

        var byOwner = controls.GroupBy(c => c.OwnerId)
            .Select(g => new OwnerReadiness(Person(people, g.Key), Count(g), overdue.Count(o => o.Owner == g.Key)))
            .OrderBy(o => o.Owner is null).ThenBy(o => o.Owner?.DisplayName, StringComparer.CurrentCulture).ToList();
        var byUnit = controls.GroupBy(c => c.OwnerId is { } o ? unitOf[o] : null)
            .Select(g => new UnitReadiness(g.Key, g.Key is { } u && unitNames.TryGetValue(u, out var n) ? n : null, Count(g)))
            .OrderBy(u => u.OrgUnitId is null).ToList();
        return new ReadinessView(id, controls.Count, Count(controls), controls.Count(ControlOverdue),
            overdue.Select(o => new OverdueTask(o.Task, o.Control, o.Code, o.Title, Person(people, o.Owner), o.Due)).ToList(), byOwner, byUnit);
    }
}
