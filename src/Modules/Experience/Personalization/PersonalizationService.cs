using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Audit.Contracts;
using TeamBeats.Authorization.Contracts;
using TeamBeats.Entitlements.Contracts;
using TeamBeats.Localization;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Components;
using TeamBeats.Modules.Experience.Contracts;
using TeamBeats.Modules.Experience.Pages;
using TeamBeats.Modules.Tenancy.Contracts;
using TeamBeats.Persistence;
using TeamBeats.Primitives;
using TeamBeats.TenantContext;

namespace TeamBeats.Modules.Experience.Personalization;

public sealed record PreferenceSetting(string Key, IReadOnlyList<string> Values, string Default, string Scope);

public sealed record PreferenceProfileView(int SchemaVersion, IReadOnlyDictionary<string, string> Values, IReadOnlyList<PreferenceSetting> Schema,
    DateTimeOffset? UpdatedAt);

public sealed record PreferenceProfileRequest(IReadOnlyDictionary<string, string>? Values);

public sealed record LayoutView(Guid PageId, int Revision, string ETag, IReadOnlyList<LayoutOperation> Operations, IReadOnlyList<SkippedOperation> Skipped,
    IReadOnlyList<RegionRights> Regions);

public sealed record LayoutRequest(IReadOnlyList<LayoutOperation>? Operations);

/// <summary>A component of the add panel, with its name, description and any refusal reason in the caller's locale.</summary>
public sealed record AddPanelItem(string ComponentRef, string NameKey, string DescriptionKey, string Category, string Icon, bool Eligible, string? ReasonKey,
    string Name = "", string Description = "", string? Reason = null);

public sealed record PolicyRegionRequest(string? RegionId, IReadOnlyList<string>? Rights, Guid? AudienceRuleId, int? MaxPersonalPlacements);

public sealed record PolicyRequest(IReadOnlyList<PolicyRegionRequest>? Regions);

public sealed record AudienceRuleRequest(IReadOnlyDictionary<string, string>? Labels, AudienceConditions? Conditions);

public sealed record Persona(string? Name, Guid? OrgUnitId, string? MembershipType, string? Locale, IReadOnlyDictionary<string, string>? Attributes);

public sealed record PreviewRequest(Guid? PageId, IReadOnlyList<Persona>? Personas);

public sealed record PersonaRegion(string RegionId, IReadOnlyList<string> Placements);

public sealed record PersonaResult(string Name, IReadOnlyList<Guid> MatchedRules, IReadOnlyList<PersonaRegion> Regions);

