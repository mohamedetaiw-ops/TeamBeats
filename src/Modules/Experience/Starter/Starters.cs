using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Modules.Experience.Delegation;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Modules.Experience.Shell;
using TeamBeats.Modules.Experience.Themes;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Starter;

/// <summary>One starter page: a template, the address and the menu label in the starter's language.</summary>
public sealed record StarterPageRequest(string? TemplateId, string? Slug, string? Title);

/// <summary>
/// The wizard's choices (spec 033 R2 T020): the brand to start from (none means the platform defaults), the design (the
/// shell's look), the navigation pattern, the language the starter is written in, the starter pages and the person who
/// will run the organization.
/// </summary>
public sealed record StarterRequest(Guid? OrgUnitId, Guid? FromBrandId, string? Design, string? Navigation, string? Language,
    IReadOnlyList<StarterPageRequest>? Pages, Guid? AdminUserId);

public sealed record StarterPageView(Guid Id, string Slug, Guid NodeId, bool Published);

public sealed record StarterView(Guid Id, Guid OrgUnitId, Guid? AdminUserId, string Language, string Design, string Navigation, Guid BrandId,
    IReadOnlyList<StarterPageView> Pages, string State, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt);

/// <summary>
/// The starter wizard for a new organization (spec 033 R2 T020, US6, FR-027). One run creates, in one transaction, a brand
/// from the chosen start with the design and navigation pattern, a page per chosen template owned by the organization,
/// a navigation entry for each, and gives the organization's administrator its pages, brand and navigation (T019). All of
/// it is a draft: the brand is not applied, the pages are not published and the organization menu is held here, so people
/// see nothing until the starter is published. Each part goes through its own service, so every permission, limit and
/// validation of those services applies as if the administrator had made it by hand.
/// </summary>
public sealed class Starters(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    ITenantSettingsReader tenantSettings,
    ThemeService themes,
    PageService pages,
    ShellService shell,
    Delegations delegations,
    IAuditWriter audit,
    IClock clock)
{
    public const int MaxPages = 8;
    private static readonly string[] Designs = [DesignPresets.Beats, DesignPresets.Atlas, DesignPresets.Pulse, DesignPresets.Studio];
    private static readonly string[] NavigationPatterns = ["rail", "top", "sidebar"];

    private sealed record Row(Guid Id, Guid Unit, Guid? Admin, string Language, string Design, string Navigation, Guid Brand,
        Guid[] PageIds, Guid[] NodeIds, string State, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt);

    public async Task<StarterView> CreateAsync(StarterRequest request, CancellationToken ct)
    {
        if (request.OrgUnitId is not { } unit || !await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.NotFound();
        }
        await entitlements.DemandAsync(Capabilities.ExperienceOrgBranding, ct);
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        await authz.DemandVisibleAsync(Permissions.ThemePublish, AuthzTarget.OrgUnit(unit), ct);

        var errors = new ValidationErrors();
        var enabled = (await tenantSettings.GetLocaleSettingsAsync(ct)).EnabledLocales;
        if (request.Language is null || !enabled.Contains(request.Language))
        {
            errors.Add("language", "validation.experience.starter_language");
        }
        if (request.Design is null || !Designs.Contains(request.Design))
        {
            errors.Add("design", "validation.experience.starter_design");
        }
        if (request.Navigation is null || !NavigationPatterns.Contains(request.Navigation))
        {
            errors.Add("navigation", "validation.experience.starter_navigation");
        }
        var chosen = request.Pages ?? [];
        if (chosen.Count == 0 || chosen.Count > MaxPages)
        {
            errors.Add("pages", "validation.experience.starter_pages");
        }
        for (var i = 0; i < chosen.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(chosen[i].Title) || chosen[i].Title!.Trim().Length > 120)
            {
                errors.Add($"pages[{i}].title", "validation.navigation.label");
            }
        }
        errors.ThrowIfAny();
        await session.ExecAsync("select pg_advisory_xact_lock(hashtext('experience.starter:' || @u::text))", new { u = unit }, ct);
        if (await session.ScalarAsync<bool>("select exists (select 1 from experience.starter where org_unit_id = @unit)", new { unit }, ct))
        {
            throw AppException.Conflict("conflict.experience.starter_exists");
        }

        // The brand: a copy of the chosen start, or the platform defaults, with the design, navigation and title chosen here.
        var names = (await hierarchy.GetNamesAsync([unit], ct)).GetValueOrDefault(unit) ?? new Dictionary<string, string>();
        var name = names.GetValueOrDefault(request.Language!) ?? names.Values.FirstOrDefault() ?? unit.ToString();
        var from = request.FromBrandId is { } source ? await themes.GetBrandAsync(source, ct) : null;
        var tokens = new Dictionary<string, TokenValue>(from?.Tokens ?? new Dictionary<string, TokenValue>())
        {
            [DesignPresets.PresetToken] = new(request.Design),
            ["layout.navigation"] = new(request.Navigation),
        };
        var assets = new Dictionary<string, string>(from?.Assets ?? new Dictionary<string, string>());
        var title = name.Trim();
        if (BrandSlots.IsValidTitle(title))
        {
            assets[$"{BrandSlots.Title}.{request.Language}"] = title;
        }
        var brand = await themes.CreateBrandAsync(new BrandRequest(Trim(name, 80), tokens, assets, from?.StrictContrast ?? false), ct);

        // The pages, each owned by the organization, and a navigation entry for each.
        var pageIds = new List<Guid>();
        var nodeIds = new List<Guid>();
        for (var i = 0; i < chosen.Count; i++)
        {
            var page = await pages.CreateAsync(new CreatePageRequest(chosen[i].TemplateId, chosen[i].Slug, unit, null), ct);
            var node = Ids.New();
            await shell.SaveNodeAsync(node, new NodeRequest(null, new Dictionary<string, string> { [request.Language!] = chosen[i].Title!.Trim() },
                page.Slug, page.Id, null, unit, i), ct);
            pageIds.Add(page.Id);
            nodeIds.Add(node);
        }

        // The organization's administrator owns what was made: its pages, brand and navigation, and nothing elsewhere.
        if (request.AdminUserId is { } admin && admin != context.ActorId)
        {
            await delegations.SetAsync(unit, admin, new DelegationRequest(["brand", "navigation", "pages"]), ct);
        }

        var id = Ids.New();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into experience.starter (tenant_id, id, org_unit_id, admin_user_id, language, design, navigation, brand_id, page_ids, node_ids,
              state, created_by, created_at)
            values (platform.current_tenant_id(), @id, @unit, @admin, @language, @design, @navigation, @brand, @pages, @nodes, 'draft', @by, @now)
            """,
            new
            {
                id, unit, admin = request.AdminUserId, language = request.Language, design = request.Design, navigation = request.Navigation,
                brand = brand.Id, pages = pageIds.ToArray(), nodes = nodeIds.ToArray(), by = context.ActorId ?? throw AppException.Denied(), now,
            }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ExperienceStarterCreated, "experience_starter", id.ToString(), TargetOrgUnitId: unit,
            Changes: new { brand = brand.Id, pages = pageIds, admin = request.AdminUserId, request.Design, request.Navigation, request.Language }), ct);
        return await ViewAsync((await RowAsync(id, ct))!, ct);
    }

    /// <summary>The starter of an organization, for whoever may publish themes there; another tenant's is not found.</summary>
    public async Task<StarterView?> ForUnitAsync(Guid unit, CancellationToken ct)
    {
        if (!await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.NotFound();
        }
        await authz.DemandVisibleAsync(Permissions.ThemeEdit, AuthzTarget.OrgUnit(unit), ct);
        var id = await session.ScalarAsync<Guid?>("select id from experience.starter where org_unit_id = @unit", new { unit }, ct);
        return id is { } found ? await ViewAsync((await RowAsync(found, ct))!, ct) : null;
    }

    /// <summary>
    /// Publishes the starter: applies its brand to the organization, publishes the organization menu and every page that is
    /// still a draft. A page under an approval route stays a draft for its approvers; the result says which.
    /// </summary>
    public async Task<StarterView> PublishAsync(Guid id, CancellationToken ct)
    {
        var row = await RowAsync(id, ct) ?? throw AppException.NotFound();
        await authz.DemandVisibleAsync(Permissions.ThemePublish, AuthzTarget.OrgUnit(row.Unit), ct);
        if (row.State == "published")
        {
            throw AppException.Conflict("conflict.experience.starter_published");
        }
        var scope = row.Unit.ToString();
        var current = await themes.GetAssignmentAsync(scope, ct);
        await themes.SetAssignmentAsync(scope, new AssignmentRequest(row.Brand, current.Overrides), ct);
        await shell.PublishMenuAsync("organization", scope, new MenuRequest(row.NodeIds.Select(n => new MenuItemInput(n, null)).ToList()), ct);
        var approvals = await pages.NeedsApprovalAsync(row.Unit, ct);
        foreach (var pageId in row.PageIds)
        {
            var page = await pages.GetAsync(pageId, ct);
            if (page.CurrentRelease is null && !approvals)
            {
                await pages.PublishAsync(pageId, new PublishRequest(null), ct);
            }
        }
        await session.ExecAsync("update experience.starter set state = 'published', published_by = @by, published_at = @now where id = @id",
            new { id, by = context.ActorId, now = clock.UtcNow }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ExperienceStarterPublished, "experience_starter", id.ToString(), TargetOrgUnitId: row.Unit,
            Changes: new { brand = row.Brand, pages = row.PageIds }), ct);
        return await ViewAsync((await RowAsync(id, ct))!, ct);
    }

    private async Task<StarterView> ViewAsync(Row row, CancellationToken ct)
    {
        var list = new List<StarterPageView>();
        for (var i = 0; i < row.PageIds.Length; i++)
        {
            var page = await pages.GetAsync(row.PageIds[i], ct);
            list.Add(new StarterPageView(page.Id, page.Slug, row.NodeIds[i], page.CurrentRelease is not null));
        }
        return new StarterView(row.Id, row.Unit, row.Admin, row.Language, row.Design, row.Navigation, row.Brand, list, row.State, row.CreatedAt, row.PublishedAt);
    }

    private async Task<Row?> RowAsync(Guid id, CancellationToken ct) =>
        (await session.QueryAsync(
            """
            select id, org_unit_id, admin_user_id, language, design, navigation, brand_id, page_ids, node_ids, state, created_at, published_at
            from experience.starter where id = @id
            """, new { id },
            r => new Row(r.GetGuid(0), r.GetGuid(1), r.IsDBNull(2) ? null : r.GetGuid(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetGuid(6),
                r.GetFieldValue<Guid[]>(7), r.GetFieldValue<Guid[]>(8), r.GetString(9), r.GetFieldValue<DateTimeOffset>(10),
                r.IsDBNull(11) ? null : r.GetFieldValue<DateTimeOffset>(11)), ct)).FirstOrDefault();

    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max];
}
