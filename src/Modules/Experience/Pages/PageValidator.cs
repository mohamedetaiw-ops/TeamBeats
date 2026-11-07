using System.Text.Json;
using System.Text.RegularExpressions;
using TeamBeats.Modules.Experience.Catalogue;
using TeamBeats.Modules.Experience.Components;
using TeamBeats.Modules.Experience.Contracts;

namespace TeamBeats.Modules.Experience.Pages;

public sealed record TemplateRef(string Id, string Version);

public sealed record LayoutRef(string Id, string Version);

/// <summary>A placement of a registered component in a region (spec 032 FR-001). Props hold configuration, never business data.</summary>
public sealed record Placement(string Id, string ComponentRef, int? Span = null, JsonElement? Props = null, string? VisibilityRuleRef = null);

public sealed record PageRegion(string Id, IReadOnlyList<Placement> Placements);

/// <summary>The declarative page definition stored in drafts and releases.</summary>
public sealed record PageDefinition(TemplateRef Template, LayoutRef Layout, string Variant, IReadOnlyList<PageRegion> Regions);

/// <summary>One validation finding. Codes are resource keys; region and placement point to the offending part (FR-010).</summary>
public sealed record Finding(string Code, string Severity, string? Region = null, string? Placement = null, string? Property = null, string? Message = null,
    string? Locale = null);

public static class Severities
{
    public const string Blocking = "blocking";
    public const string Warning = "warning";
}

/// <summary>Why a component cannot be used by this editor in this tenant right now.</summary>
public enum Availability
{
    Available,
    Disabled,
    NotEntitled,
    NotPermitted,
}

public enum ValidationMode
{
    Save,
    Publish,
}

/// <summary>
/// The page validator (spec 032 FR-010, FR-012, FR-019, FR-035, section 6 limits). Runs on every draft save and again on publish.
/// </summary>
public static partial class PageValidator
{
    public const int MaxPlacementsPerPage = 40;

