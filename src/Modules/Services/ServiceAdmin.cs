using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Services.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Modules.Workflow.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Services;

public sealed record ServiceSettingsRequest(
    string? Kind,
    IReadOnlyDictionary<string, string>? Names,
    IReadOnlyDictionary<string, string>? Descriptions,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Synonyms,
    Guid? CategoryId,
    ServiceAudience? Audience,
    Guid? OwnerOrgUnitId,
    string? Classification,
    string? Url,
    bool Sso = false,
    string? Icon = null);

public sealed record ServiceDraftRequest(FormDefinition? Form, ServiceRoute? Route, Guid? FulfilmentQueueGroupId, bool AllowOnBehalf = false, int MaxAttachments = 0);

public sealed record CategoryRequest(IReadOnlyDictionary<string, string>? Names, int Position = 0);

/// <summary>The owner's view: the service settings, its latest version (draft or published) and the published version number.</summary>
public sealed record ServiceAdminView(ServiceRow Service, VersionRow? Latest);

/// <summary>
/// The basic service builder of spec 016 US7 (R1): create a form service or linked app, edit the draft form and its one
/// approval step, publish and retire. Owners act with <c>service.manage</c> at the service's owning unit (or the tenant
/// when it has none), and an owner's audience stays inside that unit (US7 scenario 3). Publishing keeps earlier form
/// versions for requests filled with them (scenario 2).
/// </summary>
public sealed partial class ServiceAdmin(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IAuthorizationProvisioning groups,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    IProcessEngine engine,
    IAuditWriter audit,
    IOutbox outbox,
    IClock clock,
    Catalogue catalogue,
    Identity.Contracts.IUserDirectory users)
{
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;
    public const int MaxAttachmentsLimit = 10;

    [GeneratedRegex("^[a-z]{2,3}(-[A-Za-z0-9]{2,8})?$")]
    private static partial Regex Locale();

    /// <summary>The engine key of a service's approval route.</summary>
    public static string ProcessKey(Guid serviceId) => "service." + serviceId.ToString("N");

    private static AuthzTarget Target(Guid? owner) => owner is { } unit ? AuthzTarget.OrgUnit(unit) : AuthzTarget.Tenant;

    private async Task<ServiceRow> ManagedAsync(Guid id, bool forUpdate, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var row = await session.QuerySingleOrDefaultAsync($"select {Catalogue.Columns} from services.service where id = @id" + (forUpdate ? " for update" : ""),
            new { id }, Catalogue.Read, ct) ?? throw AppException.NotFound();
        var decision = await authz.CheckAsync(Permissions.ServiceManage, Target(row.OwnerOrgUnitId), ct);
        if (!decision.Allowed)
        {
            // Owners elsewhere do not learn the service exists.
            throw (await authz.HoldsAnywhereAsync(Permissions.ServiceManage, ct)) ? AppException.NotFound() : AppException.Denied();
        }
        return row;
    }

    public async Task<IReadOnlyList<ServiceAdminView>> ListAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var coverage = await authz.CoverageAsync(Permissions.ServiceManage, ct);
        if (!coverage.Any)
        {
            throw AppException.Denied();
        }
        await catalogue.SyncModuleServicesAsync(ct);
        var rows = (await session.QueryAsync($"select {Catalogue.Columns} from services.service order by created_at, id", null, Catalogue.Read, ct))
            .Where(r => r.OwnerOrgUnitId is { } owner ? coverage.Covers(owner) : coverage.WholeTenant).ToList();
        var views = new List<ServiceAdminView>();
        foreach (var row in rows)
        {
            views.Add(new ServiceAdminView(row, await Versions.LatestAsync(session, row.Id, false, ct)));
        }
        return views;
    }

    /// <summary>The latest version of a form service the caller owns, for the preview.</summary>
    internal async Task<VersionRow> LatestManagedAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, false, ct);
        if (row.Kind != ServiceKinds.Form)
        {
            throw AppException.Validation("serviceId", "validation.services.not_form");
        }
        return await Versions.LatestAsync(session, id, false, ct) ?? throw AppException.NotFound();
    }

    public async Task<ServiceAdminView> GetAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, false, ct);
        return new ServiceAdminView(row, await Versions.LatestAsync(session, id, false, ct));
    }

    private static bool Texts(IReadOnlyDictionary<string, string>? texts, int max, bool required) =>
        texts is null ? !required
            : (!required || texts.Count > 0) && texts.Count <= 10 && texts.All(t => Locale().IsMatch(t.Key) && t.Value is not null && t.Value.Trim().Length <= max)
              && (!required || texts.Values.Any(v => !string.IsNullOrWhiteSpace(v)));

    private static IReadOnlyDictionary<string, string> Clean(IReadOnlyDictionary<string, string>? texts) =>
        (texts ?? new Dictionary<string, string>()).Where(t => !string.IsNullOrWhiteSpace(t.Value)).ToDictionary(t => t.Key, t => t.Value.Trim(), StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CleanSynonyms(IReadOnlyDictionary<string, IReadOnlyList<string>>? synonyms) =>
        (synonyms ?? new Dictionary<string, IReadOnlyList<string>>())
            .ToDictionary(s => s.Key, s => (IReadOnlyList<string>)s.Value.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).Distinct().ToList(), StringComparer.Ordinal);

    /// <summary>Checks the settings shared by every kind; the audience is checked against the owning unit.</summary>
    private async Task ValidateSettingsAsync(ServiceSettingsRequest request, string kind, ValidationErrors errors, CancellationToken ct)
    {
        if (kind != ServiceKinds.Module)
        {
            if (!Texts(request.Names, MaxNameLength, required: true))
            {
                errors.Add("names", "validation.services.names");
            }
            if (!Texts(request.Descriptions, MaxDescriptionLength, required: false))
            {
                errors.Add("descriptions", "validation.services.descriptions");
            }
        }
        if (request.Synonyms is { } synonyms && (synonyms.Count > 10 || synonyms.Any(s => !Locale().IsMatch(s.Key) || s.Value is null || s.Value.Count > 20
            || s.Value.Any(v => v is null || v.Length > 100))))
        {
            errors.Add("synonyms", "validation.services.synonyms");
        }
        if (request.Classification is { } classification && !ConditionAttributes.Classifications.Contains(classification))
        {
            errors.Add("classification", "validation.services.classification");
        }
        if (request.CategoryId is { } category
            && !await session.ScalarAsync<bool>("select exists (select 1 from services.category where id = @category)", new { category }, ct))
        {
            errors.Add("categoryId", "validation.services.category");
        }
        if (request.OwnerOrgUnitId is { } owner && !await hierarchy.UnitExistsAsync(owner, ct))
        {
            errors.Add("ownerOrgUnitId", "validation.services.owner_unit");
        }
        if (kind == ServiceKinds.Linked)
        {
            // FR-002: only absolute http(s) addresses; no script, data or relative URLs.
            if (request.Url is not { Length: > 0 and <= 2000 } url || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) || string.IsNullOrEmpty(uri.Host) || uri.UserInfo.Length > 0)
            {
                errors.Add("url", "validation.services.url");
            }
        }
        else if (request.Url is not null || request.Sso)
        {
            errors.Add("url", "validation.services.url_not_linked");
        }
        // An icon is a name from the web app's icon set (spec 016 FR-018); the web app falls back to the category's icon
        // for a name it does not know, so the API only checks the form.
        if (request.Icon is { } icon && !System.Text.RegularExpressions.Regex.IsMatch(icon, "^[a-z][a-z0-9-]{1,31}$"))
        {
            errors.Add("icon", "validation.services.icon");
        }
        await ValidateAudienceAsync(request.Audience ?? ServiceAudience.Everyone, request.OwnerOrgUnitId, errors, ct);
    }

    private async Task ValidateAudienceAsync(ServiceAudience audience, Guid? owner, ValidationErrors errors, CancellationToken ct)
    {
        if ((audience.OrgUnits?.Count ?? 0) > 50 || (audience.Groups?.Count ?? 0) > 50
            || audience.MembershipTypes?.Any(t => !MembershipTypes.All.Contains(t)) == true)
        {
            errors.Add("audience", "validation.services.audience");
            return;
        }
        foreach (var unit in audience.OrgUnits ?? [])
        {
            if (!await hierarchy.UnitExistsAsync(unit, ct))
            {
                errors.Add("audience.orgUnits", "validation.services.audience");
                return;
            }
        }
        if (audience.Groups is { Count: > 0 } wanted && (await groups.ExistingGroupsAsync(wanted.Distinct().ToList(), ct)).Count != wanted.Distinct().Count())
        {
            errors.Add("audience.groups", "validation.services.audience");
            return;
        }
        if (owner is { } scope)
        {
            // A service owned by a unit is offered inside that unit only (US7 scenario 3).
            var units = audience.OrgUnits ?? [];
            if (units.Count == 0)
            {
                errors.Add("audience.orgUnits", "validation.services.audience_beyond_owner");
                return;
            }
            foreach (var unit in units)
            {
                if (!await hierarchy.IsAncestorOrSelfAsync(scope, unit, ct))
                {
                    errors.Add("audience.orgUnits", "validation.services.audience_beyond_owner");
                    return;
                }
            }
        }
    }

    public async Task<ServiceAdminView> CreateAsync(ServiceSettingsRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        var user = context.RequireUser();
        var kind = request.Kind ?? string.Empty;
        if (kind is not (ServiceKinds.Form or ServiceKinds.Linked))
        {
            // Module services register themselves (FR-010); owners create forms and linked apps.
            throw AppException.Validation("kind", "validation.services.kind");
        }
        await authz.DemandAsync(Permissions.ServiceManage, Target(request.OwnerOrgUnitId), ct);
        var errors = new ValidationErrors();
        await ValidateSettingsAsync(request, kind, errors, ct);
        errors.ThrowIfAny();

        await session.ExecAsync("select pg_advisory_xact_lock(hashtextextended(platform.current_tenant_id()::text || ':services.quota', 0))", null, ct);
        var count = await session.ScalarAsync<long>("select count(*) from services.service where kind <> 'module' and status <> 'retired'", null, ct);
        if (await entitlements.GetLimitAsync(Capabilities.WorkflowMaxServices, ct) is { } max && count >= max)
        {
            throw new AppException(ProblemCodes.EntitlementQuotaExhausted, 403, "validation.services.limit", null,
                new Dictionary<string, object?> { ["capability"] = Capabilities.WorkflowMaxServices, ["limit"] = max });
        }

        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into services.service (tenant_id, id, kind, names, descriptions, synonyms, category_id, audience, owner_org_unit_id, classification,
              url, sso, icon, status, created_at, created_by, updated_at)
            values (platform.current_tenant_id(), @id, @kind, @names, @descriptions, @synonyms, @category, @audience, @owner, @classification,
              @url, @sso, @icon, 'draft', @now, @user, @now)
            """, new
            {
                id, kind, names = Jsonb.From(Clean(request.Names)), descriptions = Jsonb.From(Clean(request.Descriptions)), synonyms = Jsonb.From(CleanSynonyms(request.Synonyms)),
                category = request.CategoryId, audience = Jsonb.From(request.Audience ?? ServiceAudience.Everyone), owner = request.OwnerOrgUnitId,
                classification = request.Classification ?? "internal", url = request.Url, sso = request.Sso, icon = request.Icon, now, user,
            }, ct);
        await session.ExecAsync(
            """
            insert into services.service_version (tenant_id, service_id, version, state, updated_at, updated_by)
            values (platform.current_tenant_id(), @id, 1, 'draft', @now, @user)
            """, new { id, now, user }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ServiceCreated, "service", id.ToString(), TargetOrgUnitId: request.OwnerOrgUnitId,
            Changes: new { kind, owner = request.OwnerOrgUnitId }), ct);
        return await GetAsync(id, ct);
    }

    public async Task<ServiceAdminView> UpdateAsync(Guid id, ServiceSettingsRequest request, CancellationToken ct)
    {
        var row = await ManagedAsync(id, forUpdate: true, ct);
        if (row.Status == "retired")
        {
            throw AppException.Conflict("conflict.services.retired");
        }
        if (request.OwnerOrgUnitId != row.OwnerOrgUnitId)
        {
            // Moving a service to another owner needs the permission there too.
            await authz.DemandAsync(Permissions.ServiceManage, Target(request.OwnerOrgUnitId), ct);
        }
        var errors = new ValidationErrors();
        var settings = row.Kind == ServiceKinds.Module
            ? request with { Names = row.Names, Descriptions = row.Descriptions, Url = null, Sso = false }
            : request;
        await ValidateSettingsAsync(settings, row.Kind, errors, ct);
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            update services.service set names = @names, descriptions = @descriptions, synonyms = @synonyms, category_id = @category, audience = @audience,
              owner_org_unit_id = @owner, classification = @classification, url = @url, sso = @sso, icon = coalesce(@icon, icon), updated_at = @now
            where id = @id
            """, new
            {
                id, names = Jsonb.From(Clean(settings.Names)), descriptions = Jsonb.From(Clean(settings.Descriptions)), synonyms = Jsonb.From(CleanSynonyms(settings.Synonyms)),
                category = settings.CategoryId, audience = Jsonb.From(settings.Audience ?? ServiceAudience.Everyone), owner = settings.OwnerOrgUnitId,
                classification = settings.Classification ?? row.Classification, url = settings.Url, sso = settings.Sso, icon = settings.Icon, now = clock.UtcNow,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ServiceChanged, "service", id.ToString(), TargetOrgUnitId: settings.OwnerOrgUnitId,
            Changes: new { part = "settings", before = new { row.CategoryId, row.Audience, row.OwnerOrgUnitId, row.Classification }, after = new { settings.CategoryId, settings.Audience, settings.OwnerOrgUnitId, settings.Classification } }), ct);
        return await GetAsync(id, ct);
    }

    private static void ValidateRoute(ServiceRoute route, FormDefinition form, ValidationErrors errors)
    {
        if (route.ServiceLevelDays is < 1 or > ServiceRoute.MaxServiceLevelDays)
        {
            errors.Add("route.serviceLevelDays", "validation.services.service_level");
        }
        if (route.Graph is { } graph)
        {
            // A step graph replaces the R1 shape; the two are never mixed.
            if (route.Approval is not null || route.Actions is { Count: > 0 })
            {
                errors.Add("route", "validation.services.route_graph");
                return;
            }
            RouteGraphCheck.Validate(graph, form, errors);
            return;
        }
        var actions = route.Actions ?? [];
        if (actions.Count > ServiceRoute.MaxActions)
        {
            errors.Add("route.actions", "validation.services.actions");
        }
        for (var i = 0; i < actions.Count; i++)
        {
            var a = actions[i];
            if (a is null || a.ConnectionId == Guid.Empty || string.IsNullOrWhiteSpace(a.Action) || a.Inputs is null || !Texts(a.Names, MaxNameLength, required: false))
            {
                errors.Add($"route.actions[{i}]", "validation.services.action_step");
            }
        }
        if (route.Approval is not { } step)
        {
            return;
        }
        if (!Texts(step.Names, MaxNameLength, required: false))
        {
            errors.Add("route.approval.names", "validation.services.names");
        }
        var rule = step.Approvers;
        if (rule is null || !ApproverKinds.All.Contains(rule.Kind ?? string.Empty))
        {
            errors.Add("route.approval.approvers", "validation.services.approvers");
            return;
        }
        if (rule.Kind == ApproverKinds.Users && (rule.Users is not { Count: > 0 and <= 50 } users || users.Any(u => u == Guid.Empty) || rule.Permission is not null))
        {
            errors.Add("route.approval.approvers", "validation.services.approvers");
        }
        if (rule.Kind == ApproverKinds.PermissionAtRequesterUnit && (rule.Permission is not { } permission || !Permissions.All.Contains(permission) || rule.Users is { Count: > 0 }))
        {
            errors.Add("route.approval.approvers", "validation.services.approvers");
        }
        if (rule.Kind == ApproverKinds.LineManager && (rule.Users is { Count: > 0 } || rule.Permission is { } fallback && !Permissions.All.Contains(fallback)))
        {
            errors.Add("route.approval.approvers", "validation.services.approvers");
        }
        if (step.DueWorkingDays is { } due && (due < 1 || due > 90))
        {
            errors.Add("route.approval.dueWorkingDays", "validation.services.due");
        }
    }

    /// <summary>
    /// Auto-approve rules on a Confidential or Restricted service skip a control, so they need a tenant administrator's
    /// sign-off (section 15): only someone who manages the tenant's settings may save or publish them there.
    /// </summary>
    private async Task DemandAutoApproveSignOffAsync(string? classification, ServiceRoute route, CancellationToken ct)
    {
        if (classification is "confidential" or "restricted" && route.Graph?.Steps?.Any(s => s?.AutoApprove is { Count: > 0 }) == true)
        {
            await authz.DemandAsync(Permissions.TenantSettingsManage, AuthzTarget.Tenant, ct);
        }
    }

    /// <summary>
    /// Start triggers (FR-014) and the services a route starts. A service never starts itself, by a trigger on its own
    /// completion or by a start service step (the designer blocks direct cycles); longer loops stop at run time.
    /// </summary>
    private async Task ValidateTriggersAsync(Guid id, ServiceRoute route, ValidationErrors errors, CancellationToken ct)
    {
        var triggers = route.Triggers ?? [];
        if (triggers.Count > ServiceRoute.MaxTriggers)
        {
            errors.Add("route.triggers", "validation.services.triggers");
            return;
        }
        var others = new List<(string Field, Guid Service)>();
        for (var i = 0; i < triggers.Count; i++)
        {
            var field = $"route.triggers[{i}]";
            var t = triggers[i];
            var ok = t is not null && TriggerKinds.All.Contains(t.Kind ?? string.Empty);
            if (ok && t!.Kind == TriggerKinds.Schedule)
            {
                ok = t.Event is null && t.ServiceId is null && t.Status is null && t.OrgUnitId is null
                    && t.Frequency is { } f && ScheduleFrequencies.All.Contains(f)
                    && t.Day is { } day && day >= 1 && day <= (f == ScheduleFrequencies.Weekly ? 7 : 28)
                    && t.RequesterId is { } requester && requester != Guid.Empty
                    && await users.GetAsync(requester, ct) is { Status: "active" };
            }
            else if (ok)
            {
                ok = t!.Event is { } e && TriggerEvents.All.Contains(e) && t.Frequency is null && t.Day is null
                    && (t.RequesterId is null || t.RequesterId != Guid.Empty && await users.GetAsync(t.RequesterId.Value, ct) is { Status: "active" })
                    && (e == TriggerEvents.RequestCompleted
                        ? t.ServiceId is { } other && other != Guid.Empty && (t.Status is null || RequestStatuses.Closed.Contains(t.Status))
                        : t.ServiceId is null && t.Status is null)
                    && (t.OrgUnitId is null || e == TriggerEvents.MembershipChanged);
                if (ok && t.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
                {
                    ok = false;
                }
                if (ok && t.ServiceId is { } other2)
                {
                    others.Add((field, other2));
                }
            }
            if (!ok)
            {
                errors.Add(field, "validation.services.trigger");
            }
        }
        var steps = route.Graph?.Steps ?? [];
        for (var i = 0; i < steps.Count; i++)
        {
            if (steps[i]?.Type == StepTypes.StartService && steps[i].ServiceId is { } target && target != Guid.Empty)
            {
                others.Add(($"route.graph.steps[{i}]", target));
            }
        }
        foreach (var (field, other) in others)
        {
            var kind = await session.ScalarAsync<string?>("select kind from services.service where id = @other", new { other }, ct);
            if (other == id)
            {
                errors.Add(field, "validation.services.route_cycle");
            }
            else if (kind != ServiceKinds.Form)
            {
                errors.Add(field, "validation.services.route_service");
            }
        }
    }

    /// <summary>Saves the draft form and route. Editing a published service starts the next version (US7 scenario 2).</summary>
    public async Task<ServiceAdminView> SaveDraftAsync(Guid id, ServiceDraftRequest request, CancellationToken ct)
    {
        var row = await ManagedAsync(id, forUpdate: true, ct);
        if (row.Kind != ServiceKinds.Form)
        {
            throw AppException.Validation("kind", "validation.services.not_form");
        }
        if (row.Status == "retired")
        {
            throw AppException.Conflict("conflict.services.retired");
        }
        var form = request.Form ?? FormDefinition.Empty;
        var route = request.Route ?? ServiceRoute.None;
        var errors = new ValidationErrors();
        FormValidator.ValidateDefinition(form, errors);
        ValidateRoute(route, form, errors);
        await ValidateTriggersAsync(id, route, errors, ct);
        if (route.Actions is { Count: > 0 } || route.Triggers is { Count: > 0 })
        {
            await entitlements.DemandAsync(Capabilities.WorkflowAutomation, ct); // Automatic steps and triggers (section 9).
        }
        await DemandAutoApproveSignOffAsync(row.Classification, route, ct);
        if (route.Graph is { } designed && !errors.Any)
        {
            // Each step type needs its package capability (section 9); approval and task steps in sequence need none more.
            foreach (var capability in designed.Capabilities())
            {
                await entitlements.DemandAsync(capability, ct);
            }
        }
        if (request.MaxAttachments is < 0 or > MaxAttachmentsLimit
            || (form.Fields?.Any(f => f?.Type == FieldTypes.Attachment) == true && request.MaxAttachments == 0))
        {
            errors.Add("maxAttachments", "validation.services.max_attachments");
        }
        if (request.FulfilmentQueueGroupId is { } queue && (await groups.ExistingGroupsAsync([queue], ct)).Count == 0)
        {
            errors.Add("fulfilmentQueueGroupId", "validation.services.queue");
        }
        errors.ThrowIfAny();

        var latest = (await Versions.LatestAsync(session, id, forUpdate: true, ct))!;
        var user = context.RequireUser();
        var now = clock.UtcNow;
        var args = new
        {
            id, form = Jsonb.From(form), route = Jsonb.From(route), queue = request.FulfilmentQueueGroupId, onBehalf = request.AllowOnBehalf,
            max = request.MaxAttachments, now, user, next = latest.Version + 1, version = latest.Version,
        };
        if (latest.State == "published")
        {
            await session.ExecAsync(
                """
                insert into services.service_version (tenant_id, service_id, version, state, form, route, fulfilment_queue_group_id, allow_on_behalf, max_attachments, updated_at, updated_by)
                values (platform.current_tenant_id(), @id, @next, 'draft', @form, @route, @queue, @onBehalf, @max, @now, @user)
                """, args, ct);
        }
        else
        {
            await session.ExecAsync(
                """
                update services.service_version set form = @form, route = @route, fulfilment_queue_group_id = @queue, allow_on_behalf = @onBehalf,
                  max_attachments = @max, updated_at = @now, updated_by = @user
                where service_id = @id and version = @version
                """, args, ct);
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ServiceChanged, "service", id.ToString(), TargetOrgUnitId: row.OwnerOrgUnitId,
            Changes: new { part = "draft", version = latest.State == "published" ? latest.Version + 1 : latest.Version, fields = form.Fields?.Count ?? 0, approval = route.Approval is not null }), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>Publishes the latest draft. A route with an approval step is published to the process engine first.</summary>
    public async Task<ServiceAdminView> PublishAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, forUpdate: true, ct);
        if (row.Kind == ServiceKinds.Module)
        {
            throw AppException.Validation("kind", "validation.services.not_form");
        }
        if (row.Status == "retired")
        {
            throw AppException.Conflict("conflict.services.retired");
        }
        var latest = (await Versions.LatestAsync(session, id, forUpdate: true, ct))!;
        if (latest.State == "published")
        {
            throw AppException.Conflict("conflict.services.nothing_to_publish");
        }
        if (row.Kind == ServiceKinds.Form && !latest.Form.Fields.Any(f => f.Type != FieldTypes.PolicyNote))
        {
            throw AppException.Validation("form", "validation.services.form_empty");
        }
        if (latest.Route.Actions is { Count: > 0 } || latest.Route.Triggers is { Count: > 0 })
        {
            await entitlements.DemandAsync(Capabilities.WorkflowAutomation, ct);
        }
        await DemandAutoApproveSignOffAsync(row.Classification, latest.Route, ct);
        if (latest.Route.Graph is { } graph)
        {
            foreach (var capability in graph.Capabilities())
            {
                await entitlements.DemandAsync(capability, ct);
            }
            // The engine checks the compiled model (reachable steps, matched joins, assignees and what steps refer to) and
            // publishes it as the next version; open requests finish on theirs (FR-009, FR-013, US5 scenario 3).
            var compiled = RouteGraphCompiler.Compile(Preferred(row.Names) ?? id.ToString("N"), graph);
            var result = await engine.PublishModelAsync(ProcessKey(id), RouteSources.ServiceRoute, compiled, row.OwnerOrgUnitId, ct);
            if (result.Problems.Count > 0)
            {
                throw AppException.Validation(result.Problems.GroupBy(p => RouteStepField(graph, p.ElementId))
                    .ToDictionary(g => g.Key, g => g.Select(p => p.Key).Distinct().ToArray()));
            }
        }
        else if (latest.Route.HasSteps)
        {
            var steps = new List<RouteStep>();
            if (latest.Route.Approval is { } step)
            {
                var name = Preferred(step.Names) ?? Preferred(row.Names) ?? id.ToString("N");
                var rule = step.Approvers;
                var candidates = rule.Kind switch
                {
                    ApproverKinds.Users => new CandidateRule(Users: rule.Users),
                    ApproverKinds.LineManager => new CandidateRule(Permission: rule.Permission ?? Permissions.WorkflowApprove, LineManager: true),
                    _ => new CandidateRule(Permission: rule.Permission),
                };
                steps.Add(new RouteStep("approval", name, candidates, step.DueWorkingDays));
            }
            var n = 0;
            foreach (var a in latest.Route.Actions ?? [])
            {
                n++;
                steps.Add(new RouteStep($"action_{n}", Preferred(a.Names) ?? a.Action, new CandidateRule(), null,
                    new ConnectorStep(a.ConnectionId, a.Action, a.Inputs ?? new Dictionary<string, string>(), a.OnBusinessError)));
            }
            // The engine checks each connector step against its action and names the field (spec 017 FR-009).
            await engine.PublishRouteAsync(ProcessKey(id), RouteSources.ServiceRoute, new RouteSpec(Preferred(row.Names) ?? id.ToString("N"), steps), ct);
        }
        var now = clock.UtcNow;
        var user = context.RequireUser();
        await session.ExecAsync("update services.service_version set state = 'published', published_at = @now, published_by = @user where service_id = @id and version = @version",
            new { id, version = latest.Version, now, user }, ct);
        await session.ExecAsync("update services.service set status = 'published', current_version = @version, updated_at = @now where id = @id",
            new { id, version = latest.Version, now }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ServicePublished, "service", id.ToString(), TargetOrgUnitId: row.OwnerOrgUnitId,
            Changes: new { version = latest.Version, row.Kind }), ct);
        await outbox.EnqueueAsync(new ServicePublished(id, latest.Version, row.Kind), ct);
        return await GetAsync(id, ct);
    }

    /// <summary>The field a problem of the compiled route is shown on: the step it came from, or the whole route.</summary>
    private static string RouteStepField(RouteGraph graph, string elementId)
    {
        var step = RouteGraph.StepIdOf(elementId);
        var index = graph.Steps.ToList().FindIndex(s => s.Id == step);
        return index < 0 ? "route.graph" : $"route.graph.steps[{index}]";
    }

    /// <summary>The engine keeps one name per element, for history and monitoring: English when there is one.</summary>
    internal static string? Preferred(IReadOnlyDictionary<string, string>? names) =>
        names is null ? null : names.TryGetValue("en", out var en) && !string.IsNullOrWhiteSpace(en) ? en : names.Values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

    /// <summary>Takes a service out of the catalogue. Open requests finish on their version.</summary>
    public async Task<ServiceAdminView> RetireAsync(Guid id, CancellationToken ct)
    {
        var row = await ManagedAsync(id, forUpdate: true, ct);
        if (row.Kind == ServiceKinds.Module)
        {
            throw AppException.Validation("kind", "validation.services.not_form");
        }
        if (row.Status == "retired")
        {
            throw AppException.Conflict("conflict.services.retired");
        }
        await session.ExecAsync("update services.service set status = 'retired', updated_at = @now where id = @id", new { id, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ServiceRetired, "service", id.ToString(), TargetOrgUnitId: row.OwnerOrgUnitId), ct);
        await outbox.EnqueueAsync(new ServiceRetired(id), ct);
        return await GetAsync(id, ct);
    }

    // ---- Categories ----

    private async Task DemandTenantAsync(CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.WorkflowCatalogue, ct);
        await authz.DemandAsync(Permissions.ServiceManage, AuthzTarget.Tenant, ct);
    }

    public async Task<CategoryView> CreateCategoryAsync(CategoryRequest request, CancellationToken ct)
    {
        await DemandTenantAsync(ct);
        if (!Texts(request.Names, MaxNameLength, required: true) || request.Position is < 0 or > 1000)
        {
            throw AppException.Validation("names", "validation.services.names");
        }
        var count = await session.ScalarAsync<long>("select count(*) from services.category", null, ct);
        if (count >= 100)
        {
            throw AppException.Validation("names", "validation.services.category_limit");
        }
        var id = Ids.New();
        var names = Clean(request.Names);
        await session.ExecAsync("insert into services.category (tenant_id, id, names, position) values (platform.current_tenant_id(), @id, @names, @position)",
            new { id, names = Jsonb.From(names), position = request.Position }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ServiceCategoryChanged, "service_category", id.ToString(), Changes: new { change = "created" }), ct);
        return new CategoryView(id, names, request.Position);
    }

    public async Task<CategoryView> UpdateCategoryAsync(Guid id, CategoryRequest request, CancellationToken ct)
    {
        await DemandTenantAsync(ct);
        if (!Texts(request.Names, MaxNameLength, required: true) || request.Position is < 0 or > 1000)
        {
            throw AppException.Validation("names", "validation.services.names");
        }
        var names = Clean(request.Names);
        if (await session.ExecAsync("update services.category set names = @names, position = @position where id = @id",
            new { id, names = Jsonb.From(names), position = request.Position }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ServiceCategoryChanged, "service_category", id.ToString(), Changes: new { change = "updated" }), ct);
        return new CategoryView(id, names, request.Position);
    }

    public async Task DeleteCategoryAsync(Guid id, CancellationToken ct)
    {
        await DemandTenantAsync(ct);
        if (await session.ScalarAsync<bool>("select exists (select 1 from services.service where category_id = @id)", new { id }, ct))
        {
            throw AppException.Conflict("conflict.services.category_in_use");
        }
        if (await session.ExecAsync("delete from services.category where id = @id", new { id }, ct) == 0)
        {
            throw AppException.NotFound();
        }
        await audit.WriteAsync(new AuditEntry(AuditActions.ServiceCategoryChanged, "service_category", id.ToString(), Changes: new { change = "deleted" }), ct);
    }
}
