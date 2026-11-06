using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Shell;

/// <summary>Shell layout: ordered regions per slot, and which registered utilities are on (spec 008 FR-001).</summary>
public sealed record ShellDefinition(IReadOnlyList<string> Start, IReadOnlyList<string> Center, IReadOnlyList<string> End,
    IReadOnlyList<string> Below, IReadOnlyList<string> Utilities);

public sealed record ShellAdminView(int Version, ShellDefinition Definition, IReadOnlyList<string> Regions, IReadOnlyList<string> Utilities);

public sealed record MenuItemInput(Guid NodeId, IReadOnlyList<MenuItemInput>? Children);

public sealed record MenuRequest(IReadOnlyList<MenuItemInput>? Items);

public sealed record MenuView(Guid Id, string Kind, string Scope, int Version, IReadOnlyList<MenuItemInput> Items);

public sealed record NodeRequest(Guid? ParentId, IReadOnlyDictionary<string, string>? Labels, string? Slug, Guid? PageId, string? Url,
    Guid? OwnerOrgUnitId, int Position = 0);

public sealed record NodeView(Guid Id, Guid? ParentId, IReadOnlyDictionary<string, string> Labels, string Slug, Guid? PageId, string? Url,
    Guid? OwnerOrgUnitId, int Position, IReadOnlyList<string> OldSlugs);

/// <summary>A resolved navigation entry for the caller: label in the caller's locale and where it goes.</summary>
public sealed record NavLink(Guid NodeId, string Label, string Href, string? Owner, IReadOnlyList<NavLink> Children);

public sealed record OrganizationIndicator(Guid Id, string Name, bool Primary);

public sealed record ShellView(int Version, ShellDefinition Layout, IReadOnlyList<NavLink> GlobalMenu,
    IReadOnlyList<OrganizationMenu> OrganizationMenus, IReadOnlyList<OrganizationIndicator> Organizations, bool CanSwitchOrganization);

public sealed record OrganizationMenu(Guid OrgUnitId, string Name, IReadOnlyList<NavLink> Items);

public sealed record Crumb(string Label, string Href);

/// <summary>
/// Answer of resolve: a page, a redirect, or the recovery result. Missing, unpublished and not permitted destinations
/// all give the same recovery answer, so the answer never reveals whether something exists (FR-019).
/// </summary>
public sealed record ResolveResult(string Kind, string? PageSlug, string? RedirectTo, Guid? NodeId, IReadOnlyList<Crumb> Breadcrumbs,
    IReadOnlyList<NavLink> SectionMenu, IReadOnlyList<Crumb> Recovery);

public sealed record QuickNavItem(string Label, string Href, string? Owner);