    public static List<Finding> Validate(PageDefinition page, string templateId, PlatformCatalogue catalogue, ComponentRegistry registry,
        Func<ComponentManifest, Availability> availability, ValidationMode mode, IReadOnlySet<string>? audienceRules = null,
        bool audiencesEntitled = false)
    {
        var findings = new List<Finding>();
        void Block(string code, string? region = null, string? placement = null, string? property = null) =>
            findings.Add(new Finding("validation.page." + code, Severities.Blocking, region, placement, property));
        void Warn(string code, string? region = null, string? placement = null) =>
            findings.Add(new Finding("validation.page." + code, Severities.Warning, region, placement));

        if (page.Template is null || page.Template.Id != templateId || !catalogue.Templates.TryGetValue(templateId, out var template)
            || page.Template.Version != template.Version)
        {
            Block("template_mismatch");
            return findings;
        }
        var layout = catalogue.Layouts[template.Layout];
        if (page.Layout is null || page.Layout.Id != layout.Id || page.Layout.Version != layout.Version)
        {
            Block("layout_mismatch");
            return findings;
        }
        if (!template.Variants.Contains(page.Variant))
        {
            Block("variant_invalid");
        }

        var regions = page.Regions ?? [];
        var policies = template.Regions.ToDictionary(r => r.Id, r => r.Policy, StringComparer.Ordinal);
        var spans = layout.Regions.ToDictionary(r => r.Id, r => r.Span, StringComparer.Ordinal);
        foreach (var duplicate in regions.GroupBy(r => r.Id).Where(g => g.Count() > 1))
        {
            Block("region_duplicate", duplicate.Key);
        }
        foreach (var region in regions.Where(r => !policies.ContainsKey(r.Id)))
        {
            Block("region_unknown", region.Id);
        }
        foreach (var missing in policies.Keys.Where(id => regions.All(r => r.Id != id)))
        {
            Block("region_missing", missing);
        }

        var all = regions.SelectMany(r => r.Placements ?? []).ToList();
        if (all.Count > MaxPlacementsPerPage)
        {
            Block("too_many_placements_page");
        }
        foreach (var duplicate in all.GroupBy(p => p.Id).Where(g => g.Count() > 1))
        {
            Block("placement_duplicate_id", placement: duplicate.Key);
        }

        var singletons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var region in regions.Where(r => policies.ContainsKey(r.Id)))
        {
            var policy = policies[region.Id];
            var placements = region.Placements ?? [];
            if (placements.Count > policy.MaxPlacements)
            {
                Block("too_many_placements", region.Id);
            }
            if (placements.Count < policy.MinPlacements)
            {
                if (mode == ValidationMode.Publish)
                {
                    Block("too_few_placements", region.Id);
                }
                else
                {
                    Warn("too_few_placements", region.Id);
                }
            }
            if (placements.Any(p => p.VisibilityRuleRef is not null) && placements.All(p => p.VisibilityRuleRef is not null))
            {
                // Spec 008 FR-030: people who match no audience still see this region's default.
                Block("audience_default_missing", region.Id);
            }
            foreach (var p in placements)
            {
                if (p.Id is null || !PlacementId().IsMatch(p.Id))
                {
                    Block("placement_id_invalid", region.Id);
                    continue;
                }
                if (p.ComponentRef is null || registry.Find(p.ComponentRef) is not { } manifest)
                {
                    Block("component_unknown", region.Id, p.Id);
                    continue;
                }
                if (manifest.State == ComponentStates.Retired)
                {
                    Block("component_retired", region.Id, p.Id);
                }
                else if (manifest.State == ComponentStates.Deprecated)
                {
                    Warn("component_deprecated", region.Id, p.Id);
                }
                switch (availability(manifest))
                {
                    case Availability.Disabled: Block("component_disabled", region.Id, p.Id); break;
                    case Availability.NotEntitled: Block("component_not_entitled", region.Id, p.Id); break;
                    case Availability.NotPermitted: Block("component_not_permitted", region.Id, p.Id); break;
                }
                if (policy.AllowedCategories.Count > 0 && !policy.AllowedCategories.Contains(manifest.Display.Category))
                {
                    Block("category_not_allowed", region.Id, p.Id);
                }
                var span = p.Span ?? Math.Min(spans[region.Id], manifest.Placement.MaxSpan);
                if (span < manifest.Placement.MinSpan || span > manifest.Placement.MaxSpan || span > spans[region.Id] || span < 1)
                {
                    Block("span_invalid", region.Id, p.Id);
                }
                if (manifest.Placement.Singleton && !singletons.Add(manifest.Id))
                {
                    Block("singleton_duplicate", region.Id, p.Id);
                }
                foreach (var (property, reason) in PropertyValidator.Validate(manifest.Properties, p.Props))
                {
                    Block(reason == "executable" ? "executable_content" : "property_invalid", region.Id, p.Id, property);
                }
                if (p.VisibilityRuleRef is not null)
                {
                    // Audience rules of spec 008 target placements; they need the package capability and an existing rule.
                    if (!audiencesEntitled)
                    {
                        Block("visibility_rule_not_entitled", region.Id, p.Id);
                    }
                    else if (audienceRules?.Contains(p.VisibilityRuleRef) != true)
                    {
                        Block("visibility_rule_unknown", region.Id, p.Id);
                    }
                }
            }
        }
        return findings;
    }

    /// <summary>An empty page from a template: every region present with no placements (FR-018).</summary>
    public static PageDefinition FromTemplate(TemplateDefinition template, LayoutDefinition layout, string? variant) =>
        new(new TemplateRef(template.Id, template.Version), new LayoutRef(layout.Id, layout.Version), variant ?? "balanced",
            layout.Regions.Select(r => new PageRegion(r.Id, [])).ToList());

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex PlacementId();
}
