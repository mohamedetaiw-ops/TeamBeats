using System.Text.Json;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Agents.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Agents;

public static class AgentStatuses
{
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Published = "published";
    public const string Suspended = "suspended";
    public const string Retired = "retired";
}

public static class AgentVersionStates
{
    public const string Draft = "draft";
    public const string InReview = "in_review";
    public const string Published = "published";
    public const string Rejected = "rejected";
}

/// <summary>A template in the library (spec 020 US1 scenario 1), with why it is not ready when it cannot be installed.</summary>
public sealed record TemplateView(
    string Id,
    int Version,
    Dictionary<string, string>? Name,
    Dictionary<string, string>? Description,
    string? Icon,
    string? Category,
    string? Autonomy,
    List<string>? Knowledge,
    List<string>? Tools,
    bool Ready,
    IReadOnlyList<string> MissingTools,
    IReadOnlyList<string> BlockedTools,
    bool AutonomyAbovePolicy);

/// <summary>
/// Installs a template, or with no template creates a blank agent in Agent Studio (spec 020 US4): then the name,
/// instructions, category and languages of the first step are given and everything else starts at its safest value.
/// </summary>
public sealed record InstallRequest(string? TemplateId, Guid? OrgUnitId, Dictionary<string, string>? Name = null,
    Dictionary<string, string>? Instructions = null, string? Category = null, List<string>? Languages = null);

/// <summary>A clone (US4 scenario 4): a new draft with a new identity. Audience and tools are copied only when asked.</summary>
/// <summary>What Agent Studio offers the designer: blank agents and how many of the limit are used, and the JSON view.</summary>
public sealed record StudioOffer(bool Custom, long CustomCount, long? CustomLimit, bool JsonView);

public sealed record CloneRequest(Guid? OrgUnitId, Dictionary<string, string>? Name, bool CopyAudience = false, bool CopyTools = false, bool CopyMemory = false);

public sealed record DraftRequest(int? Revision, AgentDefinition? Definition);

public sealed record OwnersRequest(Guid? OwnerUserId, Guid? BackupOwnerUserId);

public sealed record AgentSummary(
    Guid Id,
    Dictionary<string, string>? Name,
    Dictionary<string, string>? Description,
    string? Icon,
    string? Category,
    string Status,
    string RiskClass,
    Guid OrgUnitId,
    Guid OwnerUserId,
    string? TemplateId,
    int? TemplateVersion,
    int? CurrentVersion,
    int LatestVersion,
    DateTimeOffset UpdatedAt,
    Guid? BackupOwnerUserId = null,
    DateTimeOffset? OwnerFlaggedAt = null);

public sealed record AgentVersionView(int Number, string State, string RiskClass, int Revision, Guid LastEditor, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt);

/// <summary>An agent with the definition of its latest version, which is what the editor works on.</summary>
public sealed record AgentDetail(AgentSummary Agent, AgentDefinition Definition, AgentVersionView Latest, IReadOnlyList<AgentVersionView> Versions, bool CanEdit,
    int? TemplateUpdate = null, IReadOnlyList<string>? UnavailableTools = null, IReadOnlyList<TemplateSecurityView>? SecurityUpdates = null);

public sealed record TemplateUpdateRequest(int? Version);