/// <summary>
/// The shell, menus and information architecture of spec 008 (R1). The shell and every menu publish on their own and
/// never touch pages. Menus, breadcrumbs and quick navigation list only destinations the caller may open: a node that
/// points to a page is shown only when the page is published and the caller holds <c>page.view</c> at its scope.
/// Editing needs <c>shell.edit</c> at tenant scope, or <c>navigation.edit</c> at the scope that owns the menu or node;
/// a scope outside the caller's grants answers not found.
/// </summary>
public sealed partial class ShellService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IOrganizationHierarchy hierarchy,
    IAuditWriter audit,
    IOutbox outbox)
{
    public const string TenantScope = "tenant";
    public static readonly IReadOnlyList<string> Regions =
    [
        "identity", "global_navigation", "organization_indicator", "search", "notifications", "task_badge", "contextual_actions",
        "breadcrumbs", "settings", "utilities",
    ];

    /// <summary>Registered shell utilities. Their components belong to their modules; the shell holds only the placement.</summary>
    public static readonly IReadOnlyList<string> UtilityKeys = ["world_clock", "time_converter", "weather", "mail_badge"];

    /// <summary>Regions every shell keeps, so language, mode, density and accessibility stay in one place (FR-010).</summary>
    private static readonly IReadOnlyList<string> RequiredRegions = ["identity", "settings"];

    public static readonly ShellDefinition DefaultShell = new(
        ["identity", "global_navigation"], ["search"], ["notifications", "task_badge", "utilities", "settings"],
        ["organization_indicator", "breadcrumbs", "contextual_actions"], []);

    private const int MaxMenuItems = 50;

    // Shell

    public async Task<ShellAdminView> GetShellAdminAsync(CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ShellEdit, AuthzTarget.Tenant, ct);
        var (version, definition) = await LiveShellAsync(ct);
        return new ShellAdminView(version, definition, Regions, UtilityKeys);
    }

    public async Task<ShellAdminView> PublishShellAsync(ShellDefinition? request, CancellationToken ct)
    {
        await authz.DemandAsync(Permissions.ShellEdit, AuthzTarget.Tenant, ct);
        var definition = ValidateShell(request);
        var (current, before) = await LiveShellAsync(ct);
        var version = current + 1;
        await session.ExecAsync(
            "insert into experience.shell_config (tenant_id, version, definition, published_by) values (@t, @v, @d, @a)",
            new { t = context.RequireTenant(), v = version, d = Jsonb.From(definition), a = context.ActorId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.ShellPublished, "shell", version.ToString(),
            Changes: new { before, after = definition, version }), ct);
        await outbox.EnqueueAsync(new ShellPublished(version), ct);
        return new ShellAdminView(version, definition, Regions, UtilityKeys);
    }

    public async Task<ShellView> GetShellAsync(string locale, CancellationToken ct)
    {
        var (version, layout) = await LiveShellAsync(ct);
        var nodes = await NodesAsync(ct);
        var visible = await VisibleNodesAsync(nodes, ct);
        var menus = await MenusAsync(ct);
        var global = menus.FirstOrDefault(m => m.Kind == "global" && m.Scope == TenantScope);
        var actor = context.ActorId ?? throw AppException.Denied();
        var memberships = await hierarchy.GetActiveMembershipsAsync(actor, ct);
        var unitIds = memberships.Select(m => m.OrgUnitId).Distinct().ToList();
        var names = await hierarchy.GetNamesAsync(unitIds, ct);
        var organizations = memberships.GroupBy(m => m.OrgUnitId)
            .Select(g => new OrganizationIndicator(g.Key, Label(names.GetValueOrDefault(g.Key), locale) ?? g.Key.ToString(), g.Any(m => m.IsPrimary)))
            .OrderByDescending(o => o.Primary).ThenBy(o => o.Name, StringComparer.Ordinal).ToList();

        // Organization menus of the caller's units and their ancestors, nearest unit first.
        var orgMenus = new List<OrganizationMenu>();
        var seen = new HashSet<Guid>();
        foreach (var unit in unitIds)
        {
            foreach (var scope in (await hierarchy.GetAncestorsAndSelfAsync(unit, ct)).Reverse())
            {
                if (!seen.Add(scope) || menus.FirstOrDefault(m => m.Kind == "organization" && m.Scope == scope.ToString()) is not { } menu)
                {
                    continue;
                }
                var scopeNames = await hierarchy.GetNamesAsync([scope], ct);
                orgMenus.Add(new OrganizationMenu(scope, Label(scopeNames.GetValueOrDefault(scope), locale) ?? scope.ToString(),
                    await LinksAsync(menu.Items, nodes, visible, locale, ct)));
            }
        }

        return new ShellView(version, layout, global is null ? [] : await LinksAsync(global.Items, nodes, visible, locale, ct), orgMenus,
            organizations, organizations.Count > 1);
    }

    // Menus

    public async Task<IReadOnlyList<MenuView>> ListMenusAsync(CancellationToken ct)
    {
        if (!await authz.HoldsAnywhereAsync(Permissions.NavigationEdit, ct))
        {
            throw AppException.Denied();
        }
        var result = new List<MenuView>();
        var nodes = await NodesAsync(ct);
        foreach (var menu in await MenusAsync(ct))
        {
            if (await CanEditScopeAsync(menu.Kind, menu.Scope, nodes, ct))
            {
                result.Add(menu);
            }
        }
        return result;
    }

    public async Task<MenuView> PublishMenuAsync(string kind, string scope, MenuRequest request, CancellationToken ct)
    {
        var nodes = await NodesAsync(ct);
        await DemandScopeAsync(kind, scope, nodes, ct);
        var items = ValidateItems(request.Items, nodes);
        var existing = (await MenusAsync(ct)).FirstOrDefault(m => m.Kind == kind && m.Scope == scope);
        var id = existing?.Id ?? Ids.New();
        var version = (existing?.Version ?? 0) + 1;
        await session.ExecAsync(
            """
            insert into experience.nav_menu (tenant_id, menu_id, kind, scope, version, items, published_by)
            values (@t, @id, @k, @s, @v, @items, @a)
            on conflict (tenant_id, kind, scope) do update set version = excluded.version, items = excluded.items,
              published_by = excluded.published_by, published_at = now()
            """,
            new { t = context.RequireTenant(), id, k = kind, s = scope, v = version, items = Jsonb.From(items), a = context.ActorId }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.NavigationPublished, "nav_menu", id.ToString(), TargetOrgUnitId: ScopeUnit(kind, scope, nodes),
            Changes: new { kind, scope, version, before = existing?.Items.Select(i => i.NodeId), after = items.Select(i => i.NodeId) }), ct);
        await outbox.EnqueueAsync(new NavigationPublished(id, kind, scope, version), ct);
        return new MenuView(id, kind, scope, version, items);
    }

    // Information architecture

    public async Task<IReadOnlyList<NodeView>> ListNodesAsync(CancellationToken ct)
    {
        if (!await authz.HoldsAnywhereAsync(Permissions.NavigationEdit, ct))
        {
            throw AppException.Denied();
        }
        var redirects = await RedirectsAsync(ct);
        var result = new List<NodeView>();
        foreach (var node in (await NodesAsync(ct)).Values.OrderBy(n => n.Slug, StringComparer.Ordinal))
        {
            if ((await authz.CheckAsync(Permissions.NavigationEdit, Target(node.Owner), ct)).Allowed)
            {
                result.Add(View(node, redirects));
            }
        }
        return result;
    }

    public async Task<NodeView> SaveNodeAsync(Guid id, NodeRequest request, CancellationToken ct)
    {
        var nodes = await NodesAsync(ct);
        var existing = nodes.GetValueOrDefault(id);
        if (existing is not null)
        {
            await DemandEditAsync(existing.Owner, ct);
        }
        await DemandEditAsync(request.OwnerOrgUnitId, ct);

        var errors = new ValidationErrors();
        var labels = (request.Labels ?? new Dictionary<string, string>())
            .Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToDictionary(l => l.Key, l => l.Value.Trim(), StringComparer.Ordinal);
        if (labels.Count == 0 || labels.Any(l => !LocaleKey().IsMatch(l.Key) || l.Value.Length > 120))
        {
            errors.Add("labels", "validation.navigation.label");
        }
        var slug = request.Slug?.Trim().ToLowerInvariant() ?? "";
        if (!SlugPattern().IsMatch(slug) || slug.Length > 200)
        {
            errors.Add("slug", "validation.navigation.slug");
        }
        else if (nodes.Values.Any(n => n.Slug == slug && n.Id != id))
        {
            errors.Add("slug", "validation.navigation.slug_taken");
        }
        if ((request.PageId is null) == (request.Url is null))
        {
            errors.Add("target", "validation.navigation.target");
        }
        else if (request.Url is { } url && !(url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)) && !url.StartsWith("https://", StringComparison.Ordinal))
        {
            errors.Add("url", "validation.navigation.url");
        }
        else if (request.PageId is { } pageId
            && !await session.ScalarAsync<bool>("select exists (select 1 from experience.page where id = @id)", new { id = pageId }, ct))
        {
            errors.Add("pageId", "validation.navigation.page_unknown");
        }
        if (request.ParentId is { } parent && (!nodes.ContainsKey(parent) || parent == id || Ancestors(parent, nodes).Contains(id)))
        {
            errors.Add("parentId", "validation.navigation.parent");
        }
        if (request.OwnerOrgUnitId is { } owner && !await hierarchy.UnitExistsAsync(owner, ct))
        {
            errors.Add("ownerOrgUnitId", "validation.navigation.owner");
        }
        errors.ThrowIfAny();

        await session.ExecAsync(
            """
            insert into experience.ia_node (tenant_id, node_id, parent_id, labels, slug, page_id, url, owner_org_unit_id, position)
            values (@t, @id, @p, @labels, @slug, @page, @url, @owner, @pos)
            on conflict (tenant_id, node_id) do update set parent_id = excluded.parent_id, labels = excluded.labels, slug = excluded.slug,
              page_id = excluded.page_id, url = excluded.url, owner_org_unit_id = excluded.owner_org_unit_id, position = excluded.position,
              updated_at = now()
            """,
            new
            {
                t = context.RequireTenant(), id, p = request.ParentId, labels = Jsonb.From(labels), slug, page = request.PageId, url = request.Url,
                owner = request.OwnerOrgUnitId, pos = request.Position,
            }, ct);
        if (existing is not null && existing.Slug != slug)
        {
            // Renaming or moving keeps every old slug working (FR-013). A slug taken again by a node stops redirecting.
            // Redirect rows are only written and removed, never changed in place.
            await session.ExecAsync("delete from experience.slug_redirect where old_slug = @s", new { s = existing.Slug }, ct);
            await session.ExecAsync("insert into experience.slug_redirect (tenant_id, old_slug, node_id) values (@t, @s, @id)",
                new { t = context.RequireTenant(), s = existing.Slug, id }, ct);
        }
        await session.ExecAsync("delete from experience.slug_redirect where old_slug = @s", new { s = slug }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.NavigationNodeChanged, "ia_node", id.ToString(), TargetOrgUnitId: request.OwnerOrgUnitId,
            Changes: new
            {
                before = existing is null ? null : new { existing.Slug, existing.ParentId, existing.PageId, existing.Url },
                after = new { slug, request.ParentId, request.PageId, request.Url },
            }), ct);
        return View(new Node(id, request.ParentId, labels, slug, request.PageId, request.Url, request.OwnerOrgUnitId, request.Position),
            await RedirectsAsync(ct));
    }

    // Resolve, breadcrumbs and quick navigation

    public async Task<ResolveResult> ResolveAsync(string? path, string locale, CancellationToken ct)
    {
        var slug = (path ?? "").Trim('/').ToLowerInvariant();
        var nodes = await NodesAsync(ct);
        var visible = await VisibleNodesAsync(nodes, ct);
        var node = nodes.Values.FirstOrDefault(n => n.Slug == slug);
        if (node is null)
        {
            var redirect = (await RedirectsAsync(ct)).FirstOrDefault(r => r.OldSlug == slug);
            if (redirect.NodeId != Guid.Empty && nodes.TryGetValue(redirect.NodeId, out var target) && visible.Contains(target.Id))
            {
                return new ResolveResult("redirect", null, "/" + target.Slug, target.Id, [], [], []);
            }
            // A page that is not in the IA is reachable by its own slug, under the same visibility rule.
            var page = await PageBySlugAsync(slug, ct);
            if (page is { } p && await CanViewPageAsync(p.OrgUnitId, p.Published, ct))
            {
                return new ResolveResult("page", slug, null, null, [], [], []);
            }
            return Recovery();
        }
        if (!visible.Contains(node.Id))
        {
            return Recovery();
        }
        var crumbs = Ancestors(node.Id, nodes).Reverse().Append(node.Id)
            .Where(visible.Contains)
            .Select(id => new Crumb(Label(nodes[id].Labels, locale) ?? nodes[id].Slug, Href(nodes[id])))
            .ToList();
        var pageSlug = node.PageId is { } pid ? await PageSlugAsync(pid, ct) : null;
        if (node.Url is { } url)
        {
            return new ResolveResult("redirect", null, url, node.Id, crumbs, [], []);
        }

        // The section menu of the nearest ancestor that has one.
        IReadOnlyList<NavLink> section = [];
        var menus = await MenusAsync(ct);
        foreach (var id in new[] { node.Id }.Concat(Ancestors(node.Id, nodes)))
        {
            if (menus.FirstOrDefault(m => m.Kind == "section" && m.Scope == id.ToString()) is { } menu)
            {
                section = await LinksAsync(menu.Items, nodes, visible, locale, ct);
                break;
            }
        }
        return new ResolveResult("page", pageSlug, null, node.Id, crumbs, section, []);
    }

    public async Task<IReadOnlyList<QuickNavItem>> QuickNavAsync(string? q, string locale, CancellationToken ct)
    {
        var term = (q ?? "").Trim();
        if (term.Length is 0 or > 100)
        {
            return [];
        }
        var nodes = await NodesAsync(ct);
        var visible = await VisibleNodesAsync(nodes, ct);
        var owners = await OwnerNamesAsync(nodes.Values, locale, ct);
        var result = nodes.Values
            .Where(n => visible.Contains(n.Id))
            .Where(n => n.Slug.Contains(term, StringComparison.OrdinalIgnoreCase)
                || n.Labels.Values.Any(l => l.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            .Select(n => new QuickNavItem(Label(n.Labels, locale) ?? n.Slug, Href(n), n.Owner is { } o ? owners.GetValueOrDefault(o) : null))
            .ToList();

        // Published pages outside the IA, by slug, until search (spec 012) takes over.
        var inIa = nodes.Values.Where(n => n.PageId is not null).Select(n => n.PageId!.Value).ToHashSet();
        var pages = await session.QueryAsync(
            "select id, slug, org_unit_id from experience.page where current_release is not null and slug ilike @q order by slug limit 50",
            new { q = "%" + term.Replace("%", "").Replace("_", "") + "%" }, r => (Id: r.GetGuid(0), Slug: r.GetString(1), Unit: r.IsDBNull(2) ? (Guid?)null : r.GetGuid(2)), ct);
        foreach (var page in pages.Where(p => !inIa.Contains(p.Id)))
        {
            if (await CanViewPageAsync(page.Unit, true, ct))
            {
                result.Add(new QuickNavItem(page.Slug, "/p/" + page.Slug, null));
            }
        }
        return result.Take(20).ToList();
    }

    // Internals

    private sealed record Node(Guid Id, Guid? ParentId, IReadOnlyDictionary<string, string> Labels, string Slug, Guid? PageId, string? Url,
        Guid? Owner, int Position);

    private async Task<(int Version, ShellDefinition Definition)> LiveShellAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select version, definition::text from experience.shell_config order by version desc limit 1", null,
            r => (Version: r.GetInt32(0), Json: r.GetString(1)), ct);
        return row.Json is null ? (0, DefaultShell) : (row.Version, JsonSerializer.Deserialize<ShellDefinition>(row.Json, JsonDefaults.Options)!);
    }

    private static ShellDefinition ValidateShell(ShellDefinition? request)
    {
        var errors = new ValidationErrors();
        if (request is null)
        {
            errors.Add("definition", "validation.shell.definition");
            errors.ThrowIfAny();
        }
        var placed = new List<string>();
        foreach (var (slot, regions) in new[] { ("start", request!.Start), ("center", request.Center), ("end", request.End), ("below", request.Below) })
        {
            foreach (var region in regions ?? [])
            {
                if (!Regions.Contains(region))
                {
                    errors.Add($"{slot}", "validation.shell.region_unknown");
                }
                else if (placed.Contains(region))
                {
                    errors.Add($"{slot}", "validation.shell.region_twice");
                }
                placed.Add(region);
            }
        }
        foreach (var required in RequiredRegions.Where(r => !placed.Contains(r)))
        {
            errors.Add("regions", "validation.shell.region_required");
        }
        var utilities = request.Utilities ?? [];
        if (utilities.Any(u => !UtilityKeys.Contains(u)) || utilities.Distinct().Count() != utilities.Count)
        {
            errors.Add("utilities", "validation.shell.utility_unknown");
        }
        if (utilities.Count > 0 && !placed.Contains("utilities"))
        {
            errors.Add("utilities", "validation.shell.utilities_unplaced");
        }
        errors.ThrowIfAny();
        return new ShellDefinition(request.Start ?? [], request.Center ?? [], request.End ?? [], request.Below ?? [], utilities);
    }

    private static IReadOnlyList<MenuItemInput> ValidateItems(IReadOnlyList<MenuItemInput>? items, IReadOnlyDictionary<Guid, Node> nodes)
    {
        var errors = new ValidationErrors();
        var list = items ?? [];
        var all = list.Concat(list.SelectMany(i => i.Children ?? [])).ToList();
        if (all.Count > MaxMenuItems)
        {
            errors.Add("items", "validation.navigation.too_many");
        }
        if (list.Any(i => (i.Children ?? []).Any(c => c.Children is { Count: > 0 })))
        {
            errors.Add("items", "validation.navigation.too_deep");
        }
        if (all.Any(i => !nodes.ContainsKey(i.NodeId)))
        {
            errors.Add("items", "validation.navigation.node_unknown");
        }
        errors.ThrowIfAny();
        return list.Select(i => new MenuItemInput(i.NodeId, (i.Children ?? []).Select(c => new MenuItemInput(c.NodeId, [])).ToList())).ToList();
    }

    private async Task<IReadOnlyDictionary<Guid, Node>> NodesAsync(CancellationToken ct) =>
        (await session.QueryAsync(
            "select node_id, parent_id, labels::text, slug, page_id, url, owner_org_unit_id, position from experience.ia_node", null,
            r => new Node(r.GetGuid(0), r.IsDBNull(1) ? null : r.GetGuid(1),
                JsonSerializer.Deserialize<Dictionary<string, string>>(r.GetString(2), JsonDefaults.Options)!, r.GetString(3),
                r.IsDBNull(4) ? null : r.GetGuid(4), r.IsDBNull(5) ? null : r.GetString(5), r.IsDBNull(6) ? null : r.GetGuid(6), r.GetInt32(7)), ct))
        .ToDictionary(n => n.Id);

    private async Task<IReadOnlyList<MenuView>> MenusAsync(CancellationToken ct) =>
        await session.QueryAsync("select menu_id, kind, scope, version, items::text from experience.nav_menu", null,
            r => new MenuView(r.GetGuid(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
                JsonSerializer.Deserialize<List<MenuItemInput>>(r.GetString(4), JsonDefaults.Options)!), ct);

    private async Task<IReadOnlyList<(string OldSlug, Guid NodeId)>> RedirectsAsync(CancellationToken ct) =>
        await session.QueryAsync("select old_slug, node_id from experience.slug_redirect", null, r => (r.GetString(0), r.GetGuid(1)), ct);

    /// <summary>Nodes the caller may open: URL nodes always, page nodes when the page is published and viewable, and only under visible parents.</summary>
    private async Task<HashSet<Guid>> VisibleNodesAsync(IReadOnlyDictionary<Guid, Node> nodes, CancellationToken ct)
    {
        var pageIds = nodes.Values.Where(n => n.PageId is not null).Select(n => n.PageId!.Value).Distinct().ToArray();
        var pages = (await session.QueryAsync("select id, org_unit_id, current_release is not null from experience.page where id = any(@ids)",
            new { ids = pageIds }, r => (Id: r.GetGuid(0), Unit: r.IsDBNull(1) ? (Guid?)null : r.GetGuid(1), Published: r.GetBoolean(2)), ct))
            .ToDictionary(p => p.Id);
        var viewable = new Dictionary<Guid, bool>(); // Guid.Empty stands for the tenant scope.
        var own = new HashSet<Guid>();
        foreach (var node in nodes.Values)
        {
            if (node.PageId is not { } pid)
            {
                own.Add(node.Id);
                continue;
            }
            if (pages.TryGetValue(pid, out var page) && page.Published)
            {
                if (!viewable.TryGetValue(page.Unit ?? Guid.Empty, out var allowed))
                {
                    allowed = viewable[page.Unit ?? Guid.Empty] = (await authz.CheckAsync(Permissions.PageView, Target(page.Unit), ct)).Allowed;
                }
                if (allowed)
                {
                    own.Add(node.Id);
                }
            }
        }
        return own.Where(id => Ancestors(id, nodes).All(own.Contains)).ToHashSet();
    }

    private async Task<IReadOnlyList<NavLink>> LinksAsync(IReadOnlyList<MenuItemInput> items, IReadOnlyDictionary<Guid, Node> nodes,
        HashSet<Guid> visible, string locale, CancellationToken ct)
    {
        var owners = await OwnerNamesAsync(nodes.Values, locale, ct);
        NavLink? Link(MenuItemInput item) =>
            nodes.TryGetValue(item.NodeId, out var node) && visible.Contains(node.Id)
                ? new NavLink(node.Id, Label(node.Labels, locale) ?? node.Slug, Href(node), node.Owner is { } o ? owners.GetValueOrDefault(o) : null,
                    (item.Children ?? []).Select(Link).OfType<NavLink>().ToList())
                : null;
        return items.Select(Link).OfType<NavLink>().ToList();
    }

    private async Task<IReadOnlyDictionary<Guid, string>> OwnerNamesAsync(IEnumerable<Node> nodes, string locale, CancellationToken ct)
    {
        var owners = nodes.Where(n => n.Owner is not null).Select(n => n.Owner!.Value).Distinct().ToList();
        if (owners.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }
        var names = await hierarchy.GetNamesAsync(owners, ct);
        return owners.ToDictionary(o => o, o => Label(names.GetValueOrDefault(o), locale) ?? o.ToString());
    }

    private static string Href(Node node) => node.Url ?? "/" + node.Slug;

    private static string? Label(IReadOnlyDictionary<string, string>? labels, string locale) =>
        labels is null || labels.Count == 0 ? null
            : labels.TryGetValue(locale, out var l) ? l
            : labels.TryGetValue("en", out var en) ? en
            : labels.OrderBy(x => x.Key, StringComparer.Ordinal).First().Value;

    private static IEnumerable<Guid> Ancestors(Guid id, IReadOnlyDictionary<Guid, Node> nodes)
    {
        var seen = new HashSet<Guid> { id };
        var current = nodes.GetValueOrDefault(id)?.ParentId;
        while (current is { } c && seen.Add(c) && nodes.TryGetValue(c, out var parent))
        {
            yield return c;
            current = parent.ParentId;
        }
    }

    private static ResolveResult Recovery() =>
        new("recovery", null, null, null, [], [], [new Crumb("home", "/"), new Crumb("search", "/search")]);

    private async Task<(Guid? OrgUnitId, bool Published)?> PageBySlugAsync(string slug, CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select org_unit_id, current_release is not null from experience.page where slug = @slug",
            new { slug }, r => (Found: true, Unit: r.IsDBNull(0) ? (Guid?)null : r.GetGuid(0), Published: r.GetBoolean(1)), ct);
        return row.Found ? (row.Unit, row.Published) : null;
    }

    private async Task<string?> PageSlugAsync(Guid pageId, CancellationToken ct) =>
        await session.ScalarAsync<string?>("select slug from experience.page where id = @id", new { id = pageId }, ct);

    private async Task<bool> CanViewPageAsync(Guid? unit, bool published, CancellationToken ct) =>
        published && (await authz.CheckAsync(Permissions.PageView, Target(unit), ct)).Allowed;

    private static NodeView View(Node node, IReadOnlyList<(string OldSlug, Guid NodeId)> redirects) =>
        new(node.Id, node.ParentId, node.Labels, node.Slug, node.PageId, node.Url, node.Owner, node.Position,
            redirects.Where(r => r.NodeId == node.Id).Select(r => r.OldSlug).Order(StringComparer.Ordinal).ToList());

    private static AuthzTarget Target(Guid? unit) => unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    private static Guid? ScopeUnit(string kind, string scope, IReadOnlyDictionary<Guid, Node> nodes) => kind switch
    {
        "organization" => Guid.Parse(scope),
        "section" => nodes[Guid.Parse(scope)].Owner,
        _ => null,
    };

    private async Task<bool> CanEditScopeAsync(string kind, string scope, IReadOnlyDictionary<Guid, Node> nodes, CancellationToken ct) =>
        kind switch
        {
            "global" => (await authz.CheckAsync(Permissions.NavigationEdit, AuthzTarget.Tenant, ct)).Allowed,
            "organization" => (await authz.CheckAsync(Permissions.NavigationEdit, AuthzTarget.OrgUnit(Guid.Parse(scope)), ct)).Allowed,
            "section" => nodes.TryGetValue(Guid.Parse(scope), out var node)
                && (await authz.CheckAsync(Permissions.NavigationEdit, Target(node.Owner), ct)).Allowed,
            _ => false,
        };

    /// <summary>Only known kinds and scopes; a scope the caller cannot edit, or of another tenant, is not found.</summary>
    private async Task DemandScopeAsync(string kind, string scope, IReadOnlyDictionary<Guid, Node> nodes, CancellationToken ct)
    {
        var valid = kind switch
        {
            "global" => scope == TenantScope,
            "organization" => Guid.TryParse(scope, out var unit) && await hierarchy.UnitExistsAsync(unit, ct),
            "section" => Guid.TryParse(scope, out var node) && nodes.ContainsKey(node),
            _ => false,
        };
        if (!valid)
        {
            throw AppException.NotFound();
        }
        if (!await CanEditScopeAsync(kind, scope, nodes, ct))
        {
            throw kind == "global" ? AppException.Denied() : AppException.NotFound();
        }
    }

    private async Task DemandEditAsync(Guid? owner, CancellationToken ct)
    {
        if ((await authz.CheckAsync(Permissions.NavigationEdit, Target(owner), ct)).Allowed)
        {
            return;
        }
        throw owner is null ? AppException.Denied() : AppException.NotFound();
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*(/[a-z0-9]+(-[a-z0-9]+)*)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex("^[a-z]{2}$")]
    private static partial Regex LocaleKey();
}
