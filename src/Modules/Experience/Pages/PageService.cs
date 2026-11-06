using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Caching.Memory;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Messaging;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Components;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Pages;

public sealed record CreatePageRequest(string? TemplateId, string? Slug, Guid? OrgUnitId, string? Variant);

public sealed record SaveDraftRequest(PageDefinition? Definition);

public sealed record PublishRequest(string? Reason);

public sealed record RollbackRequest(int Release, string? Reason);

public sealed record PageView(Guid Id, string Slug, Guid? OrgUnitId, string TemplateId, string TemplateVersion, int? CurrentRelease,
    string ETag, PageDefinition Draft, IReadOnlyList<Finding> Findings);

public sealed record ReleaseView(int Release, DateTimeOffset PublishedAt, Guid PublishedBy, string? Reason, int? RollbackOf);

public sealed record PickerItem(string ComponentRef, string Version, string NameKey, string DescriptionKey, string Category, string Icon,
    IReadOnlyList<string> RequiredPermissions, bool Eligible, string? ReasonKey);

/// <summary>A placement as the renderer receives it: only declared context values, defaults applied (FR-025, FR-028).</summary>
public sealed record RenderedPlacement(string Id, string ComponentRef, string State, int Span, IReadOnlyDictionary<string, JsonElement> Props,
    IReadOnlyDictionary<string, string> Context, IReadOnlyList<string> Publishes, IReadOnlyList<string> Subscribes);

public sealed record RenderedRegion(string Id, int Span, string Section, IReadOnlyList<RenderedPlacement> Placements);

public sealed record Provenance(string Template, string Layout, int Release, Guid PublishedBy, DateTimeOffset PublishedAt);

/// <param name="ThemeScope">The scope whose effective theme the page uses (spec 033): <c>tenant</c> or the owning org unit.</param>
public sealed record RenderedPage(Guid PageId, int Release, string Template, string Layout, string Variant, string Fallback,
    IReadOnlyList<RenderedRegion> Regions, Provenance? Provenance, string ThemeScope);