/// <summary>
/// Spec 008 preferences and personalization (R1): the preference profile, personal layouts over published pages, the
/// add panel, the personalization policy per page and region, audience rules and the targeting preview. Protection,
/// policy and precedence are decided here, never by the frontend; refused operations answer 422 with the reason.
/// </summary>
public sealed partial class PersonalizationService(
    IDbSession session,
    ITenantContext context,
    IAuthorizationService authz,
    IEntitlementService entitlements,
    IOrganizationHierarchy hierarchy,
    PlatformCatalogue catalogue,
    Templates.TemplateStore templates,
    ComponentRegistry registry,
    ComponentAvailability availability,
    PageService pages,
    LayoutEngine layouts,
    AudienceEvaluator audiences,
    IAuditWriter audit,
    IClock clock,
    ILocalizer localizer,
    RequestLocale locale,
    HomeDefaults homeDefaults)
{
    public const int PreferenceSchemaVersion = 1;
    public const int LayoutSchemaVersion = 1;

    /// <summary>The preference schema (FR-021, FR-032). Every R1 preference is cross-device; language stays on the user account.</summary>
    public static readonly IReadOnlyList<PreferenceSetting> PreferenceSchema =
    [
        new("mode", ["system", "light", "dark"], "system", "cross_device"),
        new("density", ["comfortable", "compact"], "comfortable", "cross_device"),
        new("motion", ["system", "reduced"], "system", "cross_device"),
        new("text_scale", ["100", "125", "150", "200"], "100", "cross_device"),
    ];

    // Preference profile

    public async Task<PreferenceProfileView> GetProfileAsync(CancellationToken ct)
    {
        var row = await session.QuerySingleOrDefaultAsync("select values::text, updated_at from experience.preference_profile where user_id = @u",
            new { u = context.RequireUser() }, r => (Json: r.GetString(0), At: r.GetFieldValue<DateTimeOffset>(1)), ct);
        var saved = row.Json is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(row.Json, JsonDefaults.Options)!;
        // Values that left the schema are dropped on read; defaults fill the rest.
        var values = PreferenceSchema.ToDictionary(s => s.Key, s => saved.TryGetValue(s.Key, out var v) && s.Values.Contains(v) ? v : s.Default, StringComparer.Ordinal);
        return new PreferenceProfileView(PreferenceSchemaVersion, values, PreferenceSchema, row.Json is null ? null : row.At);
    }

    public async Task<PreferenceProfileView> SaveProfileAsync(PreferenceProfileRequest request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in request.Values ?? new Dictionary<string, string>())
        {
            var setting = PreferenceSchema.FirstOrDefault(s => s.Key == key);
            if (setting is null)
            {
                errors.Add($"values.{key}", "validation.preferences.unknown");
            }
            else if (!setting.Values.Contains(value))
            {
                errors.Add($"values.{key}", "validation.preferences.value");
            }
            else
            {
                values[key] = value;
            }
        }
        errors.ThrowIfAny();
        await session.ExecAsync(
            """
            insert into experience.preference_profile (tenant_id, user_id, schema_version, values, updated_at)
            values (@t, @u, @v, @values, @now)
            on conflict (tenant_id, user_id) do update set schema_version = excluded.schema_version,
              values = experience.preference_profile.values || excluded.values, updated_at = excluded.updated_at
            """,
            new { t = context.RequireTenant(), u = context.RequireUser(), v = PreferenceSchemaVersion, values = Jsonb.From(values), now = clock.UtcNow }, ct);
        return await GetProfileAsync(ct);
    }

    // Personal layouts

    public async Task<LayoutView> GetLayoutAsync(Guid pageId, CancellationToken ct)
    {
        var page = await pages.PublishedForViewerAsync(pageId, ct);
        var (revision, operations) = await layouts.OperationsAsync(context.RequireUser(), page.Id, ct);
        var (regions, rights, defaults) = await ViewerLayoutAsync(page, ct);
        var result = LayoutEngine.Apply(regions, operations, rights, defaults: defaults);
        return new LayoutView(page.Id, revision, ETag(revision), operations, result.Skipped, rights.Values.ToList());
    }

    public async Task<LayoutView> SaveLayoutAsync(Guid pageId, string? ifMatch, LayoutRequest request, CancellationToken ct)
    {
        var page = await pages.PublishedForViewerAsync(pageId, ct);
        await authz.DemandAsync(Permissions.PersonalLayoutEdit, Target(page.OrgUnitId), ct);
        var operations = request.Operations ?? [];
        var errors = new ValidationErrors();
        if (operations.Count > LayoutEngine.MaxOperations)
        {
            errors.Add("operations", "validation.personalization.too_many");
        }
        foreach (var (op, i) in operations.Select((o, i) => (o, i)))
        {
            if (op.Op == LayoutEngine.Add && (op.PlacementId is null || !PersonalId().IsMatch(op.PlacementId)))
            {
                errors.Add($"operations[{i}].placementId", "validation.personalization.placement_id");
            }
        }
        errors.ThrowIfAny();

        var (revision, _) = await layouts.OperationsAsync(context.RequireUser(), page.Id, ct);
        if (ifMatch is not null && ifMatch != ETag(revision))
        {
            throw new AppException(ProblemCodes.Conflict, 409, "conflict.personalization.layout_changed", null,
                new Dictionary<string, object?> { ["etag"] = ETag(revision) });
        }
        var (regions, rights, defaults) = await ViewerLayoutAsync(page, ct);
        var check = await NewPlacementCheckAsync(page, ct);
        var result = LayoutEngine.Apply(regions, operations, rights, check, defaults);
        if (result.Skipped.Count > 0)
        {
            throw new AppException(ProblemCodes.ValidationFailed, 422, "validation.personalization.refused", null,
                new Dictionary<string, object?> { ["refused"] = result.Skipped });
        }
        var next = revision + 1;
        await session.ExecAsync(
            """
            insert into experience.personal_layout (tenant_id, user_id, page_id, schema_version, revision, operations, updated_at)
            values (@t, @u, @p, @v, @rev, @ops, @now)
            on conflict (tenant_id, user_id, page_id) do update set schema_version = excluded.schema_version, revision = excluded.revision,
              operations = excluded.operations, updated_at = excluded.updated_at
            """,
            new { t = context.RequireTenant(), u = context.RequireUser(), p = page.Id, v = LayoutSchemaVersion, rev = next, ops = Jsonb.From(operations), now = clock.UtcNow }, ct);
        return new LayoutView(page.Id, next, ETag(next), operations, [], rights.Values.ToList());
    }

    /// <summary>Reset this page to the organization default; other pages and the preference profile stay (FR-027).</summary>
    public async Task ResetLayoutAsync(Guid pageId, CancellationToken ct)
    {
        var page = await pages.PublishedForViewerAsync(pageId, ct);
        await session.ExecAsync("delete from experience.personal_layout where user_id = @u and page_id = @p", new { u = context.RequireUser(), p = page.Id }, ct);
    }

    /// <summary>Reset every page; the preference profile stays (FR-027).</summary>
    public Task ResetAllLayoutsAsync(CancellationToken ct) =>
        session.ExecAsync("delete from experience.personal_layout where user_id = @u", new { u = context.RequireUser() }, ct);

    /// <summary>Components for the add panel of one region, with the reason each ineligible one cannot be added.</summary>
    public async Task<IReadOnlyList<AddPanelItem>> AddPanelAsync(Guid pageId, string? regionId, CancellationToken ct)
    {
        var page = await pages.PublishedForViewerAsync(pageId, ct);
        var template = await templates.GetAsync(page.Definition.Template, ct);
        if (regionId is null || template.Regions.All(r => r.Id != regionId))
        {
            throw AppException.Validation("region", "validation.page.region_unknown");
        }
        var (_, operations) = await layouts.OperationsAsync(context.RequireUser(), page.Id, ct);
        var (regions, rights, defaults) = await ViewerLayoutAsync(page, ct);
        var current = LayoutEngine.Apply(regions, operations, rights, defaults: defaults);
        var region = rights[regionId];
        var check = await NewPlacementCheckAsync(page, ct);
        string? regionReason = !region.Rights.Contains(LayoutEngine.RightAdd) ? "validation.personalization.region_not_delegated"
            : current.Regions[regionId].Count(p => p.Personal) >= region.MaxPersonalPlacements ? "validation.personalization.region_full"
            : null;
        var featureOff = await homeDefaults.FeaturesOffAsync(ct);
        return registry.All.Where(m => m.State != ComponentStates.Retired).Select(m =>
        {
            // R1 adds a component with its defaults; one with required settings and no default waits for personal settings.
            // A component that reads a connection feature is offered only while that feature is on (spec 032 FR-069).
            var reason = regionReason ?? check(new LayoutOperation("panel", "my-check", m.Reference, regionId))
                ?? (m.Requires.ConnectionFeature is { } f && featureOff.Contains(f) ? "validation.personalization.feature_off" : null)
                ?? (PropertyValidator.Validate(m.Properties, null).Count > 0 ? "validation.personalization.needs_setup" : null);
            return new AddPanelItem(m.Reference, m.Display.NameKey, m.Display.DescriptionKey, m.Display.Category, m.Display.Icon, reason is null, reason,
                localizer.Get(m.Display.NameKey, locale.Locale), localizer.Get(m.Display.DescriptionKey, locale.Locale),
                reason is null ? null : localizer.Get(reason, locale.Locale));
        }).ToList();
    }

    // Policy

    public async Task<IReadOnlyList<RegionPolicyView>> GetPolicyAsync(Guid pageId, CancellationToken ct)
    {
        var page = await pages.ForEditorAsync(pageId, Permissions.PersonalizationPolicy, ct);
        return await layouts.PolicyAsync(page.Id, await templates.GetAsync(page.Definition.Template, ct), ct);
    }

    public async Task<IReadOnlyList<RegionPolicyView>> SetPolicyAsync(Guid pageId, PolicyRequest request, CancellationToken ct)
    {
        var page = await pages.ForEditorAsync(pageId, Permissions.PersonalizationPolicy, ct);
        var template = await templates.GetAsync(page.Definition.Template, ct);
        var entitled = await entitlements.IsEnabledAsync(Capabilities.ExperiencePersonalization, ct);
        var audiencesEntitled = await entitlements.IsEnabledAsync(Capabilities.ExperienceAudienceVariants, ct);
        var rules = await audiences.RulesAsync(ct);
        var errors = new ValidationErrors();
        var items = request.Regions ?? [];
        foreach (var (item, i) in items.Select((r, i) => (r, i)))
        {
            var region = template.Regions.FirstOrDefault(r => r.Id == item.RegionId);
            var rights = item.Rights ?? [];
            if (region is null || items.Count(x => x.RegionId == item.RegionId) > 1)
            {
                errors.Add($"regions[{i}].regionId", "validation.page.region_unknown");
                continue;
            }
            if (rights.Any(r => !LayoutEngine.AllRights.Contains(r)) || rights.Distinct().Count() != rights.Count)
            {
                errors.Add($"regions[{i}].rights", "validation.personalization.rights");
            }
            if (rights.Count > 0 && region.Policy.Protection != RegionProtection.Optional)
            {
                // A locked region always wins over personalization (FR-031).
                errors.Add($"regions[{i}].rights", "validation.personalization.region_locked");
            }
            else if (rights.Count > 0 && !LayoutEngine.Delegable(region.Policy, entitled))
            {
                errors.Add($"regions[{i}].rights", "validation.personalization.not_entitled");
            }
            if (item.AudienceRuleId is { } rule && (!audiencesEntitled || !rules.ContainsKey(rule)))
            {
                errors.Add($"regions[{i}].audienceRuleId", audiencesEntitled ? "validation.audience.unknown" : "validation.audience.not_entitled");
            }
            if (item.MaxPersonalPlacements is < 0 or > 24)
            {
                errors.Add($"regions[{i}].maxPersonalPlacements", "validation.personalization.max");
            }
        }
        errors.ThrowIfAny();

        var before = await layouts.PolicyAsync(page.Id, template, ct);
        await session.ExecAsync("delete from experience.personalization_policy where page_id = @p", new { p = page.Id }, ct);
        foreach (var item in items)
        {
            await session.ExecAsync(
                """
                insert into experience.personalization_policy (tenant_id, page_id, region_id, audience_rule_id, rights, max_personal_placements, updated_by, updated_at)
                values (@t, @p, @r, @a, @rights, @max, @by, @now)
                """,
                new
                {
                    t = context.RequireTenant(), p = page.Id, r = item.RegionId, a = item.AudienceRuleId, rights = (item.Rights ?? []).ToArray(),
                    max = item.MaxPersonalPlacements ?? LayoutEngine.DefaultMaxPersonal, by = context.RequireUser(), now = clock.UtcNow,
                }, ct);
        }
        var after = await layouts.PolicyAsync(page.Id, template, ct);
        await audit.WriteAsync(new AuditEntry(AuditActions.PersonalizationPolicyChanged, "page", page.Id.ToString(), TargetOrgUnitId: page.OrgUnitId,
            Changes: new { before, after }), ct);
        return after;
    }

    // Audience rules

    public async Task<IReadOnlyList<AudienceRule>> ListRulesAsync(CancellationToken ct)
    {
        await DemandRuleReaderAsync(ct);
        return (await audiences.RulesAsync(ct)).Values.OrderBy(r => r.Id).ToList();
    }

    public async Task<AudienceRule> SaveRuleAsync(Guid id, AudienceRuleRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceAudienceVariants, ct);
        await authz.DemandAsync(Permissions.PersonalizationPolicy, AuthzTarget.Tenant, ct);
        var errors = new ValidationErrors();
        var labels = (request.Labels ?? new Dictionary<string, string>())
            .Where(l => !string.IsNullOrWhiteSpace(l.Value)).ToDictionary(l => l.Key, l => l.Value.Trim(), StringComparer.Ordinal);
        if (labels.Count == 0 || labels.Any(l => !Locales.IsSupported(l.Key) || l.Value.Length > 120))
        {
            errors.Add("labels", "validation.audience.label");
        }
        AudienceEvaluator.Validate(request.Conditions, errors);
        foreach (var unit in request.Conditions?.OrgUnits ?? [])
        {
            if (!await hierarchy.UnitExistsAsync(unit, ct))
            {
                errors.Add("conditions.orgUnits", "validation.audience.org_unit");
                break;
            }
        }
        errors.ThrowIfAny();
        var before = (await audiences.RulesAsync(ct)).GetValueOrDefault(id);
        await session.ExecAsync(
            """
            insert into experience.audience_rule (tenant_id, rule_id, labels, conditions, updated_by, updated_at)
            values (@t, @id, @labels, @conditions, @by, @now)
            on conflict (tenant_id, rule_id) do update set labels = excluded.labels, conditions = excluded.conditions,
              updated_by = excluded.updated_by, updated_at = excluded.updated_at
            """,
            new { t = context.RequireTenant(), id, labels = Jsonb.From(labels), conditions = Jsonb.From(request.Conditions!), by = context.RequireUser(), now = clock.UtcNow }, ct);
        audiences.Forget();
        var rule = new AudienceRule(id, labels, request.Conditions!);
        await audit.WriteAsync(new AuditEntry(AuditActions.AudienceRuleChanged, "audience_rule", id.ToString(), Changes: new { before, after = rule }), ct);
        return rule;
    }

    /// <summary>
    /// Targeting preview with synthetic personas (FR-028): which rules each persona matches and, for a page, which
    /// placements it sees in each region. Personas are never real users and the preview reads no one's data.
    /// </summary>
    public async Task<IReadOnlyList<PersonaResult>> PreviewAsync(PreviewRequest request, CancellationToken ct)
    {
        await entitlements.DemandAsync(Capabilities.ExperienceAudienceVariants, ct);
        PageContext? page = null;
        if (request.PageId is { } pageId)
        {
            page = await pages.ForEditorAsync(pageId, Permissions.PageEdit, ct);
        }
        else
        {
            await DemandRuleReaderAsync(ct);
        }
        var personas = request.Personas ?? [];
        var errors = new ValidationErrors();
        if (personas.Count is 0 or > 20)
        {
            errors.Add("personas", "validation.audience.personas");
        }
        foreach (var (p, i) in personas.Select((p, i) => (p, i)))
        {
            if (p.OrgUnitId is { } unit && !await hierarchy.UnitExistsAsync(unit, ct))
            {
                errors.Add($"personas[{i}].orgUnitId", "validation.audience.org_unit");
            }
            if (p.MembershipType is { } type && !MembershipTypes.All.Contains(type))
            {
                errors.Add($"personas[{i}].membershipType", "validation.audience.membership_type");
            }
            if (p.Locale is { } l && !Locales.IsSupported(l))
            {
                errors.Add($"personas[{i}].locale", "validation.audience.locale");
            }
        }
        errors.ThrowIfAny();
        var rules = await audiences.RulesAsync(ct);
        var result = new List<PersonaResult>();
        foreach (var (persona, i) in personas.Select((p, i) => (p, i)))
        {
            var facts = await audiences.PersonaAsync(persona.OrgUnitId, persona.MembershipType, persona.Locale, persona.Attributes, ct);
            var matched = rules.Values.Where(r => AudienceMatcher.Matches(r.Conditions, facts)).Select(r => r.Id).OrderBy(x => x).ToList();
            var regions = page is null ? [] : LayoutEngine.ForAudience(page.Definition.Regions, rules, facts)
                .Select(r => new PersonaRegion(r.Id, r.Placements.Select(p => p.Id).ToList())).ToList();
            result.Add(new PersonaResult(string.IsNullOrWhiteSpace(persona.Name) ? $"persona-{i + 1}" : persona.Name[..Math.Min(persona.Name.Length, 80)],
                matched, regions));
        }
        return result;
    }

    // Internals

    private async Task<(IReadOnlyList<PageRegion> Regions, IReadOnlyDictionary<string, RegionRights> Rights,
        IReadOnlyDictionary<string, IReadOnlyList<Placement>> Defaults)> ViewerLayoutAsync(PageContext page, CancellationToken ct)
    {
        var template = await templates.GetAsync(page.Definition.Template, ct);
        var layout = catalogue.Layouts[template.Layout];
        var rules = await audiences.RulesAsync(ct);
        var viewer = await audiences.ViewerAsync(ct);
        var canPersonalize = (await authz.CheckAsync(Permissions.PersonalLayoutEdit, Target(page.OrgUnitId), ct)).Allowed;
        var targeted = LayoutEngine.ForAudience(page.Definition.Regions, rules, viewer);
        var regions = layout.Regions.Select(lr => targeted.FirstOrDefault(r => r.Id == lr.Id) ?? new PageRegion(lr.Id, [])).ToList();
        var rights = await layouts.RightsAsync(page.Id, template, viewer, rules, canPersonalize, ct);
        var defaults = await homeDefaults.ForAsync(template, rights, await availability.ForScopeAsync(page.OrgUnitId, ct, viewer: true), ct);
        return (regions, rights, defaults);
    }

    /// <summary>
    /// The checks a new personal placement must pass, the same as the spec 032 picker: registered and not retired,
    /// enabled, in the package, its permissions held by the user at the page's scope, allowed by the region and wide
    /// enough, with valid properties.
    /// </summary>
    private async Task<Func<LayoutOperation, string?>> NewPlacementCheckAsync(PageContext page, CancellationToken ct)
    {
        var template = await templates.GetAsync(page.Definition.Template, ct);
        var layout = catalogue.Layouts[template.Layout];
        var usable = await availability.ForScopeAsync(page.OrgUnitId, ct);
        return op =>
        {
            var manifest = op.ComponentRef is null ? null : registry.Find(op.ComponentRef);
            if (manifest is null || manifest.State == ComponentStates.Retired)
            {
                return "validation.page.component_unknown";
            }
            var reason = usable(manifest) switch
            {
                Availability.Disabled => "validation.page.component_disabled",
                Availability.NotEntitled => "validation.page.component_not_entitled",
                Availability.NotPermitted => "validation.page.component_not_permitted",
                _ => null,
            };
            if (reason is not null)
            {
                return reason;
            }
            var policy = template.Regions.First(r => r.Id == op.RegionId).Policy;
            if (policy.AllowedCategories.Count > 0 && !policy.AllowedCategories.Contains(manifest.Display.Category))
            {
                return "validation.page.category_not_allowed";
            }
            if (manifest.Placement.MinSpan > layout.Regions.First(r => r.Id == op.RegionId).Span)
            {
                return "validation.page.region_too_narrow";
            }
            if (op.Op == LayoutEngine.Add && PropertyValidator.Validate(manifest.Properties, op.Props).Any())
            {
                return "validation.page.property_invalid";
            }
            return null;
        };
    }

    private async Task DemandRuleReaderAsync(CancellationToken ct)
    {
        if (!await authz.HoldsAnywhereAsync(Permissions.PersonalizationPolicy, ct) && !await authz.HoldsAnywhereAsync(Permissions.PageEdit, ct))
        {
            throw AppException.Denied();
        }
    }

    private static string ETag(int revision) => $"\"l{revision}\"";

    private static AuthzTarget Target(Guid? unit) => unit is { } u ? AuthzTarget.OrgUnit(u) : AuthzTarget.Tenant;

    [GeneratedRegex("^my-[a-z0-9][a-z0-9-]{0,35}$")]
    private static partial Regex PersonalId();
}
