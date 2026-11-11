using System.Text.Json;
using TeamBeats.Primitives;

namespace TeamBeats.Modules.Experience.Contracts;

/// <summary>
/// A component manifest (spec 032 FR-024). Modules describe the components they own with a manifest and register it through
/// <see cref="IComponentRegistration"/>; templates, layouts and the builder never name a concrete component.
/// </summary>
public sealed record ComponentManifest(
    string Id,
    string Version,
    string Publisher,
    string Compatibility,
    ComponentDisplay Display,
    IReadOnlyDictionary<string, PropertySchema> Properties,
    PlacementRules Placement,
    IReadOnlyList<string> Context,
    ComponentRequirements Requires,
    ComponentEvents Events,
    IReadOnlyList<string> Data,
    string Trust = TrustTiers.Platform,
    string State = ComponentStates.Active)
{
    /// <summary>The reference pages use, <c>id@major</c>.</summary>
    public string Reference => $"{Id}@{Major}";

    public int Major => int.Parse(Version.Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>Display metadata as resource keys, never literal text (CLAUDE.md rule 8).</summary>
public sealed record ComponentDisplay(string NameKey, string DescriptionKey, string Category, string Icon);

/// <summary>
/// One typed property (spec 032 FR-025). A restricted subset of JSON Schema: no type accepts raw HTML.
/// </summary>
public sealed record PropertySchema(
    string Type,
    bool Required = false,
    JsonElement? Default = null,
    double? Minimum = null,
    double? Maximum = null,
    int? MinLength = null,
    int? MaxLength = null,
    IReadOnlyList<string>? Enum = null,
    PropertySchema? Items = null,
    int? MaxItems = null);

/// <summary>Placement capabilities: column spans the component supports and whether it may appear once per page only.</summary>
public sealed record PlacementRules(int MinSpan = 1, int MaxSpan = 12, bool Singleton = false);

/// <summary>Capabilities and permissions a component needs, and the connection feature it reads (spec 032 FR-069).</summary>
public sealed record ComponentRequirements(IReadOnlyList<string> Capabilities, IReadOnlyList<string> Permissions, string? ConnectionFeature = null)
{
    public static readonly ComponentRequirements None = new([], []);
}

public sealed record ComponentEvent(string Name, IReadOnlyDictionary<string, PropertySchema> Payload);

/// <summary>Events a component publishes and subscribes to on the page channel (spec 032 FR-067).</summary>
public sealed record ComponentEvents(IReadOnlyList<ComponentEvent> Publishes, IReadOnlyList<ComponentEvent> Subscribes)
{
    public static readonly ComponentEvents None = new([], []);
}

public static class TrustTiers
{
    /// <summary>Built and reviewed by TeamBeats; the only tier before R3 (spec 032 FR-031).</summary>
    public const string Platform = "platform";
}

public static class ComponentStates
{
    public const string Active = "active";
    public const string Deprecated = "deprecated";
    public const string Retired = "retired";
}

/// <summary>Context values a component may ask for (spec 032 FR-028). Anything else is never supplied.</summary>
public static class ComponentContext
{
    public static readonly IReadOnlySet<string> Approved = new HashSet<string>(StringComparer.Ordinal)
    {
        "tenantId", "orgUnitId", "siteId", "pageId", "locale", "direction", "themeMode", "density", "deviceClass",
    };
}

/// <summary>Contributes component manifests at start. Each module registers its own components.</summary>
public interface IComponentRegistration
{
    IEnumerable<ComponentManifest> Manifests();

    /// <summary>
    /// References (<c>id@major</c>) of this module's components that the default home offers in its personal region
    /// (spec 008 FR-051), in order. They appear as personal placements while the viewer may use them, and the viewer can
    /// remove, reorder or re-add them. Templates never name components (spec 032 FR-014), so modules suggest them here.
    /// </summary>
    IEnumerable<string> PersonalHomeDefaults() => [];

    /// <summary>Migration hooks from one major version of this module's components to the next (spec 032 FR-030).</summary>
    IEnumerable<ComponentMigration> Migrations() => [];
}

/// <summary>
/// A manifest migration hook (spec 032 FR-030): converts a placement's properties written for <c>id@FromMajor</c> into
/// properties for <c>id@ToMajor</c>. The hook sees only the placement's own properties, never page or tenant data, and its
/// result is validated against the new manifest before any page is changed. Only the next major is supported.
/// </summary>
public sealed record ComponentMigration(string Id, int FromMajor, int ToMajor, Func<JsonElement?, JsonElement?> Convert)
{
    public string From => $"{Id}@{FromMajor}";

    public string To => $"{Id}@{ToMajor}";
}

public sealed record PagePublished(Guid PageId, int Release) : IIntegrationEvent;

public sealed record PageRolledBack(Guid PageId, int Release, int RestoredRelease) : IIntegrationEvent;

/// <summary>A tenant template version was released (spec 032 section 12); owners of pages on older versions may move to it.</summary>
public sealed record TemplateReleased(string TemplateId, int Version) : IIntegrationEvent;

/// <summary>Released versions of a tenant template were deprecated: their pages keep working, new pages cannot use them.</summary>
public sealed record TemplateDeprecated(string TemplateId, IReadOnlyList<int> Versions) : IIntegrationEvent;

// Spec 033 section 12. A null scope means the tenant.
public sealed record ThemePublished(Guid BrandId, int Version) : IIntegrationEvent;

public sealed record BrandAssignmentChanged(Guid? ScopeOrgUnitId, Guid? BrandId) : IIntegrationEvent;

public sealed record ExperienceLockChanged(Guid? ScopeOrgUnitId, IReadOnlyList<string> Settings) : IIntegrationEvent;

// Spec 008 section 12.
public sealed record ShellPublished(int Version) : IIntegrationEvent;

public sealed record NavigationPublished(Guid MenuId, string Kind, string Scope, int Version) : IIntegrationEvent;

/// <summary>How many pages use one template version, and how many of them are published (spec 021 adoption basics, spec 032 inventory).</summary>
public sealed record TemplateUsage(string TemplateId, string TemplateVersion, int Pages, int Published);

/// <summary>Template adoption for analytics (spec 021 T007): counts only, for the whole tenant.</summary>
public interface ITemplateUsage
{
    Task<IReadOnlyList<TemplateUsage>> UsageAsync(CancellationToken ct = default);
}

/// <summary>
/// Whether the caller may open at least one administration section (spec 006 US4, spec 008 FR-062), so the shell offers an
/// Administration entry. The administration module answers with the same checks that build its own navigation; the entry
/// is only a way in, and every section still authorizes itself.
/// </summary>
public interface IAdministrationEntry
{
    Task<bool> CanOpenAsync(CancellationToken ct = default);
}