/// <summary>
/// Page lifecycle (spec 032 US1, US5, US6, US8): create from a template, save drafts with optimistic concurrency, validate,
/// publish immutable releases, roll back as a new release, and render the latest release for a viewer. Pages of another
/// tenant are never visible (row-level security); pages outside the caller's scope answer not found (FR-056).
/// </summary>
public sealed partial class PageService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    PlatformCatalogue catalogue,
    ComponentRegistry registry,
    ComponentEnablement enablement,
    IAuditWriter audit,
    IOutbox outbox,
    IMemoryCache cache,
    IClock clock)
{
    private sealed record PageRow(Guid Id, Guid? OrgUnitId, string Slug, string TemplateId, string TemplateVersion, int? CurrentRelease);

    public async Task<PageView> CreateAsync(CreatePageRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var errors = new ValidationErrors();
        if (request.TemplateId is null || !catalogue.Templates.TryGetValue(request.TemplateId, out var template))
        {
            errors.Add("templateId", "validation.page.template_unknown");
            template = null;
        }
        else if (template.State != "released")
        {
            errors.Add("templateId", "validation.page.template_not_released");
        }
        if (request.Slug is null || !Slug().IsMatch(request.Slug))
        {
            errors.Add("slug", "validation.page.slug_invalid");
        }
        if (template is not null && request.Variant is not null && !template.Variants.Contains(request.Variant))
        {
            errors.Add("variant", "validation.page.variant_invalid");
        }
        errors.ThrowIfAny();
        if (request.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
        {
            throw AppException.Validation("orgUnitId", "validation.page.org_unit_unknown");
        }
        await authz.DemandAsync(Permissions.PageEdit, Target(request.OrgUnitId), ct);

        var taken = await session.ScalarAsync<Guid?>("select id from experience.page where slug = @s", new { s = request.Slug }, ct);
        if (taken is not null)
        {
            throw AppException.Conflict("conflict.page.slug_taken");
        }
        var id = Ids.New();
        var actor = context.RequireUser();
        var definition = PageValidator.FromTemplate(template!, catalogue.Layouts[template!.Layout], request.Variant);
        await session.ExecAsync(
            """
            insert into experience.page (tenant_id, id, org_unit_id, slug, owner_id, template_id, template_version)
            values (@t, @id, @unit, @slug, @owner, @template, @version)
            """,
            new { t = context.RequireTenant(), id, unit = request.OrgUnitId, slug = request.Slug, owner = actor, template = template.Id, version = template.Version }, ct);
        await session.ExecAsync(
            "insert into experience.page_draft (tenant_id, page_id, revision, definition, updated_by) values (@t, @id, 1, @d, @u)",
            new { t = context.RequireTenant(), id, d = Jsonb.From(definition), u = actor }, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PageCreated, "page", id.ToString(), TargetOrgUnitId: request.OrgUnitId,
            Changes: new { slug = request.Slug, template = $"{template.Id}@{template.Version}", layout = template.Layout }), ct);
        return await GetAsync(id, ct);
    }

    public async Task<PageView> GetAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await EditableAsync(id, Permissions.PageEdit, ct);
        var (revision, draft) = await DraftAsync(id, ct);
        var findings = await ValidateAsync(page, draft, ValidationMode.Save, ct);
        return View(page, revision, draft, findings);
    }

    public async Task<PageView> SaveDraftAsync(Guid id, string? ifMatch, SaveDraftRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await EditableAsync(id, Permissions.PageEdit, ct);
        if (request.Definition is null)
        {
            throw AppException.Validation("definition", "validation.page.definition_required");
        }
        var (revision, current) = await DraftAsync(id, ct, forUpdate: true);
        if (ifMatch is null || ifMatch != ETag(revision))
        {
            // Both versions are kept: the caller still has its own, and gets the stored one here (FR-042).
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.page.draft_changed", null,
                new Dictionary<string, object?> { ["etag"] = ETag(revision), ["current"] = current });
        }
        var findings = await ValidateAsync(page, request.Definition, ValidationMode.Save, ct);
        ThrowIfBlocking(findings);
        await session.ExecAsync(
            "update experience.page_draft set revision = @r, definition = @d, updated_by = @u, updated_at = @now where page_id = @id",
            new { r = revision + 1, d = Jsonb.From(request.Definition), u = context.RequireUser(), now = clock.UtcNow, id }, ct);
        return View(page, revision + 1, request.Definition, findings);
    }

    public async Task<IReadOnlyList<Finding>> ValidateDraftAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await EditableAsync(id, Permissions.PageEdit, ct);
        var (_, draft) = await DraftAsync(id, ct);
        return await ValidateAsync(page, draft, ValidationMode.Publish, ct);
    }

    public async Task<ReleaseView> PublishAsync(Guid id, PublishRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await EditableAsync(id, Permissions.PagePublish, ct, audited: true);
        var (_, draft) = await DraftAsync(id, ct);
        var findings = await ValidateAsync(page, draft, ValidationMode.Publish, ct);
        ThrowIfBlocking(findings);
        var release = await InsertReleaseAsync(page, draft, request.Reason, rollbackOf: null, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PagePublished, "page", id.ToString(), TargetOrgUnitId: page.OrgUnitId,
            Changes: new { release = release.Release, previous = page.CurrentRelease, reason = request.Reason }), ct);
        await outbox.EnqueueAsync(new PagePublished(id, release.Release), ct);
        return release;
    }

    public async Task<ReleaseView> RollbackAsync(Guid id, RollbackRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await EditableAsync(id, Permissions.PagePublish, ct, audited: true);
        var definition = await session.QuerySingleOrDefaultAsync(
            "select definition::text as d from experience.page_release where page_id = @id and release = @r",
            new { id, r = request.Release }, r => r.Get<string>("d")!, ct);
        if (definition is null)
        {
            throw AppException.Validation("release", "validation.page.release_unknown");
        }
        var restored = JsonSerializer.Deserialize<PageDefinition>(definition, JsonDefaults.Options)!;
        var release = await InsertReleaseAsync(page, restored, request.Reason, request.Release, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PageRolledBack, "page", id.ToString(), TargetOrgUnitId: page.OrgUnitId,
            Changes: new { release = release.Release, restored = request.Release, previous = page.CurrentRelease, reason = request.Reason }), ct);
        await outbox.EnqueueAsync(new PageRolledBack(id, release.Release, request.Release), ct);
        return release;
    }

    public async Task<IReadOnlyList<ReleaseView>> ReleasesAsync(Guid id, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        await EditableAsync(id, Permissions.PageEdit, ct);
        return await session.QueryAsync(
            "select release, published_at, published_by, reason, rollback_of from experience.page_release where page_id = @id order by release desc",
            new { id }, r => new ReleaseView(r.Get<int>("release"), r.Get<DateTimeOffset>("published_at"), r.Get<Guid>("published_by"),
                r.Get<string>("reason"), r.Get<int?>("rollback_of")), ct);
    }

    /// <summary>Components for the picker, with the reason each ineligible one is disabled (FR-037).</summary>
    public async Task<IReadOnlyList<PickerItem>> PickerAsync(Guid id, string? regionId, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperiencePageBuilder, ct);
        var page = await EditableAsync(id, Permissions.PageEdit, ct);
        var template = catalogue.Templates[page.TemplateId];
        var policy = template.Regions.FirstOrDefault(r => r.Id == regionId)?.Policy;
        if (regionId is not null && policy is null)
        {
            throw AppException.Validation("region", "validation.page.region_unknown");
        }
        var span = regionId is null ? 12 : catalogue.Layouts[template.Layout].Regions.First(r => r.Id == regionId).Span;
        var availability = await AvailabilityAsync(page.OrgUnitId, ct);
        var items = new List<PickerItem>();
        foreach (var m in registry.All.Where(m => m.State != ComponentStates.Retired))
        {
            string? reason = availability(m) switch
            {
                Availability.Disabled => "validation.page.component_disabled",
                Availability.NotEntitled => "validation.page.component_not_entitled",
                Availability.NotPermitted => "validation.page.component_not_permitted",
                _ => null,
            };
            if (reason is null && policy is { AllowedCategories.Count: > 0 } && !policy.AllowedCategories.Contains(m.Display.Category))
            {
                reason = "validation.page.category_not_allowed";
            }
            if (reason is null && m.Placement.MinSpan > span)
            {
                reason = "validation.page.region_too_narrow";
            }
            items.Add(new PickerItem(m.Reference, m.Version, m.Display.NameKey, m.Display.DescriptionKey, m.Display.Category, m.Display.Icon,
                m.Requires.Permissions, reason is null, reason));
        }
        return items;
    }

    /// <summary>The latest release of a page for the current viewer, with the effective structure (FR-002, FR-028, FR-066).</summary>
    public async Task<RenderedPage> RenderAsync(string slug, string locale, string direction, CancellationToken ct)
    {
        var page = await session.QuerySingleOrDefaultAsync(PageSelect + " where slug = @slug", new { slug }, MapPage, ct);
        if (page?.CurrentRelease is not { } current)
        {
            throw AppException.NotFound();
        }
        await authz.DemandVisibleAsync(Permissions.PageView, Target(page.OrgUnitId), ct);
        var snapshot = await ReleaseAsync(page.Id, current, ct);
        var template = catalogue.Templates[snapshot.Definition.Template.Id];
        var layout = catalogue.Layouts[template.Layout];
        var availability = await AvailabilityAsync(page.OrgUnitId, ct, viewer: true);
        var known = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["tenantId"] = context.RequireTenant().ToString(),
            ["pageId"] = page.Id.ToString(),
            ["locale"] = locale,
            ["direction"] = direction,
        };
        if (page.OrgUnitId is { } unit)
        {
            known["orgUnitId"] = unit.ToString();
        }

        var regions = layout.Regions.Select(lr =>
        {
            var placements = snapshot.Definition.Regions.FirstOrDefault(r => r.Id == lr.Id)?.Placements ?? [];
            return new RenderedRegion(lr.Id, lr.Span, lr.Section, placements.Select(p =>
            {
                var manifest = registry.Find(p.ComponentRef);
                if (manifest is null || manifest.State == ComponentStates.Retired || availability(manifest) != Availability.Available)
                {
                    // Viewers see "no longer available"; nothing about the component's configuration leaves the server.
                    return new RenderedPlacement(p.Id, p.ComponentRef, "unavailable", p.Span ?? lr.Span,
                        new Dictionary<string, JsonElement>(), new Dictionary<string, string>(), [], []);
                }
                return new RenderedPlacement(p.Id, p.ComponentRef, "ready", p.Span ?? Math.Min(lr.Span, manifest.Placement.MaxSpan),
                    PropertyValidator.WithDefaults(manifest.Properties, p.Props),
                    manifest.Context.Where(known.ContainsKey).ToDictionary(k => k, k => known[k], StringComparer.Ordinal),
                    manifest.Events.Publishes.Select(e => e.Name).ToList(), manifest.Events.Subscribes.Select(e => e.Name).ToList());
            }).ToList());
        }).ToList();

        var provenance = (await authz.CheckAsync(Permissions.ConfigHistoryView, Target(page.OrgUnitId), ct)).Allowed
            ? new Provenance($"{template.Id}@{template.Version}", $"{layout.Id}@{layout.Version}", current, snapshot.PublishedBy, snapshot.PublishedAt)
            : null;
        return new RenderedPage(page.Id, current, template.Id, layout.Id, snapshot.Definition.Variant, layout.Fallback, regions, provenance,
            page.OrgUnitId?.ToString() ?? "tenant");
    }

    private sealed record Snapshot(PageDefinition Definition, Guid PublishedBy, DateTimeOffset PublishedAt);

    /// <summary>Releases are immutable, so a snapshot is cached by tenant, page and release with no invalidation (FR-065).</summary>
    private async Task<Snapshot> ReleaseAsync(Guid pageId, int release, CancellationToken ct)
    {
        var key = ("experience.release", context.RequireTenant(), pageId, release);
        if (cache.TryGetValue(key, out Snapshot? hit) && hit is not null)
        {
            return hit;
        }
        var snapshot = await session.QuerySingleOrDefaultAsync(
            "select definition::text as d, published_by, published_at from experience.page_release where page_id = @pageId and release = @release",
            new { pageId, release }, r => new Snapshot(JsonSerializer.Deserialize<PageDefinition>(r.Get<string>("d")!, JsonDefaults.Options)!,
                r.Get<Guid>("published_by"), r.Get<DateTimeOffset>("published_at")), ct) ?? throw AppException.NotFound();
        cache.Set(key, snapshot, new MemoryCacheEntryOptions { SlidingExpiration = TimeSpan.FromMinutes(30), Size = 1 });
        return snapshot;
    }

    private async Task<ReleaseView> InsertReleaseAsync(PageRow page, PageDefinition definition, string? reason, int? rollbackOf, CancellationToken ct)
    {
        if (reason is { Length: > 500 })
        {
            throw AppException.Validation("reason", "validation.page.reason_too_long");
        }
        // The page row lock serializes publishes, so release numbers never collide.
        var current = await session.ScalarAsync<int?>("select current_release from experience.page where id = @id for update", new { id = page.Id }, ct);
        var release = (current ?? 0) + 1;
        var actor = context.RequireUser();
        var now = clock.UtcNow;
        await session.ExecAsync(
            """
            insert into experience.page_release (tenant_id, page_id, release, definition, published_by, published_at, reason, rollback_of)
            values (@t, @id, @release, @d, @actor, @now, @reason, @rollbackOf)
            """,
            new { t = context.RequireTenant(), id = page.Id, release, d = Jsonb.From(definition), actor, now, reason, rollbackOf }, ct);
        await session.ExecAsync("update experience.page set current_release = @release where id = @id", new { release, id = page.Id }, ct);
        return new ReleaseView(release, now, actor, reason, rollbackOf);
    }

    private async Task<List<Finding>> ValidateAsync(PageRow page, PageDefinition definition, ValidationMode mode, CancellationToken ct)
    {
        var availability = await AvailabilityAsync(page.OrgUnitId, ct);
        return PageValidator.Validate(definition, page.TemplateId, catalogue, registry, availability, mode);
    }

    /// <summary>
    /// Whether each component can be used: enabled by the tenant, its capabilities in the package, and (for editors) the
    /// permissions it declares held at the page's scope. Viewers are not checked for the component's permissions here: the
    /// component's own API authorizes every call (FR-027).
    /// </summary>
    private async Task<Func<ComponentManifest, Availability>> AvailabilityAsync(Guid? orgUnitId, CancellationToken ct, bool viewer = false)
    {
        var disabled = await enablement.DisabledAsync(ct);
        var capabilities = new Dictionary<string, bool>(StringComparer.Ordinal);
        var permissions = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var m in registry.All)
        {
            foreach (var c in m.Requires.Capabilities.Where(c => !capabilities.ContainsKey(c)))
            {
                capabilities[c] = await entitlements.IsEnabledAsync(c, ct);
            }
            if (!viewer)
            {
                foreach (var p in m.Requires.Permissions.Where(p => !permissions.ContainsKey(p)))
                {
                    permissions[p] = (await authz.CheckAsync(p, Target(orgUnitId), ct)).Allowed;
                }
            }
        }
        return m =>
            disabled.Contains(m.Id) ? Availability.Disabled
            : m.Requires.Capabilities.Any(c => !capabilities[c]) ? Availability.NotEntitled
            : !viewer && m.Requires.Permissions.Any(p => !permissions[p]) ? Availability.NotPermitted
            : Availability.Available;
    }

    private async Task<PageRow> EditableAsync(Guid id, string permission, CancellationToken ct, bool audited = false)
    {
        var page = await session.QuerySingleOrDefaultAsync(PageSelect + " where id = @id", new { id }, MapPage, ct);
        if (page is null)
        {
            throw AppException.NotFound();
        }
        var decision = await authz.CheckAsync(permission, Target(page.OrgUnitId), ct);
        if (!decision.Allowed)
        {
            if (audited)
            {
                // A delegated editor reaching outside their scope is recorded (US6 scenario 5), then sees not found.
                await audit.WriteAsync(new AuditEntry(permission == Permissions.PagePublish ? AuditActions.PagePublished : AuditActions.PageRolledBack,
                    "page", id.ToString(), AuditOutcome.Denied, page.OrgUnitId), ct);
                await session.CommitAsync(ct);
            }
            throw AppException.NotFound();
        }
        return page;
    }

    private async Task<(int Revision, PageDefinition Draft)> DraftAsync(Guid id, CancellationToken ct, bool forUpdate = false)
    {
        var row = await session.QuerySingleOrDefaultAsync(
            "select revision, definition::text as d from experience.page_draft where page_id = @id" + (forUpdate ? " for update" : ""),
            new { id }, r => (r.Get<int>("revision"), r.Get<string>("d")!), ct);
        return (row.Item1, JsonSerializer.Deserialize<PageDefinition>(row.Item2, JsonDefaults.Options)!);
    }

    private static void ThrowIfBlocking(IReadOnlyList<Finding> findings)
    {
        if (findings.Any(f => f.Severity == Severities.Blocking))
        {
            throw new AppException(ProblemCodes.ValidationFailed, 422, "validation.page.invalid", null,
                new Dictionary<string, object?> { ["findings"] = findings });
        }
    }

    private static PageView View(PageRow page, int revision, PageDefinition draft, IReadOnlyList<Finding> findings) =>
        new(page.Id, page.Slug, page.OrgUnitId, page.TemplateId, page.TemplateVersion, page.CurrentRelease, ETag(revision), draft, findings);

    public static string ETag(int revision) => $"\"r{revision}\"";

    private static AuthzTarget Target(Guid? orgUnitId) => orgUnitId is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    private const string PageSelect = "select id, org_unit_id, slug, template_id, template_version, current_release from experience.page";

    private static PageRow MapPage(Npgsql.NpgsqlDataReader r) => new(r.Get<Guid>("id"), r.Get<Guid?>("org_unit_id"), r.Get<string>("slug")!,
        r.Get<string>("template_id")!, r.Get<string>("template_version")!, r.Get<int?>("current_release"));

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*(/[a-z0-9]+(-[a-z0-9]+)*)*$")]
    private static partial Regex Slug();
}