/// <summary>
/// The agent registry (spec 020 T001, T003, T007): the template library, installing a template as a tenant agent, the
/// agents a person may see, and saving drafts checked against the definition schema, the tool registry and the tenant
/// policy (FR-003, FR-004). An agent is owned by an org unit; designing needs <c>agent.design</c> on that unit, and
/// people without a covering permission do not learn the agent exists.
/// </summary>
public sealed class AgentRegistry(IDbSession session, ITenantContext context, IEntitlementService entitlements, IAuthorizationService authz,
    IOrganizationHierarchy org, IAuditWriter audit, IClock clock, ToolRegistry tools, AgentPolicyService policies, ITenantMemberships memberships,
    ITemplateSecurityUpdates securityUpdates)
{
    private static string Json(object value) => JsonSerializer.Serialize(value, AgentPolicyService.Json);

    private static AgentDefinition Definition(string json) => JsonSerializer.Deserialize<AgentDefinition>(json, AgentPolicyService.Json)!;

    private async Task<Guid> DesignerAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAgentsBasic, ct);
        return context.RequireUser();
    }

    public async Task<IReadOnlyList<TemplateView>> TemplatesAsync(CancellationToken ct)
    {
        await DesignerAsync(ct);
        if (!await authz.HoldsAnywhereAsync(Permissions.AgentDesign, ct))
        {
            throw AppException.Denied();
        }
        var policy = (await policies.CurrentAsync(ct)).Policy;
        var list = new List<TemplateView>();
        foreach (var template in AgentTemplates.All)
        {
            // Only templates the package includes are shown (US1 scenario 1).
            if (!await entitlements.IsEnabledAsync(template.Capability, ct))
            {
                continue;
            }
            list.Add(View(template, policy, await UnavailableAsync(template.Definition.Tools, ct)));
        }
        return list;
    }

    /// <summary>Tools whose capability the tenant's package does not include; an agent cannot hold them.</summary>
    private async Task<IReadOnlySet<string>> UnavailableAsync(IEnumerable<string>? names, CancellationToken ct)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in (names ?? []).Distinct())
        {
            if (tools.Find(name)?.Definition.RequiredCapability is { } capability && !await entitlements.IsEnabledAsync(capability, ct))
            {
                set.Add(name);
            }
        }
        return set;
    }

    private TemplateView View(AgentTemplate template, AgentPolicy policy, IReadOnlySet<string> unavailable)
    {
        var d = template.Definition;
        var names = d.Tools ?? [];
        var missing = names.Where(n => tools.Find(n) is null || unavailable.Contains(n)).ToList();
        var blocked = names.Where(n => tools.Find(n) is { } t && (!policy.Allows(n) || policy.RuleFor(t.Definition) == ToolApprovalRules.Block)).ToList();
        var above = AgentAutonomy.Rank(d.Autonomy) > AgentAutonomy.Rank(policy.MaxAutonomy) ||
            Classifications.Rank(d.MaxClassification) > Classifications.Rank(policy.MaxClassification) || d.MaxToolCalls > policy.MaxToolCalls;
        return new TemplateView(template.Id, template.Version, d.Name, d.Description, d.Icon, d.Category, d.Autonomy, d.Knowledge, d.Tools,
            missing.Count == 0 && blocked.Count == 0 && !above, missing, blocked, above);
    }

    public async Task<IReadOnlyList<ToolView>> ToolsAsync(CancellationToken ct)
    {
        await DesignerAsync(ct);
        if (!await authz.HoldsAnywhereAsync(Permissions.AgentDesign, ct) && !await authz.HoldsAnywhereAsync(Permissions.AiPolicyManage, ct) &&
            !await authz.HoldsAnywhereAsync(Permissions.AgentView, ct))
        {
            throw AppException.Denied();
        }
        var policy = (await policies.CurrentAsync(ct)).Policy;
        // The Studio tool picker (US4 scenario 2) offers a tool only when policy allows it and the package includes it.
        var unavailable = await UnavailableAsync(tools.All.Select(t => t.Definition.Name), ct);
        return tools.All.Select(t => t.Definition).OrderBy(d => d.Name, StringComparer.Ordinal)
            .Select(d => new ToolView(d.Name, d.Module, d.Version, d.SideEffect, d.ApprovalRule, policy.RuleFor(d), d.OutputClassification,
                policy.Allows(d.Name) && policy.RuleFor(d) != ToolApprovalRules.Block && !unavailable.Contains(d.Name), d.Parameters, d.Title,
                !unavailable.Contains(d.Name)))
            .ToList();
    }

    /// <summary>Installs a template as a tenant-owned agent in the designer's org unit (FR-005), as a first draft for that unit.</summary>
    public async Task<AgentDetail> InstallAsync(InstallRequest request, CancellationToken ct)
    {
        var user = await DesignerAsync(ct);
        if (request.OrgUnitId is not { } unit || !await org.UnitExistsAsync(unit, ct))
        {
            throw AppException.Validation("orgUnitId", "validation.agent.org_unit");
        }
        await authz.DemandAsync(Permissions.AgentDesign, AuthzTarget.OrgUnit(unit), ct);
        if (request.TemplateId is null)
        {
            return await CreateBlankAsync(request, unit, user, ct);
        }
        if (request.TemplateId is not { } templateId || AgentTemplates.Find(templateId) is not { } template ||
            !await entitlements.IsEnabledAsync(template.Capability, ct))
        {
            throw AppException.Validation("templateId", "validation.agent.template");
        }
        var policy = (await policies.CurrentAsync(ct)).Policy;
        if (!View(template, policy, await UnavailableAsync(template.Definition.Tools, ct)).Ready)
        {
            throw AppException.Validation("templateId", "validation.agent.template_not_ready");
        }
        var now = clock.UtcNow;
        // A forced security update in force is part of the install (FR-005), so its owner is not told it was applied later.
        var inForce = securityUpdates.InForce(template.Id, now);
        var definition = TemplateSecurity.Apply(template.Definition, inForce) with { Audience = new AgentAudience(AgentAudience.Units, [unit]) };
        AgentDefinitions.Validate(definition, tools, policy);
        var risk = AgentDefinitions.RiskClass(definition, tools);
        var id = Guid.NewGuid();
        var applied = inForce.Count > 0 ? inForce[^1].Version : 0;
        await session.ExecAsync(
            """
            insert into agents.agent (tenant_id, id, template_id, template_version, owner_user_id, org_unit_id, status, current_version, risk_class, created_at, created_by, updated_at,
              template_security_applied, template_security_announced)
            values (platform.current_tenant_id(), @id, @template, @version, @user, @unit, 'draft', null, @risk, @now, @user, @now, @applied, @applied);
            insert into agents.agent_version (tenant_id, agent_id, number, state, definition, risk_class, revision, last_editor, created_at, updated_at)
            values (platform.current_tenant_id(), @id, 1, 'draft', @definition::jsonb, @risk, 1, @user, @now, @now);
            """, new { id, template = template.Id, version = template.Version, user, unit, risk, now, definition = Json(definition), applied }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentInstalledFromTemplate, "agent", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { template = template.Id, templateVersion = template.Version, riskClass = risk }), ct);
        return await DetailAsync(id, ct);
    }

    /// <summary>
    /// The Studio offer for the page (US4): whether blank agents are in the package, the custom agent count against the
    /// limit, and whether the caller gets the read-only JSON view, which is for AI policy administrators (section 10).
    /// </summary>
    public async Task<StudioOffer> StudioAsync(CancellationToken ct)
    {
        await DesignerAsync(ct);
        if (!await authz.HoldsAnywhereAsync(Permissions.AgentDesign, ct) && !await authz.HoldsAnywhereAsync(Permissions.AiPolicyManage, ct))
        {
            throw AppException.Denied();
        }
        var custom = await entitlements.IsEnabledAsync(Capabilities.AiAgentsCustom, ct);
        var count = await session.ScalarAsync<long>("select count(*) from agents.agent where template_id is null and status <> 'retired'", null, ct);
        return new StudioOffer(custom, count, custom ? await entitlements.GetLimitAsync(Capabilities.AiAgentsMaxCustom, ct) : 0,
            await authz.HoldsAnywhereAsync(Permissions.AiPolicyManage, ct));
    }

    /// <summary>
    /// Customer-created agents (FR-019): agents with no template. They need <c>ai.agents.custom</c>, and the tenant stays
    /// within <c>ai.agents.max_custom</c>; retired agents do not count.
    /// </summary>
    private async Task DemandCustomAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.AiAgentsCustom, ct);
        if (await entitlements.GetLimitAsync(Capabilities.AiAgentsMaxCustom, ct) is { } limit &&
            await session.ScalarAsync<long>("select count(*) from agents.agent where template_id is null and status <> 'retired'", null, ct) >= limit)
        {
            throw AppException.Conflict("conflict.agent.custom_limit");
        }
    }

    /// <summary>
    /// A blank agent (US4 scenario 1): the designer's name, instructions, category and languages, and otherwise the safest
    /// start: no tools, no knowledge, L0, Internal, the designer's unit as the audience.
    /// </summary>
    private async Task<AgentDetail> CreateBlankAsync(InstallRequest request, Guid unit, Guid user, CancellationToken ct)
    {
        await DemandCustomAsync(ct);
        var policy = (await policies.CurrentAsync(ct)).Policy;
        var definition = new AgentDefinition(request.Name, null, "spark", request.Category, request.Instructions, "neutral", "text", [], [],
            new AgentAudience(AgentAudience.Units, [unit]), request.Languages, null, "L0", "internal", Math.Min(5, policy.MaxToolCalls));
        AgentDefinitions.Validate(definition, tools, policy);
        var id = await InsertAsync(definition, null, null, null, unit, user, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentCreated, "agent", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { riskClass = AgentDefinitions.RiskClass(definition, tools) }), ct);
        return await DetailAsync(id, ct);
    }

    /// <summary>
    /// Clones an agent the designer can open into a new draft (US4 scenario 4, business rule on retired agents): a new
    /// identity, no published history, and audience and tools only when asked. A clone keeps its source's template, so
    /// only a clone of a customer-created agent counts against the custom agent limit.
    /// </summary>
    public async Task<AgentDetail> CloneAsync(Guid sourceId, CloneRequest request, CancellationToken ct)
    {
        var user = await DesignerAsync(ct);
        var source = await DetailAsync(sourceId, ct);
        if (request.OrgUnitId is not { } unit || !await org.UnitExistsAsync(unit, ct))
        {
            throw AppException.Validation("orgUnitId", "validation.agent.org_unit");
        }
        await authz.DemandAsync(Permissions.AgentDesign, AuthzTarget.OrgUnit(unit), ct);
        if (source.Agent.TemplateId is null)
        {
            await DemandCustomAsync(ct);
        }
        var policy = (await policies.CurrentAsync(ct)).Policy;
        var original = source.Definition;
        var definition = original with
        {
            Name = request.Name,
            Audience = request.CopyAudience ? original.Audience : new AgentAudience(AgentAudience.Units, [unit]),
            Tools = request.CopyTools ? original.Tools : [],
            // The memory area comes along only when asked; notes kept for people never do (spec 020 US4 scenario 4).
            Memory = request.CopyMemory ? original.Memory : null,
        };
        AgentDefinitions.Validate(definition, tools, policy);
        if ((await UnavailableAsync(definition.Tools, ct)).Count > 0)
        {
            throw AppException.Validation("tools", "validation.agent.tool_not_in_package");
        }
        if (request.CopyAudience)
        {
            await CheckAudienceAsync(definition.Audience!, ct);
        }
        var id = await InsertAsync(definition, source.Agent.TemplateId, source.Agent.TemplateVersion, sourceId, unit, user, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentCloned, "agent", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { from = sourceId, request.CopyAudience, request.CopyTools, request.CopyMemory }), ct);
        return await DetailAsync(id, ct);
    }

    private async Task<Guid> InsertAsync(AgentDefinition definition, string? template, int? version, Guid? clonedFrom, Guid unit, Guid user, CancellationToken ct)
    {
        var risk = AgentDefinitions.RiskClass(definition, tools);
        var id = Guid.NewGuid();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into agents.agent (tenant_id, id, template_id, template_version, cloned_from, owner_user_id, org_unit_id, status, current_version, risk_class, created_at, created_by, updated_at)
            values (platform.current_tenant_id(), @id, @template, @version, @clonedFrom, @user, @unit, 'draft', null, @risk, @now, @user, @now);
            insert into agents.agent_version (tenant_id, agent_id, number, state, definition, risk_class, revision, last_editor, created_at, updated_at)
            values (platform.current_tenant_id(), @id, 1, 'draft', @definition::jsonb, @risk, 1, @user, @now, @now);
            """, new { id, template, version, clonedFrom, user, unit, risk, now, definition = Json(definition) }, ct);
        return id;
    }

    private const string SummarySql =
        """
        select a.id, a.status, a.risk_class, a.org_unit_id, a.owner_user_id, a.template_id, a.template_version, a.current_version, a.updated_at,
          v.number, v.definition::text, a.backup_owner_user_id, a.owner_flagged_at
        from agents.agent a
        join agents.agent_version v on v.agent_id = a.id and v.number = (select max(number) from agents.agent_version x where x.agent_id = a.id)
        """;

    private static AgentSummary ReadSummary(Npgsql.NpgsqlDataReader r)
    {
        var d = Definition(r.GetString(10));
        return new AgentSummary(r.GetGuid(0), d.Name, d.Description, d.Icon, d.Category, r.GetString(1), r.GetString(2), r.GetGuid(3), r.GetGuid(4),
            r.Get<string?>("template_id"), r.Get<int?>("template_version"), r.Get<int?>("current_version"), r.GetInt32(9), r.GetFieldValue<DateTimeOffset>(8),
            r.Get<Guid?>("backup_owner_user_id"), r.Get<DateTimeOffset?>("owner_flagged_at"));
    }

    /// <summary>Agents the person owns or oversees: their own, and those of units where they hold <c>agent.view</c> or <c>agent.design</c>.</summary>
    public async Task<IReadOnlyList<AgentSummary>> ListAsync(CancellationToken ct)
    {
        var user = await DesignerAsync(ct);
        var view = await authz.CoverageAsync(Permissions.AgentView, ct);
        var design = await authz.CoverageAsync(Permissions.AgentDesign, ct);
        var all = view.WholeTenant || design.WholeTenant;
        var units = view.Units.Concat(design.Units).Distinct().ToArray();
        return await session.QueryAsync(
            $"{SummarySql} where @all or a.owner_user_id = @user or a.backup_owner_user_id = @user or a.org_unit_id = any(@units) order by a.updated_at desc, a.id limit 500",
            new { all, user, units }, ReadSummary, ct);
    }

    public async Task<AgentDetail> GetAsync(Guid id, CancellationToken ct)
    {
        await DesignerAsync(ct);
        return await DetailAsync(id, ct);
    }

    private async Task<AgentDetail> DetailAsync(Guid id, CancellationToken ct)
    {
        var user = context.RequireUser();
        var rows = await session.QueryAsync($"{SummarySql} where a.id = @id", new { id }, ReadSummary, ct);
        if (rows.Count == 0)
        {
            throw AppException.NotFound();
        }
        var agent = rows[0];
        var canEdit = (await authz.CheckAsync(Permissions.AgentDesign, AuthzTarget.OrgUnit(agent.OrgUnitId), ct)).Allowed;
        if (agent.OwnerUserId != user && agent.BackupOwnerUserId != user && !canEdit && !(await authz.CheckAsync(Permissions.AgentView, AuthzTarget.OrgUnit(agent.OrgUnitId), ct)).Allowed)
        {
            throw AppException.NotFound();
        }
        var versions = await session.QueryAsync(
            "select number, state, risk_class, revision, last_editor, created_at, updated_at, published_at, definition::text from agents.agent_version where agent_id = @id order by number desc",
            new { id },
            r => (View: new AgentVersionView(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetGuid(4), r.GetFieldValue<DateTimeOffset>(5),
                r.GetFieldValue<DateTimeOffset>(6), r.Get<DateTimeOffset?>("published_at")), Definition: r.GetString(8)), ct);
        // A newer platform version of the agent's template is offered, never applied on its own (FR-005).
        int? update = agent.TemplateId is { } templateId && AgentTemplates.Find(templateId) is { } template && template.Version > (agent.TemplateVersion ?? 0)
            ? template.Version : null;
        var definition = Definition(versions[0].Definition);
        await tools.LoadAsync(ct);
        var now = clock.UtcNow;
        var security = securityUpdates.Of(agent.TemplateId)
            .Select(u => new TemplateSecurityView(u.Version, u.Reason, u.AppliesAt, u.Critical, u.AppliesBy(now), u.RemoveTools, u.MaxAutonomy, u.MaxClassification, u.MaxToolCalls))
            .ToList();
        return new AgentDetail(agent, definition, versions[0].View, versions.Select(v => v.View).ToList(),
            canEdit && agent.Status != AgentStatuses.Retired, update, AgentDefinitions.UnavailableTools(definition, tools), security);
    }

    /// <summary>
    /// Saves the draft (FR-003, FR-004, edge case "two designers"): the definition is checked on every save, the audience
    /// stays inside the designer's org scope unless an AI Policy Administrator makes it tenant-wide, and a save over a
    /// newer revision is refused. Saving after a published or rejected version starts the next version.
    /// </summary>
    public async Task<AgentDetail> SaveDraftAsync(Guid id, DraftRequest request, CancellationToken ct)
    {
        var user = await DesignerAsync(ct);
        var current = await DetailAsync(id, ct);
        if (!current.CanEdit)
        {
            throw AppException.Denied();
        }
        if (request.Definition is not { } definition)
        {
            throw AppException.Validation("definition", "validation.agent.definition");
        }
        if (current.Latest.State == AgentVersionStates.InReview)
        {
            throw AppException.Conflict("conflict.agent.in_review");
        }
        if (request.Revision != current.Latest.Revision)
        {
            throw AppException.Conflict("conflict.agent.revision");
        }
        var policy = (await policies.CurrentAsync(ct)).Policy;
        AgentDefinitions.Validate(definition, tools, policy);
        // A forced security update of the template in force cannot be undone in a draft (FR-005).
        if (TemplateSecurity.Breaks(definition, securityUpdates.InForce(current.Agent.TemplateId, clock.UtcNow)))
        {
            throw AppException.Validation("definition", "validation.agent.template_security");
        }
        if ((await UnavailableAsync(definition.Tools, ct)).Count > 0)
        {
            throw AppException.Validation("tools", "validation.agent.tool_not_in_package");
        }
        await CheckAudienceAsync(definition.Audience!, ct);
        var risk = AgentDefinitions.RiskClass(definition, tools);
        var now = clock.UtcNow;
        if (current.Latest.State == AgentVersionStates.Draft)
        {
            var updated = await session.ExecAsync(
                """
                update agents.agent_version set definition = @definition::jsonb, risk_class = @risk, revision = revision + 1, last_editor = @user, updated_at = @now
                where agent_id = @id and number = @number and state = 'draft' and revision = @revision
                """, new { id, number = current.Latest.Number, revision = current.Latest.Revision, definition = Json(definition), risk, user, now }, ct);
            if (updated == 0)
            {
                throw AppException.Conflict("conflict.agent.revision");
            }
        }
        else
        {
            await session.ExecAsync(
                """
                insert into agents.agent_version (tenant_id, agent_id, number, state, definition, risk_class, revision, last_editor, created_at, updated_at)
                values (platform.current_tenant_id(), @id, @number, 'draft', @definition::jsonb, @risk, 1, @user, @now, @now)
                """, new { id, number = current.Latest.Number + 1, definition = Json(definition), risk, user, now }, ct);
        }
        // The agent's risk class is that of the version in use; until one is published it follows the draft.
        await session.ExecAsync("update agents.agent set updated_at = @now, risk_class = case when current_version is null then @risk else risk_class end where id = @id",
            new { id, now, risk }, ct);
        if (!SameAudience(current.Definition.Audience, definition.Audience))
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AgentAudienceChanged, "agent", id.ToString(), TargetOrgUnitId: current.Agent.OrgUnitId,
                Changes: new { from = current.Definition.Audience, to = definition.Audience }), ct);
        }
        return await DetailAsync(id, ct);
    }

    /// <summary>
    /// Names the owner and the backup owner (FR-002, section 4 "Agent owner"): both active members of the tenant, and not
    /// the same person. Setting them clears the mark left when an owner
    /// left. Audited <c>agent.owner_changed</c> with the old and new names.
    /// </summary>
    public async Task<AgentDetail> SetOwnersAsync(Guid id, OwnersRequest request, CancellationToken ct)
    {
        await DesignerAsync(ct);
        var current = await DetailAsync(id, ct);
        if (!current.CanEdit)
        {
            throw AppException.Denied();
        }
        var owner = request.OwnerUserId ?? current.Agent.OwnerUserId;
        if (!await QualifiesAsync(owner, ct))
        {
            throw AppException.Validation("ownerUserId", "validation.agent.owner");
        }
        if (request.BackupOwnerUserId is { } backup && (backup == owner || !await QualifiesAsync(backup, ct)))
        {
            throw AppException.Validation("backupOwnerUserId", "validation.agent.backup_owner");
        }
        await session.ExecAsync(
            "update agents.agent set owner_user_id = @owner, backup_owner_user_id = @backup, owner_flagged_at = null, updated_at = @now where id = @id",
            new { id, owner, backup = request.BackupOwnerUserId, now = clock.UtcNow }, ct);
        if (owner != current.Agent.OwnerUserId || request.BackupOwnerUserId != current.Agent.BackupOwnerUserId)
        {
            await audit.WriteAsync(new AuditEntry(AuditActions.AgentOwnerChanged, "agent", id.ToString(), TargetOrgUnitId: current.Agent.OrgUnitId,
                Changes: new
                {
                    from = new { owner = current.Agent.OwnerUserId, backup = current.Agent.BackupOwnerUserId },
                    to = new { owner, backup = request.BackupOwnerUserId },
                }), ct);
        }
        return await DetailAsync(id, ct);
    }

    // Owners are active members of the tenant. Naming someone grants nothing: editing still needs agent.design on the unit.
    private async Task<bool> QualifiesAsync(Guid user, CancellationToken ct) => (await memberships.GetAsync(user, ct))?.Status == MembershipStatus.Active;

    /// <summary>
    /// Takes the offered template version into a new draft (FR-005, edge case "template update during review"): the
    /// template's behaviour (instructions, knowledge, tools, autonomy, limits, tone, format and escalation text) replaces
    /// the agent's, while its name, description, purpose, icon, category, audience and languages stay as the tenant set
    /// them. The draft is checked like any save and published through review and the gate as usual. Refused while a
    /// version is in review, and for a version other than the one offered. Audited <c>agent.template_update_applied</c>.
    /// </summary>
    public async Task<AgentDetail> ApplyTemplateUpdateAsync(Guid id, TemplateUpdateRequest request, CancellationToken ct)
    {
        await DesignerAsync(ct);
        var current = await DetailAsync(id, ct);
        if (!current.CanEdit)
        {
            throw AppException.Denied();
        }
        if (current.TemplateUpdate is not { } offered || request.Version != offered)
        {
            throw AppException.Conflict("conflict.agent.no_template_update");
        }
        if (current.Latest.State == AgentVersionStates.InReview)
        {
            throw AppException.Conflict("conflict.agent.in_review");
        }
        var template = AgentTemplates.Find(current.Agent.TemplateId!)!.Definition;
        var mine = current.Definition;
        var merged = template with
        {
            Name = mine.Name,
            Description = mine.Description,
            Purpose = mine.Purpose,
            Icon = mine.Icon,
            Category = mine.Category,
            Audience = mine.Audience,
            Languages = mine.Languages,
        };
        merged = TemplateSecurity.Apply(merged, securityUpdates.InForce(current.Agent.TemplateId, clock.UtcNow));
        var detail = await SaveDraftAsync(id, new DraftRequest(current.Latest.Revision, merged), ct);
        await session.ExecAsync("update agents.agent set template_version = @offered where id = @id", new { id, offered }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.AgentTemplateUpdateApplied, "agent", id.ToString(), TargetOrgUnitId: current.Agent.OrgUnitId,
            Changes: new { template = current.Agent.TemplateId, from = current.Agent.TemplateVersion, to = offered, draft = detail.Latest.Number }), ct);
        return await DetailAsync(id, ct);
    }

    private static bool SameAudience(AgentAudience? a, AgentAudience? b) =>
        a?.Scope == b?.Scope && (a?.Outside ?? AgentAudience.Hidden) == (b?.Outside ?? AgentAudience.Hidden) &&
        (a?.OrgUnits ?? []).Order().SequenceEqual((b?.OrgUnits ?? []).Order());

    /// <summary>Spec 020 section 9: an audience no wider than the designer's org scope, and tenant-wide only with <c>ai.policy_manage</c>.</summary>
    private async Task CheckAudienceAsync(AgentAudience audience, CancellationToken ct)
    {
        if (audience.Scope == AgentAudience.Tenant)
        {
            if (!(await authz.CheckAsync(Permissions.AiPolicyManage, AuthzTarget.Tenant, ct)).Allowed)
            {
                throw AppException.Validation("audience", "validation.agent.audience_tenant");
            }
            return;
        }
        foreach (var unit in audience.OrgUnits!.Distinct())
        {
            if (!await org.UnitExistsAsync(unit, ct) || !(await authz.CheckAsync(Permissions.AgentDesign, AuthzTarget.OrgUnit(unit), ct)).Allowed)
            {
                throw AppException.Validation("audience", "validation.agent.audience_scope");
            }
        }
    }
}

/// <summary>Agent facts for other modules (<see cref="IAgentCatalog"/>), read without a permission check of its own.</summary>
public sealed class AgentCatalog(IDbSession session) : IAgentCatalog
{
    public async Task<AgentFacts?> GetAsync(Guid agentId, CancellationToken ct = default)
    {
        var rows = await session.QueryAsync(
            """
            select a.id, a.org_unit_id, a.owner_user_id, a.template_id, a.status, a.risk_class, a.current_version, v.number, v.definition::text
            from agents.agent a
            join agents.agent_version v on v.agent_id = a.id and v.number = (select max(number) from agents.agent_version x where x.agent_id = a.id)
            where a.id = @id
            """, new { id = agentId },
            r => (Id: r.GetGuid(0), Unit: r.GetGuid(1), Owner: r.GetGuid(2), Template: r.Get<string?>("template_id"), Status: r.GetString(4), Risk: r.GetString(5),
                Current: r.Get<int?>("current_version"), Latest: r.GetInt32(7), Definition: r.GetString(8)), ct);
        if (rows.Count == 0)
        {
            return null;
        }
        var row = rows[0];
        var d = System.Text.Json.JsonSerializer.Deserialize<AgentDefinition>(row.Definition, AgentPolicyService.Json)!;
        return new AgentFacts(row.Id, row.Unit, row.Owner, row.Template, row.Status, row.Risk, row.Current, row.Latest, d.Languages ?? [], d.Name);
    }

    public async Task<AgentVersionFacts?> VersionAsync(Guid agentId, int number, CancellationToken ct = default)
    {
        var rows = await session.QueryAsync("select number, state, risk_class, last_editor from agents.agent_version where agent_id = @agentId and number = @number",
            new { agentId, number }, r => new AgentVersionFacts(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetGuid(3)), ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public Task<IReadOnlyList<AgentFacts>> PublishedAsync(CancellationToken ct = default) =>
        FactsAsync("select id from agents.agent where status = 'published' and current_version is not null order by created_at, id limit 500", ct);

    public Task<IReadOnlyList<AgentFacts>> ListAsync(CancellationToken ct = default) =>
        FactsAsync("select id from agents.agent where status <> 'retired' order by created_at, id limit 500", ct);

    private async Task<IReadOnlyList<AgentFacts>> FactsAsync(string sql, CancellationToken ct)
    {
        var ids = await session.QueryAsync(sql, null, r => r.GetGuid(0), ct);
        var list = new List<AgentFacts>();
        foreach (var id in ids)
        {
            if (await GetAsync(id, ct) is { } facts)
            {
                list.Add(facts);
            }
        }
        return list;
    }
}
